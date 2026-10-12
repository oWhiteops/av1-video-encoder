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
    /// <param name="started">Called with each attempt's job, so a UI can pause and resume it.</param>
    public static async Task<EncodeResult> EncodeToSizeAsync(FfmpegTools tools, EncodeRequest template, MediaInfo media, long maxBytes,
        int maxAttempts = 3, IProgress<EncodeProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default,
        double margin = 0.05, int floorKbps = MinVideoKbps, Action<EncodeJob>? started = null)
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
                var job = new EncodeJob(tools, attemptRequest, media, log);
                started?.Invoke(job);
                var result = await job.RunAsync(attemptProgress, ct);
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
        bool whole = length <= SampleLength * SamplePositions.Length;
        (TimeSpan Start, TimeSpan Length)[] samples = whole
            ? [(Start: start, Length: length)]
            : SamplePositions.Select(p => (Start: start + length * p - SampleLength / 2, Length: SampleLength)).ToArray();

        static string Secs(TimeSpan t) => t.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        async Task<(double Seconds, FfmpegExit Exit)> Run(TimeSpan at, string[] limit, string output)
        {
            string[] args =
            [
                "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-ss", Secs(at), "-i", media.Path, .. limit,
                "-map", $"0:{video.Index}", "-an", "-sn", "-dn",
                "-c:v", "libsvtav1", "-preset", "8", "-crf", "32", "-pix_fmt", "yuv420p10le", "-f", "matroska", output,
            ];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var exit = await new FfmpegProcess().RunAsync(tools.FfmpegPath, args, null, null, ct);
            return (clock.Elapsed.TotalSeconds, exit);
        }

        // Each run pays a fixed start-up cost (launch, seek, decoder and encoder set-up): 0.2 s to 1.5 s per run in the
        // estimate validation, depending on the source. Timing a 1-frame encode at the first sample measures it, so
        // the samples' speed reflects encoding alone; without this, time estimates came out up to 2.9x too long.
        double overhead;
        var probe = Path.Combine(Path.GetTempPath(), $"av1-analysis-{Guid.NewGuid():N}.mkv");
        try { overhead = (await Run(samples[0].Start, ["-frames:v", "1"], probe)).Seconds; }
        finally { try { File.Delete(probe); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        var rates = new List<double>();
        double encodeSeconds = 0, frames = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            var (at, len) = samples[i];
            var temp = Path.Combine(Path.GetTempPath(), $"av1-analysis-{Guid.NewGuid():N}.mkv");
            try
            {
                var (seconds, exit) = await Run(at, ["-t", Secs(len)], temp);
                encodeSeconds += Math.Max(seconds - overhead, seconds * 0.1);
                frames += len.TotalSeconds * Math.Max(video.FrameRate, 1);
                if (exit.ExitCode != 0 || !File.Exists(temp))
                    throw new EncodeException("The quality check could not read this video.", string.Join(Environment.NewLine, exit.StderrTail));
                double bytes = new FileInfo(temp).Length;
                // Every sample starts with a keyframe, but a full encode has one only every GOP. On static footage
                // (screen recordings) that keyframe is most of a 2 s sample, so the estimate came out 1.94x too high
                // in calibration. Charge the sample its share of one keyframe per GOP instead.
                double gop = KeyframeIntervalSeconds(video.FrameRate);
                if (!whole && len.TotalSeconds < gop && await FirstVideoPacketBytesAsync(tools, temp, ct) is long keyframe)
                    bytes += keyframe * (len.TotalSeconds / gop - 1);
                rates.Add(bytes * 8 / 1000.0 / len.TotalSeconds);
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            progress?.Report((i + 1.0) / samples.Length);
        }
        return new ComplexityEstimate(rates.Average(), rates.Max(), IsRough: false, Preset8Fps: frames / Math.Max(encodeSeconds, 0.001));
    }

    /// <summary>
    /// Measures a hardware encoder directly: encodes three 2 s samples (or a short video whole) with the request's exact
    /// settings and returns the video bitrate and speed they reached. The software model doesn't apply to GPUs: their
    /// files are 40-75% larger at the same quality and their speed depends on the card. Throws <see cref="EncodeException"/>
    /// when the encoder can't encode this video, which is worth knowing before starting.
    /// </summary>
    public static TimeSpan HardwareSampleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public static async Task<HardwareSample> SampleHardwareAsync(FfmpegTools tools, MediaInfo media, EncodeRequest request,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var video = media.Video ?? throw new EncodeException("This file does not contain a video stream.");
        double fps = Math.Max(video.FrameRate, 1);
        var length = request.OutputDuration(media);
        var start = request.TrimStart ?? TimeSpan.Zero;
        bool whole = length <= SampleLength * SamplePositions.Length;
        (TimeSpan Start, TimeSpan Length)[] samples = whole
            ? [(start, length)]
            : SamplePositions.Select(p => (start + length * p - SampleLength / 2, SampleLength)).ToArray();

        // GPU drivers sometimes hang during initialization (AMF: a 48-byte output and no progress) or fail to start
        // intermittently; a 2 s sample never legitimately takes 30 s, so a stuck run is stopped and retried once.
        async Task<(double Seconds, long Bytes)> Run(TimeSpan at, TimeSpan len)
        {
            try { return await RunOnce(at, len); }
            catch (EncodeException) { return await RunOnce(at, len); }
        }

        async Task<(double Seconds, long Bytes)> RunOnce(TimeSpan at, TimeSpan len)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"av1-gpu-sample-{Guid.NewGuid():N}.mp4");
            var sample = request with
            {
                TrimStart = at, TrimEnd = at + len, IncludeAudio = false, IncludeSubtitles = false, OutputPath = "sample.mp4",
                MaxOutputBytes = null, TwoPass = false,
            };
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(HardwareSampleTimeout);
                FfmpegExit exit;
                try { exit = await new FfmpegProcess().RunAsync(tools.FfmpegPath, FfmpegArguments.Build(media, sample, request.Encoder, temp), null, null, limit.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new EncodeException($"The {HardwareDetector.DisplayName(request.Encoder)} encoder stopped responding.");
                }
                double seconds = clock.Elapsed.TotalSeconds;
                if (exit.ExitCode != 0 || !File.Exists(temp))
                    throw new EncodeException(EncodeJob.Explain(exit.StderrTail, request.Encoder), string.Join(Environment.NewLine, exit.StderrTail));
                return (seconds, new FileInfo(temp).Length);
            }
            finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        // Start-up (device and session set-up, seek, decode) is timed with a 1-frame encode and subtracted, as for SVT-AV1.
        // Two frames' worth: one frame's duration can round below a frame (1/30 s -> "0.033"), giving the encoder no
        // frames at all, and AMF then spins forever waiting for input.
        double overhead = (await Run(samples[0].Start, TimeSpan.FromSeconds(2 / fps))).Seconds;
        double seconds = 0, bytes = 0, frames = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            var (at, len) = samples[i];
            var (s, b) = await Run(at, len);
            seconds += Math.Max(s - overhead, s * 0.1);
            bytes += b;
            frames += len.TotalSeconds * fps;
            progress?.Report((i + 1.0) / samples.Length);
        }
        double sampled = samples.Sum(x => x.Length.TotalSeconds);
        return new HardwareSample(request.Encoder, request.Quality, bytes * 8 / 1000.0 / sampled, frames / Math.Max(seconds, 0.001));
    }

    /// <summary>SVT-AV1's default keyframe spacing with this build: 321 frames at 50+ fps, 161 below (measured).</summary>
    public static double KeyframeIntervalSeconds(double fps) => fps <= 0 ? 5.37 : (fps >= 50 ? 321 : 161) / fps;

    static async Task<long?> FirstVideoPacketBytesAsync(FfmpegTools tools, string path, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(tools.FfprobePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=size", "-read_intervals", "%+#1", "-of", "csv=p=0", path })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return long.TryParse(stdout.Split('\n')[0].Trim(), out var size) ? size : null;
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
