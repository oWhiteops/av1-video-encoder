using System.Globalization;

namespace Av1Encoder.Engine;

/// <summary>Builds FFmpeg argument lists. Arguments are passed via ProcessStartInfo.ArgumentList, never a shell string.</summary>
public static class FfmpegArguments
{
    static readonly HashSet<string> Mp4CopyAudio = ["aac", "mp3"];

    /// <summary>MKV is only needed when the source carries streams MP4 cannot hold (bitmap subtitles, font attachments).</summary>
    public static OutputContainer PreferredContainer(MediaInfo media) =>
        media.Subtitles.Any(s => !s.IsText) || media.AttachmentStreams > 0 ? OutputContainer.Mkv : OutputContainer.Mp4;

    /// <summary>Human-readable notes about streams that are converted or not carried over, so nothing is dropped silently.</summary>
    public static List<string> Warnings(MediaInfo media, EncodeRequest request)
    {
        var w = new List<string>();
        bool mp4 = request.Container == OutputContainer.Mp4;
        if (!request.IncludeAudio && media.Audio.Count > 0) w.Add($"Audio removed as requested ({media.Audio.Count} track(s)).");
        if (request.IncludeSubtitles)
        {
            if (mp4 && media.Subtitles.Any(s => !s.IsText))
                w.Add($"{media.Subtitles.Count(s => !s.IsText)} image-based subtitle track(s) cannot be stored in MP4 and were not included. Use MKV output to keep them.");
            if (mp4 && media.Subtitles.Any(s => s.Codec is "ass" or "ssa"))
                w.Add("Styled (ASS/SSA) subtitles were converted to plain MP4 text subtitles; styling is lost.");
        }
        else if (media.Subtitles.Count > 0) w.Add($"Subtitles removed as requested ({media.Subtitles.Count} track(s)).");
        if (mp4 && media.AttachmentStreams > 0) w.Add($"{media.AttachmentStreams} attachment(s) (e.g. fonts) cannot be stored in MP4 and were not included.");
        if (media.ExtraVideoStreams > 0) w.Add($"{media.ExtraVideoStreams} extra video stream(s)/cover image(s) were not included; only the main video was encoded.");
        if (media.DataStreams > 0) w.Add($"{media.DataStreams} data stream(s) (e.g. timecode or sensor data) were not included.");
        if (request.IncludeAudio)
            foreach (var a in media.Audio.Where(a => !CanCopyAudio(a, request)))
                w.Add($"Audio track {a.Index} ({a.Codec}) re-encoded to AAC {AudioBitrateKbps(a, request)} kbps.");
        if (media.Video is { IsHdr: true } && request.Encoder != VideoEncoder.SvtAv1)
            w.Add("HDR source on a hardware encoder: HDR metadata may not be preserved. Software encoding is recommended for HDR.");
        return w;
    }

    public static int AudioBitrateKbps(AudioStreamInfo a, EncodeRequest request) =>
        request.AudioBitrateKbps ?? a.Channels switch { <= 1 => 96, 2 => 160, _ => Math.Min(a.Channels, 8) * 64 };

    static bool CanCopyAudio(AudioStreamInfo a, EncodeRequest request) =>
        request.AudioBitrateKbps == null && (request.Container == OutputContainer.Mkv
            ? !a.Codec.StartsWith("pcm_") && a.Codec is not ("flac" or "alac" or "truehd")
            : Mp4CopyAudio.Contains(a.Codec));

    public static List<string> Build(MediaInfo media, EncodeRequest request, VideoEncoder encoder, string outputPath)
    {
        var video = media.Video ?? throw new EncodeException("This file does not contain a video stream.");
        bool mp4 = request.Container == OutputContainer.Mp4;
        var q = request.Quality;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "warning",
            "-progress", "pipe:1", "-nostats", "-stats_period", "0.5",
            "-i", media.Path,
            "-map_metadata", "0", "-map_chapters", "0",
            "-map", $"0:{video.Index}",
            // Keep source timestamps: FFmpeg's MP4 default would otherwise force constant frame rate.
            "-fps_mode", "vfr",
        };
        args.AddRange(VideoCodecArgs(video, q, encoder, request.SvtParams));

