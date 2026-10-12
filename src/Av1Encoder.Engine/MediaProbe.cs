using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Av1Encoder.Engine;

public sealed record VideoStreamInfo(
    int Index, string Codec, int Width, int Height, double FrameRate, bool IsVariableFrameRate,
    int Rotation, string PixelFormat, int BitDepth, string? ColorTransfer)
{
    /// <summary>Dimensions as displayed, after applying rotation metadata.</summary>
    public (int Width, int Height) DisplaySize => Math.Abs(Rotation) % 180 == 90 ? (Height, Width) : (Width, Height);
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
}

public sealed record AudioStreamInfo(int Index, string Codec, int Channels, string? ChannelLayout, int SampleRate, long? BitRate, string? Language);

public sealed record SubtitleStreamInfo(int Index, string Codec, string? Language)
{
    static readonly HashSet<string> TextCodecs = ["subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text"];
    public bool IsText => TextCodecs.Contains(Codec);
}

public sealed record MediaInfo(
    string Path, string FormatName, string FormatLongName, long SizeBytes, TimeSpan Duration, long? BitRate,
    VideoStreamInfo? Video, IReadOnlyList<AudioStreamInfo> Audio, IReadOnlyList<SubtitleStreamInfo> Subtitles,
    int ExtraVideoStreams, int AttachmentStreams, int DataStreams);

public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(FfmpegTools tools, string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new EncodeException("The selected file does not exist.");
        EnsureReadable(path);

        var psi = new ProcessStartInfo(tools.FfprobePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
            throw new EncodeException("This file could not be read as a video. It may be corrupt or in an unsupported format.", await stderr);
        return Parse(path, await stdout);
    }

    /// <summary>Fails early with a clear message when another program holds the file exclusively.</summary>
    internal static void EnsureReadable(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (UnauthorizedAccessException) { throw new EncodeException("Access to the file was denied. Check its permissions."); }
        catch (IOException ex) { throw new EncodeException("The file is in use by another program and cannot be read.", ex.Message); }
    }

    public static MediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;

        VideoStreamInfo? video = null;
        var audio = new List<AudioStreamInfo>();
        var subs = new List<SubtitleStreamInfo>();
        int extraVideo = 0, attachments = 0, data = 0;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                int index = s.GetProperty("index").GetInt32();
                string codec = Str(s, "codec_name") ?? "unknown";
                switch (Str(s, "codec_type"))
                {
                    case "video" when IsAttachedPicture(s) || video != null:
                        extraVideo++;
                        break;
                    case "video":
                        video = ParseVideo(s, index, codec);
                        break;
                    case "audio":
                        audio.Add(new AudioStreamInfo(index, codec, Int(s, "channels") ?? 2, Str(s, "channel_layout"),
                            Int(s, "sample_rate") ?? 0, Long(s, "bit_rate"), Tag(s, "language")));
                        break;
                    case "subtitle":
                        subs.Add(new SubtitleStreamInfo(index, codec, Tag(s, "language")));
                        break;
                    case "attachment":
                        attachments++;
                        break;
                    default:
                        data++;
                        break;
                }
            }
        }

        double seconds = Dbl(format, "duration") ?? 0;
        return new MediaInfo(path, Str(format, "format_name") ?? "unknown", Str(format, "format_long_name") ?? "",
            Long(format, "size") ?? new FileInfo(path).Length, TimeSpan.FromSeconds(seconds), Long(format, "bit_rate"),
            video, audio, subs, extraVideo, attachments, data);
    }

    static VideoStreamInfo ParseVideo(JsonElement s, int index, string codec)
    {
        double avg = Rate(Str(s, "avg_frame_rate")), real = Rate(Str(s, "r_frame_rate"));
        string pixFmt = Str(s, "pix_fmt") ?? "unknown";
        // ponytail: r_frame_rate vs avg_frame_rate is the cheap VFR heuristic; a full timestamp scan is exact but slow.
        bool vfr = avg > 0 && real > 0 && Math.Abs(avg - real) / real > 0.01;
        return new VideoStreamInfo(index, codec, Int(s, "width") ?? 0, Int(s, "height") ?? 0, avg > 0 ? avg : real, vfr,
            Rotation(s), pixFmt, BitDepth(pixFmt, Int(s, "bits_per_raw_sample")), Str(s, "color_transfer"));
    }

    static bool IsAttachedPicture(JsonElement s) =>
        s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;

    static int Rotation(JsonElement s)
    {
        if (s.TryGetProperty("side_data_list", out var list))
            foreach (var sd in list.EnumerateArray())
                if (Dbl(sd, "rotation") is double r) return (int)Math.Round(r);
        return int.TryParse(Tag(s, "rotate"), out var t) ? t : 0;
    }

    internal static int BitDepth(string pixFmt, int? rawBits)
    {
        if (pixFmt.StartsWith("p010") || pixFmt.StartsWith("p210") || pixFmt.StartsWith("p410")) return 10;
        if (pixFmt.StartsWith("p012")) return 12;
        var m = Regex.Match(pixFmt, @"p(\d{2})(le|be)$");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        return rawBits is > 0 ? rawBits.Value : 8;
    }

    internal static double Rate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0)
            return n / d;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static string? Tag(JsonElement e, string name) =>
        e.TryGetProperty("tags", out var t) ? Str(t, name) : null;

    static double? Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    static long? Long(JsonElement e, string name) => Dbl(e, name) is double d ? (long)d : null;
    static int? Int(JsonElement e, string name) => Dbl(e, name) is double d ? (int)d : null;
}
