namespace Av1Encoder.Engine;

public sealed record EncoderAvailability(VideoEncoder Encoder, bool Available, string Diagnostic);

public static class HardwareDetector
{
    public static readonly VideoEncoder[] HardwareEncoders = [VideoEncoder.Amf, VideoEncoder.Nvenc, VideoEncoder.Qsv];

    public static string FfmpegName(VideoEncoder e) => e switch
    {
        VideoEncoder.SvtAv1 => "libsvtav1", VideoEncoder.Amf => "av1_amf", VideoEncoder.Nvenc => "av1_nvenc", VideoEncoder.Qsv => "av1_qsv",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    public static string DisplayName(VideoEncoder e) => e switch
    {
        VideoEncoder.SvtAv1 => "Software (SVT-AV1)", VideoEncoder.Amf => "AMD (AMF)", VideoEncoder.Nvenc => "NVIDIA (NVENC)", VideoEncoder.Qsv => "Intel (Quick Sync)",
        _ => e.ToString(),
    };

    /// <summary>
    /// Checks each hardware AV1 encoder by actually encoding a few frames: an encoder being compiled into FFmpeg
    /// says nothing about whether this GPU and driver can run it.
    /// </summary>
    public static async Task<IReadOnlyList<EncoderAvailability>> DetectAsync(FfmpegTools tools, CancellationToken ct = default)
    {
        var checks = HardwareEncoders.Select(async e =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var exit = await new FfmpegProcess().RunAsync(tools.FfmpegPath,
                    ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=640x360:r=30:d=0.5",
                     "-c:v", FfmpegName(e), "-f", "null", "-"], null, null, timeout.Token);
                return exit.ExitCode == 0
                    ? new EncoderAvailability(e, true, "OK")
                    : new EncoderAvailability(e, false, exit.StderrTail.FirstOrDefault(l => l.Length > 0)?.Trim() ?? $"exit code {exit.ExitCode}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new EncoderAvailability(e, false, "Timed out during initialization.");
            }
        });
        return await Task.WhenAll(checks);
    }
}
