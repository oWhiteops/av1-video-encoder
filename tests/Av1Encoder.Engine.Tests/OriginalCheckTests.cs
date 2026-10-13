using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

public class OriginalCheckTests
{
    static MediaInfo Media(string path, string codec, long size, double seconds) => new(path, "mov", "QuickTime", size, TimeSpan.FromSeconds(seconds), null,
        new VideoStreamInfo(0, codec, 1920, 1080, 30, false, 0, "yuv420p", 8, null),
        [new AudioStreamInfo(1, "aac", 2, "stereo", 48000, 128_000, null)], [], 0, 0, 0);

    static readonly MediaInfo Source = Media(@"C:\in\clip.mp4", "h264", 100_000_000, 600);
    static readonly EncodeRequest Request = new() { InputPath = Source.Path, OutputPath = @"C:\in\clip_av1.mp4", Quality = QualitySettings.For(QualityPreset.Balanced) };

    static EncodeResult Result(long size = 40_000_000, double seconds = 600.5, string codec = "av1", string path = @"C:\in\clip_av1.mp4") =>
        new(path, Source.SizeBytes, size, TimeSpan.FromMinutes(5), VideoEncoder.SvtAv1, false, [], Media(path, codec, size, seconds));

    [Fact]
    public void GoodEncodeMayDeleteTheOriginal() => Assert.Null(OriginalCheck.Reason(Source, Request, Result()));

    [Fact]
    public void AnythingDoubtfulKeepsTheOriginal()
    {
        Assert.Contains("10% smaller", OriginalCheck.Reason(Source, Request, Result(size: 95_000_000)));
        Assert.Contains("length", OriginalCheck.Reason(Source, Request, Result(seconds: 590)));
        Assert.Contains("AV1", OriginalCheck.Reason(Source, Request, Result(codec: "h264")));
        Assert.Contains("same name", OriginalCheck.Reason(Source, Request, Result(path: Source.Path)));
        Assert.Contains("part", OriginalCheck.Reason(Source, Request with { TrimEnd = TimeSpan.FromSeconds(60) }, Result()));
        var silent = Result() with { Output = Result().Output with { Audio = [] } };
        Assert.Contains("audio", OriginalCheck.Reason(Source, Request, silent));
    }
}
