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
            foreach (var a in KeptAudio(media, request).Where(a => !CanCopyAudio(a, request)))
                w.Add($"Audio track {a.Index} ({a.Codec}) re-encoded to AAC {AudioBitrateKbps(a, request)} kbps.");
        if (request.IncludeAudio && media.Audio.Except(KeptAudio(media, request)).ToList() is { Count: > 0 } dropped)
            w.Add($"{dropped.Count} audio track(s) not included: {string.Join("; ", dropped.Select(a => a.Describe()))}. Only the main track was kept.");
        foreach (var a in KeptAudio(media, request).Where(a => IsDownmixed(a, request)))
            w.Add($"Audio {a.Describe()} downmixed from {a.Channels} channels to stereo.");
        if (media.Video is { IsHdr: true } && request.Encoder != VideoEncoder.SvtAv1)
            w.Add("HDR source on a hardware encoder: HDR metadata may not be preserved. Software encoding is recommended for HDR.");
        return w;
    }

    /// <summary>Source audio streams that go into the output.</summary>
    public static IEnumerable<AudioStreamInfo> KeptAudio(MediaInfo media, EncodeRequest request) =>
        !request.IncludeAudio ? [] : request.AudioStreamIndexes is { } keep ? media.Audio.Where(a => keep.Contains(a.Index)) : media.Audio;

    static bool IsDownmixed(AudioStreamInfo a, EncodeRequest request) => request.DownmixToStereo && a.Channels > 2;

    public static int AudioBitrateKbps(AudioStreamInfo a, EncodeRequest request) =>
        request.AudioBitrateKbps ?? (IsDownmixed(a, request) ? 2 : a.Channels) switch { <= 1 => 96, 2 => 160, var c => Math.Min(c, 8) * 64 };

    static bool CanCopyAudio(AudioStreamInfo a, EncodeRequest request) =>
        request.AudioBitrateKbps == null && !IsDownmixed(a, request) && (request.Container == OutputContainer.Mkv
            ? !a.Codec.StartsWith("pcm_") && a.Codec is not ("flac" or "alac" or "truehd")
            : Mp4CopyAudio.Contains(a.Codec));

    /// <summary>Scale and fps filters; empty when the source is kept as it is.</summary>
    internal static List<string> VideoFilters(VideoStreamInfo v, EncodeRequest request)
    {
        var filters = new List<string>();
        // Autorotate runs before these filters, so they see the displayed orientation.
        var (w, h) = v.DisplaySize;
        if (request.MaxShortSide is int side && Math.Min(w, h) > side)
            filters.Add(w <= h ? $"scale={side}:-2:flags=lanczos" : $"scale=-2:{side}:flags=lanczos");
        if (request.MaxFrameRate is double fps && v.FrameRate > fps + 0.01)
            filters.Add("fps=" + fps.ToString(CultureInfo.InvariantCulture));
        return filters;
    }

    /// <param name="pass">0 = single pass; 1 = analysis pass (writes only <paramref name="passLog"/>); 2 = final pass.</param>
    public static List<string> Build(MediaInfo media, EncodeRequest request, VideoEncoder encoder, string outputPath, int pass = 0, string? passLog = null)
    {
        var video = media.Video ?? throw new EncodeException("This file does not contain a video stream.");
        bool mp4 = request.Container == OutputContainer.Mp4;
        var q = request.Quality;
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "warning",
            "-progress", "pipe:1", "-nostats", "-stats_period", "0.5",
        };
        // Input-side seek is fast and, because the video is re-encoded, frame-accurate.
        if (request.TrimStart is { } start && start > TimeSpan.Zero) args.AddRange(["-ss", Seconds(start)]);
        args.AddRange(["-i", media.Path]);
        if (request.TrimStart is { TotalSeconds: > 0 } || request.TrimEnd is { } end && end < media.Duration)
            args.AddRange(["-t", Seconds(request.OutputDuration(media))]);
        args.AddRange([
            "-map_metadata", "0", "-map_chapters", "0",
            "-map", $"0:{video.Index}",
            // Keep source timestamps: FFmpeg's MP4 default would otherwise force constant frame rate.
            "-fps_mode", "vfr",
        ]);
        if (VideoFilters(video, request) is { Count: > 0 } filters) args.AddRange(["-vf", string.Join(",", filters)]);
        args.AddRange(VideoCodecArgs(video, q, encoder, request.SvtParams));
        if (pass > 0) args.AddRange(["-pass", $"{pass}", "-passlogfile", passLog ?? throw new ArgumentNullException(nameof(passLog))]);
        if (pass == 1)
        {
            args.AddRange(["-an", "-sn", "-dn", "-f", "null", "-"]);
            return args;
        }

        int audioIdx = 0;
        foreach (var a in KeptAudio(media, request))
        {
            args.AddRange(["-map", $"0:{a.Index}"]);
            if (CanCopyAudio(a, request))
                args.AddRange([$"-c:a:{audioIdx}", "copy"]);
            else
                args.AddRange([$"-c:a:{audioIdx}", "aac", $"-b:a:{audioIdx}", $"{AudioBitrateKbps(a, request)}k"]);
            if (IsDownmixed(a, request)) args.AddRange([$"-ac:a:{audioIdx}", "2"]);
            audioIdx++;
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

    static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Renders an argument list as a copy-pasteable command line for the log.</summary>
    public static string ToDisplayString(string exe, IEnumerable<string> args) =>
        string.Join(' ', new[] { exe }.Concat(args).Select(a => a.Length == 0 || a.Any(c => c is ' ' or '"' or '\t') ? $"\"{a.Replace("\"", "\\\"")}\"" : a));
}
