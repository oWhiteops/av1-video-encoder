namespace Av1Encoder.Engine;

public enum QualityPreset { HighQuality, Balanced, SmallestFile }

/// <summary>Every encoder here produces AV1. SVT-AV1 is the software default; the rest are hardware paths.</summary>
public enum VideoEncoder { SvtAv1, Amf, Nvenc, Qsv }

public enum OutputContainer { Mp4, Mkv }

/// <summary>
/// Quality knobs for one encode. <see cref="Crf"/> (0-63, lower = better) and <see cref="Speed"/> (SVT-AV1 preset 0-13,
/// lower = slower/more efficient) drive SVT-AV1. <see cref="HardwareQuality"/> (1-51, lower = better) drives the
/// hardware encoders' constant-quality modes. Setting <see cref="VideoBitrateKbps"/> switches to bitrate-targeted
/// encoding, which is what size-limited outputs need.
/// </summary>
public sealed record QualitySettings(int Crf, int Speed, int HardwareQuality, int? VideoBitrateKbps = null)
{
    /// <summary>
    /// Defaults chosen from VMAF/size/speed measurements; see docs/PRESETS.md for the data and reasoning.
    /// </summary>
    public static QualitySettings For(QualityPreset preset) => preset switch
    {
        QualityPreset.HighQuality => new(Crf: 26, Speed: 4, HardwareQuality: 20),
        QualityPreset.Balanced => new(Crf: 32, Speed: 5, HardwareQuality: 26),
        QualityPreset.SmallestFile => new(Crf: 40, Speed: 4, HardwareQuality: 32),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    /// <summary>Maps an SVT-AV1 CRF onto the hardware 1-51 quality scale, for custom quality on hardware.</summary>
    /// Fitted to the presets: CRF 26/32/40 -> 20/26/32 matched VMAF between SVT-AV1 and AMF in benchmarks.
    public static int HardwareQualityFromCrf(int crf) => Math.Clamp((int)Math.Round(2 + crf * 0.75), 1, 51);
}

public sealed record EncodeRequest
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required QualitySettings Quality { get; init; }
    public VideoEncoder Encoder { get; init; } = VideoEncoder.SvtAv1;
    public bool IncludeAudio { get; init; } = true;
    public bool IncludeSubtitles { get; init; } = true;
    /// <summary>Per-stream AAC bitrate override. Null picks a channel-count based default.</summary>
    public int? AudioBitrateKbps { get; init; }
    /// <summary>Extra SVT-AV1 parameters (key=value:key=value). Advanced; normally empty.</summary>
    public string? SvtParams { get; init; }
    public bool OverwriteExisting { get; init; }
    /// <summary>Retry with SVT-AV1 if a hardware encoder fails to initialize or errors.</summary>
    public bool AllowSoftwareFallback { get; init; } = true;
    /// <summary>Hard size limit. A temp output over it is deleted before it can reach <see cref="OutputPath"/>.</summary>
    public long? MaxOutputBytes { get; init; }

    // The options below default to "leave the source as it is", which is what Compress mode uses.

    /// <summary>Encode only this part of the source.</summary>
    public TimeSpan? TrimStart { get; init; }
    public TimeSpan? TrimEnd { get; init; }
    /// <summary>Downscale so the displayed short side is at most this (720 = "720p", portrait included). Never upscales.</summary>
    public int? MaxShortSide { get; init; }
    /// <summary>Lower the frame rate to this when the source is faster. Never raises it.</summary>
    public double? MaxFrameRate { get; init; }
    /// <summary>Source audio stream indexes to keep; null keeps all.</summary>
    public IReadOnlyList<int>? AudioStreamIndexes { get; init; }
    /// <summary>Downmix tracks with more than two channels to stereo.</summary>
    public bool DownmixToStereo { get; init; }
    /// <summary>Two-pass SVT-AV1 when a bitrate is set: lands much closer to the target size than one pass.</summary>
    public bool TwoPass { get; init; }

    /// <summary>Length of the output: the trimmed range, or the whole source.</summary>
    public TimeSpan OutputDuration(MediaInfo media)
    {
        var start = TrimStart ?? TimeSpan.Zero;
        var end = TrimEnd is { } e && e < media.Duration ? e : media.Duration;
        return end > start ? end - start : TimeSpan.Zero;
    }

    public OutputContainer Container =>
        Path.GetExtension(OutputPath).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? OutputContainer.Mkv : OutputContainer.Mp4;
}

public sealed record EncodeProgress(
    double Fraction, TimeSpan Elapsed, TimeSpan? Remaining, double? Speed, double? Fps, long? EstimatedOutputBytes, string Stage);

public sealed record EncodeResult(
    string OutputPath, long InputBytes, long OutputBytes, TimeSpan EncodeTime, VideoEncoder EncoderUsed,
    bool FellBackToSoftware, IReadOnlyList<string> Warnings, MediaInfo Output)
{
    /// <summary>Encode attempts it took (size-targeted encodes may retry).</summary>
    public int Attempts { get; init; } = 1;
    public long SavedBytes => InputBytes - OutputBytes;
    public double SavedPercent => InputBytes > 0 ? 100.0 * SavedBytes / InputBytes : 0;
}

public class EncodeException(string message, string? details = null) : Exception(message)
{
    /// <summary>Technical detail (FFmpeg output) for the log, not for the headline message.</summary>
    public string? Details { get; } = details;
}

/// <summary>The encode finished but was over <see cref="EncodeRequest.MaxOutputBytes"/>; nothing was saved.</summary>
public sealed class OutputTooLargeException(long actualBytes, long maxBytes)
    : EncodeException($"The encoded file was {actualBytes:N0} bytes, over the {maxBytes:N0}-byte limit, and was discarded.")
{
    public long ActualBytes { get; } = actualBytes;
    public long MaxBytes { get; } = maxBytes;
}

public sealed class FfmpegTools(string directory)
{
    public string Directory { get; } = directory;
    public string FfmpegPath => Path.Combine(Directory, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(Directory, "ffprobe.exe");

    /// <summary>Finds the bundled FFmpeg (an "ffmpeg" folder next to the app), or AV1ENCODER_FFMPEG_DIR when set.</summary>
    public static FfmpegTools Locate()
    {
        var dir = Environment.GetEnvironmentVariable("AV1ENCODER_FFMPEG_DIR");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var tools = new FfmpegTools(dir);
        if (!File.Exists(tools.FfmpegPath) || !File.Exists(tools.FfprobePath))
            throw new EncodeException($"FFmpeg was not found in \"{dir}\". The installation appears incomplete; please reinstall.");
        return tools;
    }
}
