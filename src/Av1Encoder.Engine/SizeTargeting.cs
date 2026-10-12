namespace Av1Encoder.Engine;

/// <summary>
/// Encodes to a maximum file size (the basis for the planned "Video to Discord" app). The bitrate estimate is only a
/// starting point: every attempt's real size is checked, and the bitrate is lowered and the encode retried if needed.
/// </summary>
public static class SizeTargeting
{
    /// <summary>Lowest video bitrate considered watchable; below this the target cannot sensibly be met.</summary>
    public const int MinVideoKbps = 50;

    /// <summary>Video bitrate (kbps) that should fit <paramref name="maxBytes"/>, leaving headroom for container overhead.</summary>
    public static int CalculateVideoBitrateKbps(long maxBytes, TimeSpan duration, int totalAudioKbps, double headroom = 0.95)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        double totalKbps = maxBytes * 8 / 1000.0 / duration.TotalSeconds * headroom;
        return (int)Math.Floor(totalKbps - totalAudioKbps);
    }

    public static int TotalAudioKbps(MediaInfo media, EncodeRequest request) =>
        request.IncludeAudio ? media.Audio.Sum(a => FfmpegArguments.AudioBitrateKbps(a, request)) : 0;

    public static async Task<EncodeResult> EncodeToSizeAsync(FfmpegTools tools, EncodeRequest template, MediaInfo media, long maxBytes,
        int maxAttempts = 3, IProgress<EncodeProgress>? progress = null, Action<string>? log = null, CancellationToken ct = default)
    {
        // Always re-encode audio here so its bitrate is known rather than whatever the source used.
        var request = template with { AudioBitrateKbps = template.AudioBitrateKbps ?? 128 };
        int kbps = CalculateVideoBitrateKbps(maxBytes, media.Duration, TotalAudioKbps(media, request));
        for (int attempt = 1; ; attempt++)
        {
            if (kbps < MinVideoKbps)
                throw new EncodeException($"The video is too long to fit in {maxBytes / 1048576.0:0.#} MB at a usable quality.");
            var attemptRequest = request with
            {
                Quality = request.Quality with { VideoBitrateKbps = kbps },
                OverwriteExisting = template.OverwriteExisting || attempt > 1, // later attempts replace our own earlier output
            };
            log?.Invoke($"Size target {maxBytes:N0} bytes, attempt {attempt}: video {kbps} kbps.");
            var result = await new EncodeJob(tools, attemptRequest, media, log).RunAsync(progress, ct);
            if (result.OutputBytes <= maxBytes) return result;
            if (attempt >= maxAttempts)
            {
                File.Delete(result.OutputPath);
                throw new EncodeException($"Could not get the video under {maxBytes / 1048576.0:0.#} MB after {attempt} attempts.");
            }
            kbps = (int)(kbps * (double)maxBytes / result.OutputBytes * 0.95);
        }
    }
}
