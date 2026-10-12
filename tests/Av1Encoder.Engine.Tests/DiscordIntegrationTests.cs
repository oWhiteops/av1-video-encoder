using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>Size-limited (Video to Discord) encoding against the real bundled FFmpeg.</summary>
[Collection("ffmpeg")]
public class DiscordIntegrationTests(Samples s)
{
    static readonly QualitySettings Fast = new(Crf: 35, Speed: 10, HardwareQuality: 30);

    string Out(string name) => Path.Combine(s.Dir, "out", Guid.NewGuid().ToString("N")[..6], name);

    static string[] TempFiles(string dir) =>
        Directory.GetFiles(dir, "*.partial").Concat(Directory.GetFiles(dir, "*.passlog*")).ToArray();

    [Fact]
    public async Task OversizedOutputIsRejectedBeforeReachingTheDestination()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, s.Basic);
        var output = Out("too-big.mp4");
        var request = new EncodeRequest { InputPath = s.Basic, OutputPath = output, Quality = Fast, MaxOutputBytes = 10_000 };
        var ex = await Assert.ThrowsAsync<OutputTooLargeException>(() => new EncodeJob(s.Tools, request, media).RunAsync(null, CancellationToken.None));
        Assert.True(ex.ActualBytes > 10_000);
        Assert.False(File.Exists(output));
        Assert.Empty(TempFiles(Path.GetDirectoryName(output)!));
    }

    [Fact]
    public async Task ExistingFileSurvivesAnOversizedAttemptEvenWithOverwriteConfirmed()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, s.Basic);
        var output = Out("existing.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep me");
        var request = new EncodeRequest { InputPath = s.Basic, OutputPath = output, Quality = Fast, MaxOutputBytes = 10_000, OverwriteExisting = true };
        await Assert.ThrowsAsync<OutputTooLargeException>(() => new EncodeJob(s.Tools, request, media).RunAsync(null, CancellationToken.None));
        Assert.Equal("keep me", File.ReadAllText(output));
    }

    async Task<EncodeResult> Encode(string input, Func<EncodeRequest, EncodeRequest> configure)
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        var request = configure(new EncodeRequest { InputPath = input, OutputPath = Out("out.mp4"), Quality = Fast });
        return await new EncodeJob(s.Tools, request, media).RunAsync(null, CancellationToken.None);
    }

    /// <summary>1280x720 @ 60 fps, 12 s, with a stereo default track, a mono "Mic" track and a 5.1 track.</summary>
    string MultiTrack => s.Get("discord-src.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=60:d=12",
        "-f", "lavfi", "-i", "sine=f=440:d=12", "-f", "lavfi", "-i", "sine=f=300:d=12", "-f", "lavfi", "-i", "sine=f=600:d=12",
        "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-c:v", "ffv1", "-c:a", "pcm_s16le",
        "-filter:a:0", "aformat=channel_layouts=stereo", "-filter:a:1", "aformat=channel_layouts=mono", "-filter:a:2", "aformat=channel_layouts=5.1",
        "-metadata:s:a:1", "title=Mic", "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0");

    [Fact]
    public async Task TrimmedEncodeHasTheSelectedDurationAndPassesVerification()
    {
        var result = await Encode(MultiTrack, r => r with { TrimStart = TimeSpan.FromSeconds(3), TrimEnd = TimeSpan.FromSeconds(8) });
        Assert.InRange(result.Output.Duration.TotalSeconds, 4.5, 5.5);
    }

    [Fact]
    public async Task DownscalesAndLowersFrameRateKeepingAspect()
    {
        var result = await Encode(MultiTrack, r => r with { MaxShortSide = 480, MaxFrameRate = 30 });
        var v = result.Output.Video!;
        Assert.Equal((854, 480), (v.Width, v.Height));
        Assert.Equal(30, v.FrameRate, 1);
        Assert.InRange(result.Output.Duration.TotalSeconds, 11.5, 12.5);
    }

    [Fact]
    public async Task PortraitSourceScalesItsShortSide()
    {
        var input = s.Get("portrait.mkv", "-f", "lavfi", "-i", "testsrc2=s=1080x1920:r=30:d=2", "-c:v", "ffv1");
        var result = await Encode(input, r => r with { MaxShortSide = 720 });
        Assert.Equal((720, 1280), (result.Output.Video!.Width, result.Output.Video.Height));
    }

    [Fact]
    public async Task KeepsTheDefaultTrackDownmixedAndListsDroppedOnes()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, MultiTrack);
        Assert.Equal("Mic", media.Audio[1].Title);
        var surround = media.Audio[2];
        Assert.Equal(6, surround.Channels);

        var kept = await Encode(MultiTrack, r => r with { AudioStreamIndexes = [media.DefaultAudio!.Index], DownmixToStereo = true });
        var a = Assert.Single(kept.Output.Audio);
        Assert.Equal(2, a.Channels);
        Assert.Contains(kept.Warnings, w => w.Contains("2 audio track(s) not included") && w.Contains("(Mic)"));

        var downmixed = await Encode(MultiTrack, r => r with { AudioStreamIndexes = [surround.Index], DownmixToStereo = true });
        Assert.Equal(2, Assert.Single(downmixed.Output.Audio).Channels);
    }

    [Fact]
    public async Task LeftoverPassLogsFromAKilledRunAreRemoved()
    {
        var output = Out("sweep.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var stale = output + ".0123456789abcdef0123456789abcdef.passlog-0.log";
        File.WriteAllText(stale, "junk");
        var media = await MediaProbe.ProbeAsync(s.Tools, s.Basic);
        await new EncodeJob(s.Tools, new EncodeRequest { InputPath = s.Basic, OutputPath = output, Quality = Fast }, media).RunAsync(null, CancellationToken.None);
        Assert.False(File.Exists(stale));
    }
}
