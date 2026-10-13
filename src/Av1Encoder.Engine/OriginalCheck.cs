namespace Av1Encoder.Engine;

/// <summary>
/// Decides whether a finished encode is trustworthy enough to delete its source: it must be the whole video, clearly
/// smaller, the same length, and decode from start to end without a single error.
/// </summary>
public static class OriginalCheck
{
    /// <summary>The new file must be at most this share of the source's size to count as "compressed well".</summary>
    public const double MaxSizeRatio = 0.9;

    /// <summary>Why the original must be kept, judged from the encode's own facts; null when it may go.</summary>
    public static string? Reason(MediaInfo source, EncodeRequest request, EncodeResult result)
    {
        if (request.TrimStart != null || request.TrimEnd != null) return "only part of it was encoded";
        if (string.Equals(Path.GetFullPath(source.Path), Path.GetFullPath(result.OutputPath), StringComparison.OrdinalIgnoreCase))
            return "the new file has the same name";
        if (result.Output.Video?.Codec != "av1") return "the new file has no AV1 video";
        if (source.SizeBytes <= 0 || result.OutputBytes > source.SizeBytes * MaxSizeRatio)
            return $"the new file is not at least {(1 - MaxSizeRatio) * 100:0}% smaller";
        double drift = Math.Abs((result.Output.Duration - source.Duration).TotalSeconds);
        if (drift > Math.Max(2, source.Duration.TotalSeconds * 0.01))
            return $"the new file's length ({result.Output.Duration:h\\:mm\\:ss}) differs from the original's ({source.Duration:h\\:mm\\:ss})";
        if (source.Audio.Count > 0 && request.IncludeAudio && result.Output.Audio.Count == 0) return "the new file has no audio";
        return null;
    }

    /// <summary>
    /// <see cref="Reason"/>, then a full decode of the saved file (video with dav1d, plus audio) that fails on the first
    /// error. Reports decode progress as 0-1. Null when the original may be deleted.
    /// </summary>
    public static async Task<string?> VerifyAsync(FfmpegTools tools, MediaInfo source, EncodeRequest request, EncodeResult result,
        Action<double>? progress, Action<string>? log, CancellationToken ct)
    {
        if (Reason(source, request, result) is { } reason) return reason;
        var file = new FileInfo(result.OutputPath);
        if (!file.Exists || file.Length != result.OutputBytes) return "the new file changed after encoding";
        string[] args = ["-hide_banner", "-nostdin", "-v", "error", "-xerror", "-c:v", "libdav1d", "-i", result.OutputPath,
            "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-progress", "pipe:1", "-nostats", "-"];
        log?.Invoke("Checking: " + FfmpegArguments.ToDisplayString("ffmpeg", args));
        double total = Math.Max(result.Output.Duration.TotalSeconds, 1);
        var exit = await new FfmpegProcess().RunAsync(tools.FfmpegPath, args,
            p => { if (p.OutTime is { } t) progress?.Invoke(Math.Clamp(t.TotalSeconds / total, 0, 1)); },
            line => log?.Invoke("ffmpeg: " + line), ct);
        return exit.ExitCode == 0 && exit.StderrTail.Count == 0 ? null : "the new file did not decode cleanly (see the log)";
    }
}