        if (request.IncludeAudio)
        {
            int outIdx = 0;
            foreach (var a in media.Audio)
            {
                args.AddRange(["-map", $"0:{a.Index}"]);
                if (CanCopyAudio(a, request))
                    args.AddRange([$"-c:a:{outIdx}", "copy"]);
                else
                    args.AddRange([$"-c:a:{outIdx}", "aac", $"-b:a:{outIdx}", $"{AudioBitrateKbps(a, request)}k"]);
                outIdx++;
            }
        }

        if (request.IncludeSubtitles)
        {
            int outIdx = 0;
            foreach (var s in media.Subtitles.Where(s => !mp4 || s.IsText))
            {
                args.AddRange(["-map", $"0:{s.Index}", $"-c:s:{outIdx++}", mp4 ? "mov_text" : "copy"]);
            }
            if (!mp4 && media.AttachmentStreams > 0) args.AddRange(["-map", "0:t", "-c:t", "copy"]);
        }

        if (mp4) args.AddRange(["-movflags", "+faststart", "-f", "mp4"]);
        else args.AddRange(["-f", "matroska"]);
        args.Add(outputPath);
        return args;
    }

    static IEnumerable<string> VideoCodecArgs(VideoStreamInfo v, QualitySettings q, VideoEncoder encoder, string? svtParams)
    {
        string? kbps = q.VideoBitrateKbps?.ToString(CultureInfo.InvariantCulture) + "k";
        switch (encoder)
        {
            case VideoEncoder.SvtAv1:
                // 10-bit output compresses better and bands less, even from 8-bit sources; AV1 Main profile covers it.
                var svt = new List<string> { "-c:v", "libsvtav1", "-preset", $"{q.Speed}", "-pix_fmt", "yuv420p10le" };
                svt.AddRange(q.VideoBitrateKbps is null ? ["-crf", $"{q.Crf}"] : ["-b:v", kbps!]);
                if (!string.IsNullOrWhiteSpace(svtParams)) svt.AddRange(["-svtav1-params", svtParams]);
                return svt;
            case VideoEncoder.Amf:
                // AMF's QVBR level runs the other way (higher = better); 52 - q keeps one "lower is better" scale.
                return q.VideoBitrateKbps is null
                    ? ["-c:v", "av1_amf", "-usage", "transcoding", "-quality", "high_quality", "-rc", "qvbr", "-qvbr_quality_level", $"{52 - q.HardwareQuality}"]
                    : ["-c:v", "av1_amf", "-usage", "transcoding", "-quality", "high_quality", "-rc", "vbr_peak", "-b:v", kbps!, "-maxrate", kbps!];
            case VideoEncoder.Nvenc:
                return q.VideoBitrateKbps is null
                    ? ["-c:v", "av1_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-cq", $"{q.HardwareQuality}", "-b:v", "0"]
                    : ["-c:v", "av1_nvenc", "-preset", "p6", "-tune", "hq", "-rc", "vbr", "-b:v", kbps!, "-maxrate", kbps!];
            case VideoEncoder.Qsv:
                return q.VideoBitrateKbps is null
                    ? ["-c:v", "av1_qsv", "-preset", "slow", "-global_quality", $"{q.HardwareQuality}"]
                    : ["-c:v", "av1_qsv", "-preset", "slow", "-b:v", kbps!, "-maxrate", kbps!];
            default:
                throw new ArgumentOutOfRangeException(nameof(encoder));
        }
    }

    /// <summary>Renders an argument list as a copy-pasteable command line for the log.</summary>
    public static string ToDisplayString(string exe, IEnumerable<string> args) =>
        string.Join(' ', new[] { exe }.Concat(args).Select(a => a.Length == 0 || a.Any(c => c is ' ' or '"' or '\t') ? $"\"{a.Replace("\"", "\\\"")}\"" : a));
}
