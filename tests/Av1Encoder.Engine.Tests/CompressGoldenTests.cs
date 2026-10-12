using System.Runtime.CompilerServices;
using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>
/// Locks down the exact FFmpeg arguments and warnings Compress mode produces, so Discord-mode changes to the shared
/// engine can't silently alter it. Regenerate deliberately with UPDATE_GOLDEN=1 and review the diff.
/// </summary>
public class CompressGoldenTests
{
    static AudioStreamInfo Audio(int index, string codec, int channels = 2) => new(index, codec, channels, null, 48000, null, null);

    static MediaInfo Media(AudioStreamInfo[]? audio = null, SubtitleStreamInfo[]? subs = null, int attachments = 0,
        int extraVideo = 0, int data = 0, string? transfer = null) => new(
        @"C:\in\clip.mov", "mov", "QuickTime", 10_000_000, TimeSpan.FromSeconds(10), 8_000_000,
        new VideoStreamInfo(0, "h264", 1920, 1080, 30, false, 0, "yuv420p", 8, transfer),
        audio ?? [Audio(1, "aac")], subs ?? [], extraVideo, attachments, data);

    static EncodeRequest Request(QualitySettings? q = null, string output = @"C:\out\clip_av1.mp4") => new()
    {
        InputPath = @"C:\in\clip.mov", OutputPath = output, Quality = q ?? QualitySettings.For(QualityPreset.Balanced),
    };

    static IEnumerable<(string Name, MediaInfo Media, EncodeRequest Request, VideoEncoder Encoder)> Cases()
    {
        foreach (var p in Enum.GetValues<QualityPreset>())
            yield return ($"svt-{p}", Media(), Request(QualitySettings.For(p)), VideoEncoder.SvtAv1);
        yield return ("svt-custom", Media(), Request(new QualitySettings(22, 3, QualitySettings.HardwareQualityFromCrf(22))), VideoEncoder.SvtAv1);
        yield return ("svt-params", Media(), Request() with { SvtParams = "tune=0" }, VideoEncoder.SvtAv1);
        foreach (var e in HardwareDetector.HardwareEncoders)
            yield return ($"hw-{e}", Media(), Request(), e);
        yield return ("audio-removed", Media(), Request() with { IncludeAudio = false }, VideoEncoder.SvtAv1);
        yield return ("audio-mp3-pcm-51", Media([Audio(1, "mp3"), Audio(2, "pcm_s16le"), Audio(3, "pcm_s24le", 6)]), Request(), VideoEncoder.SvtAv1);
        yield return ("audio-mono-flac", Media([Audio(1, "flac", 1)]), Request(), VideoEncoder.SvtAv1);
        yield return ("subs-text-mp4", Media(subs: [new(2, "subrip", "eng"), new(3, "ass", null)]), Request(), VideoEncoder.SvtAv1);
        yield return ("subs-image-mp4", Media(subs: [new(2, "hdmv_pgs_subtitle", "eng")]), Request(), VideoEncoder.SvtAv1);
        yield return ("subs-removed", Media(subs: [new(2, "subrip", "eng")]), Request() with { IncludeSubtitles = false }, VideoEncoder.SvtAv1);
        yield return ("mkv-all", Media([Audio(1, "ac3", 6), Audio(2, "flac")], [new(3, "subrip", null), new(4, "hdmv_pgs_subtitle", null)], attachments: 2),
            Request(output: @"C:\out\clip_av1.mkv"), VideoEncoder.SvtAv1);
        yield return ("extras-hdr-hw", Media(extraVideo: 1, data: 1, transfer: "smpte2084"), Request(), VideoEncoder.Amf);
    }

    static string Render() => string.Join("\n", Cases().Select(c =>
        $"{c.Name}\n  args: {FfmpegArguments.ToDisplayString("ffmpeg", FfmpegArguments.Build(c.Media, c.Request, c.Encoder, @"C:\out\tmp.partial"))}\n" +
        $"  warnings: {string.Join(" | ", FfmpegArguments.Warnings(c.Media, c.Request with { Encoder = c.Encoder }))}")) + "\n";

    static string GoldenPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "golden", "compress-args.txt");

    [Fact]
    public void CompressModeArgumentsAreUnchanged()
    {
        var actual = Render();
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath())!);
            File.WriteAllText(GoldenPath(), actual);
        }
        Assert.Equal(File.ReadAllText(GoldenPath()).ReplaceLineEndings("\n"), actual);
    }
}
