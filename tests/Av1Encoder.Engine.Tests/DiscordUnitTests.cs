using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

public class DiscordArgumentsTests
{
    static MediaInfo Media(int w = 1920, int h = 1080, double fps = 60, int rotation = 0, params AudioStreamInfo[] audio) => new(
        @"C:\in\clip.mp4", "mov", "QuickTime", 100_000_000, TimeSpan.FromSeconds(120), 8_000_000,
        new VideoStreamInfo(0, "h264", w, h, fps, false, rotation, "yuv420p", 8, null),
        audio.Length > 0 ? audio : [new AudioStreamInfo(1, "aac", 2, "stereo", 48000, 160000, null)], [], 0, 0, 0);

    static EncodeRequest Request => new() { InputPath = @"C:\in\clip.mp4", OutputPath = @"C:\out\clip.mp4", Quality = QualitySettings.For(QualityPreset.Balanced) };

    static List<string> Args(MediaInfo m, EncodeRequest r) => FfmpegArguments.Build(m, r, VideoEncoder.SvtAv1, "o");
    static string? After(List<string> args, string flag) => args.IndexOf(flag) is >= 0 and var i ? args[i + 1] : null;

    [Fact]
    public void TrimSeeksBeforeInputAndLimitsDuration()
    {
        var args = Args(Media(), Request with { TrimStart = TimeSpan.FromSeconds(10.5), TrimEnd = TimeSpan.FromSeconds(40) });
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
        Assert.Equal("10.5", After(args, "-ss"));
        Assert.Equal("29.5", After(args, "-t"));
        Assert.True(args.IndexOf("-t") > args.IndexOf("-i"));
    }

    [Fact]
    public void TrimEndAloneNeedsNoSeekAndEndPastSourceIsIgnored()
    {
        var endOnly = Args(Media(), Request with { TrimEnd = TimeSpan.FromSeconds(30) });
        Assert.DoesNotContain("-ss", endOnly);
        Assert.Equal("30", After(endOnly, "-t"));
        var past = Args(Media(), Request with { TrimEnd = TimeSpan.FromSeconds(500) });
        Assert.DoesNotContain("-t", past);
    }

    [Fact]
    public void OutputDurationFollowsTheTrimRange()
    {
        var m = Media();
        Assert.Equal(TimeSpan.FromSeconds(120), Request.OutputDuration(m));
        Assert.Equal(TimeSpan.FromSeconds(20), (Request with { TrimStart = TimeSpan.FromSeconds(100) }).OutputDuration(m));
        Assert.Equal(TimeSpan.Zero, (Request with { TrimStart = TimeSpan.FromSeconds(50), TrimEnd = TimeSpan.FromSeconds(40) }).OutputDuration(m));
    }

    [Theory]
    [InlineData(1920, 1080, 0, 720, "scale=-2:720:flags=lanczos")]   // landscape: height is the short side
    [InlineData(1080, 1920, 0, 720, "scale=720:-2:flags=lanczos")]   // portrait
    [InlineData(1920, 1080, 90, 720, "scale=720:-2:flags=lanczos")]  // stored landscape, displayed portrait
    [InlineData(3840, 2160, 0, 1080, "scale=-2:1080:flags=lanczos")]
    public void ScalesTheDisplayedShortSide(int w, int h, int rotation, int side, string expected) =>
        Assert.Equal(expected, After(Args(Media(w, h, rotation: rotation), Request with { MaxShortSide = side }), "-vf"));

    [Theory]
    [InlineData(1280, 720, 720)]
    [InlineData(1280, 720, 1080)]
    public void NeverUpscales(int w, int h, int side) =>
        Assert.DoesNotContain("-vf", Args(Media(w, h), Request with { MaxShortSide = side }));

    [Fact]
    public void LowersButNeverRaisesFrameRate()
    {
        Assert.Equal("fps=30", After(Args(Media(fps: 60), Request with { MaxFrameRate = 30 }), "-vf"));
        Assert.DoesNotContain("-vf", Args(Media(fps: 30), Request with { MaxFrameRate = 30 }));
        Assert.DoesNotContain("-vf", Args(Media(fps: 29.97), Request with { MaxFrameRate = 30 }));
        Assert.DoesNotContain("-vf", Args(Media(fps: 24), Request with { MaxFrameRate = 30 }));
        Assert.Equal("scale=-2:720:flags=lanczos,fps=30", After(Args(Media(fps: 60), Request with { MaxFrameRate = 30, MaxShortSide = 720 }), "-vf"));
    }

    [Fact]
    public void KeepsOnlySelectedAudioAndNamesTheRest()
    {
        var m = Media(audio: [
            new AudioStreamInfo(1, "aac", 2, "stereo", 48000, null, "eng", "Game", IsDefault: true),
            new AudioStreamInfo(2, "aac", 1, "mono", 48000, null, null, "Mic"),
            new AudioStreamInfo(3, "aac", 2, "stereo", 48000, null, "und", null)]);
        Assert.Equal(1, m.DefaultAudio!.Index);
        var r = Request with { AudioStreamIndexes = [1] };
        var args = Args(m, r);
        Assert.Contains("0:1", args);
        Assert.DoesNotContain("0:2", args);
        Assert.DoesNotContain("0:3", args);
        var warning = Assert.Single(FfmpegArguments.Warnings(m, r), w => w.Contains("not included"));
        Assert.Contains("track 2 (Mic)", warning);
        Assert.Contains("track 3.", warning);  // "und" language is not worth printing
        Assert.DoesNotContain("und", warning);
    }

    [Fact]
    public void DefaultAudioFallsBackToTheFirstTrack()
    {
        var m = Media(audio: [new AudioStreamInfo(4, "aac", 2, null, 48000, null, null), new AudioStreamInfo(5, "aac", 2, null, 48000, null, null)]);
        Assert.Equal(4, m.DefaultAudio!.Index);
    }

    [Fact]
    public void DownmixesOnlyAboveStereo()
    {
        var m = Media(audio: [new AudioStreamInfo(1, "aac", 6, "5.1", 48000, null, null), new AudioStreamInfo(2, "aac", 1, "mono", 48000, null, null)]);
        var r = Request with { DownmixToStereo = true };
        var args = Args(m, r);
        Assert.Equal("2", After(args, "-ac:a:0"));
        Assert.Equal("aac", After(args, "-c:a:0"));       // can't copy a track that is being downmixed
        Assert.Equal("160k", After(args, "-b:a:0"));      // stereo default, not the 5.1 one
        Assert.Null(After(args, "-ac:a:1"));
        Assert.Equal("copy", After(args, "-c:a:1"));
        Assert.Contains(FfmpegArguments.Warnings(m, r), w => w.Contains("downmixed from 6 channels"));
    }

    [Fact]
    public void AudioBudgetCountsOnlyKeptTracks()
    {
        var m = Media(audio: [new AudioStreamInfo(1, "aac", 2, null, 48000, null, null), new AudioStreamInfo(2, "aac", 2, null, 48000, null, null)]);
        Assert.Equal(192, SizeTargeting.TotalAudioKbps(m, Request with { AudioBitrateKbps = 96 }));
        Assert.Equal(96, SizeTargeting.TotalAudioKbps(m, Request with { AudioBitrateKbps = 96, AudioStreamIndexes = [2] }));
        Assert.Equal(0, SizeTargeting.TotalAudioKbps(m, Request with { IncludeAudio = false }));
    }
}
