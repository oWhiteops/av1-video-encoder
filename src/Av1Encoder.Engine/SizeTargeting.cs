namespace Av1Encoder.Engine;

/// <summary>
/// Encodes to a maximum file size (Video to Discord). The bitrate estimate is only a starting point: every attempt's
/// real size is checked on the temp file, and the bitrate is lowered and the encode retried if needed.
/// </summary>
public static class SizeTargeting
{
    /// <summary>Lowest video bitrate considered at all, whatever the resolution. Callers pass a resolution-aware floor.</summary>
    public const int MinVideoKbps = 50;

    /// <summary>Video bitrate (kbps) that should fit <paramref name="maxBytes"/>, leaving headroom for container overhead.</summary>
    public static int CalculateVideoBitrateKbps(long maxBytes, TimeSpan duration, int totalAudioKbps, double headroom = 0.95)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        double totalKbps = maxBytes * 8 / 1000.0 / duration.TotalSeconds * headroom;
        return (int)Math.Floor(totalKbps - totalAudioKbps);
    }

    public static int TotalAudioKbps(MediaInfo media, EncodeRequest request) =>
        FfmpegArguments.KeptAudio(media, request).Sum(a => FfmpegArguments.AudioBitrateKbps(a, request));

    /// <summary>
    /// Video bitrate for the next attempt after one came out at <paramref name="actualBytes"/>. Audio is a fixed cost,
    /// so only the video share is scaled; scaling the whole file would under-correct when audio is a large share.
    /// </summary>
    public static int NextVideoKbps(int kbps, long actualBytes, long aimBytes, int audioKbps, TimeSpan duration)
    {
        double audioBytes = audioKbps * 1000 / 8.0 * duration.TotalSeconds;
        double videoActual = Math.Max(1, actualBytes - audioBytes), videoAim = aimBytes - audioBytes;
        return (int)Math.Floor(kbps * videoAim / videoActual * 0.97);
    }

    /// <param name="margin">Share of the limit held back for container overhead and encoder variance.</param>
    /// <param name="floorKbps">Quality floor: no attempt is made below this video bitrate.</param>
    public static async Task<EncodeResult> EncodeToSizeAsync(FfmpegTools tools, EncodeRequest template, MediaInfo media, long maxBytes,
        int maxAttempts = 3, IProgress<EncodeProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default,
        double margin = 0.05, int floorKbps = MinVideoKbps)
    {
        // Always re-encode audio here so its bitrate is known rather than whatever the source used.
        var request = template with { AudioBitrateKbps = template.AudioBitrateKbps ?? 128 };
        var duration = request.OutputDuration(media);
        int audioKbps = TotalAudioKbps(media, request);
        long aim = (long)(maxBytes * (1 - margin));
        int kbps = CalculateVideoBitrateKbps(maxBytes, duration, audioKbps, 1 - margin);
        long? lastBytes = null;
        for (int attempt = 1; ; attempt++)
        {
            if (kbps < Math.Max(floorKbps, MinVideoKbps))
                throw new TargetUnreachableException(maxBytes, kbps, Math.Max(floorKbps, MinVideoKbps), lastBytes, attempt - 1);
            // Oversized attempts are rejected inside EncodeJob before finalizing, so the destination (and any existing
            // file there) is only ever touched by an output that fits; retries never need to overwrite anything.
            var attemptRequest = request with { Quality = request.Quality with { VideoBitrateKbps = kbps }, MaxOutputBytes = maxBytes };
            log?.Invoke($"Size target {maxBytes:N0} bytes, attempt {attempt} of {maxAttempts}: video {kbps} kbps, audio {audioKbps} kbps.");
            var attemptProgress = attempt == 1 || progress is null ? progress : new Relabel(progress, $"Attempt {attempt} of {maxAttempts}: ");
            try
            {
                var result = await new EncodeJob(tools, attemptRequest, media, log).RunAsync(attemptProgress, ct);
                return result with { Attempts = attempt };
            }
            catch (OutputTooLargeException ex) when (attempt < maxAttempts)
            {
                log?.Invoke(ex.Message);
                lastBytes = ex.ActualBytes;
                kbps = NextVideoKbps(kbps, ex.ActualBytes, aim, audioKbps, duration);
            }
            catch (OutputTooLargeException ex)
            {
                throw new TargetUnreachableException(maxBytes, kbps, floorKbps, ex.ActualBytes, attempt);
            }
        }
    }

    /// <summary>Sample positions within the selection, and each sample's length.</summary>
    public static readonly double[] SamplePositions = [0.2, 0.5, 0.8];
    public static readonly TimeSpan SampleLength = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Measures how hard the selected part is to compress: encodes short samples at CRF 32, SVT-AV1 preset 8, source
    /// resolution and frame rate, and returns the bitrate they needed. Seconds of work; cancellable. A short selection
    /// is measured whole. Samples go to the temp folder and are always deleted.
    /// </summary>
    public static async Task<ComplexityEstimate> AnalyzeAsync(FfmpegTools tools, MediaInfo media, TimeSpan? trimStart, TimeSpan? trimEnd,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var video = media.Video ?? throw new EncodeException("This file does not contain a video stream.");
        var start = trimStart ?? TimeSpan.Zero;
        var length = new EncodeRequest { InputPath = media.Path, OutputPath = "", Quality = QualitySettings.For(QualityPreset.Balanced), TrimStart = trimStart, TrimEnd = trimEnd }
            .OutputDuration(media);
        // ponytail: each sample starts on a keyframe, so samples read somewhat high against a full encode; the
        // calibration study measures and corrects that bias rather than guessing it here.
        (TimeSpan Start, TimeSpan Length)[] samples = length <= SampleLength * SamplePositions.Length
            ? [(Start: start, Length: length)]
            : SamplePositions.Select(p => (Start: start + length * p - SampleLength / 2, Length: SampleLength)).ToArray();

        var rates = new List<double>();
        for (int i = 0; i < samples.Length; i++)
        {
            var (at, len) = samples[i];
            var temp = Path.Combine(Path.GetTempPath(), $"av1-analysis-{Guid.NewGuid():N}.mkv");
            try
            {
                string[] args =
                [
                    "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                    "-ss", at.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", media.Path,
                    "-t", len.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-map", $"0:{video.Index}", "-an", "-sn", "-dn",
                    "-c:v", "libsvtav1", "-preset", "8", "-crf", "32", "-pix_fmt", "yuv420p10le", "-f", "matroska", temp,
                ];
                var exit = await new FfmpegProcess().RunAsync(tools.FfmpegPath, args, null, null, ct);
                if (exit.ExitCode != 0 || !File.Exists(temp))
                    throw new EncodeException("The quality check could not read this video.", string.Join(Environment.NewLine, exit.StderrTail));
                rates.Add(new FileInfo(temp).Length * 8 / 1000.0 / len.TotalSeconds);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            progress?.Report((i + 1.0) / samples.Length);
        }
        return new ComplexityEstimate(rates.Average(), rates.Max(), IsRough: false);
    }

    /// <summary>Prefixes the stage text, passing reports straight through (no extra thread hop or reordering).</summary>
    sealed class Relabel(IProgress<EncodeProgress> inner, string prefix) : IProgress<EncodeProgress>
    {
        public void Report(EncodeProgress value) => inner.Report(value with { Stage = prefix + value.Stage });
    }
}

/// <summary>
/// The size target can't be met above the quality floor, or attempts ran out. Nothing was saved. The UI turns this into
/// concrete alternatives (trim, lower resolution) from the planner.
/// </summary>
public sealed class TargetUnreachableException(long maxBytes, int neededKbps, int floorKbps, long? lastAttemptBytes, int attempts)
    : EncodeException(attempts > 0 && lastAttemptBytes is long last && neededKbps >= floorKbps
        ? $"Couldn't get the video under {maxBytes / 1048576.0:0.#} MB after {attempts} attempts (last try {last / 1048576.0:0.0} MB)."
        : $"The video can't fit in {maxBytes / 1048576.0:0.#} MB without going below the quality floor"
          + (lastAttemptBytes is long l ? $" (last try {l / 1048576.0:0.0} MB)." : "."))
{
    public long MaxBytes { get; } = maxBytes;
    /// <summary>The video bitrate the next attempt would have needed.</summary>
    public int NeededKbps { get; } = neededKbps;
    public int FloorKbps { get; } = floorKbps;
    public long? LastAttemptBytes { get; } = lastAttemptBytes;
    public int Attempts { get; } = attempts;
}
