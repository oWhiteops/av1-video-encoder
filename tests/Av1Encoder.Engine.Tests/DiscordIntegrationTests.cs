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

    /// <summary>720p30, 10 s, noisy enough that bitrate control has real work to do.</summary>
    string Noisy => s.Get("noisy720.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=10,noise=alls=10:allf=t",
        "-f", "lavfi", "-i", "sine=d=10", "-c:v", "ffv1", "-c:a", "pcm_s16le");

    static EncodeRequest TwoPass(string input, string output) => new()
    {
        InputPath = input, OutputPath = output, Quality = new QualitySettings(32, 8, 26), TwoPass = true, AudioBitrateKbps = 96,
    };

    [Fact]
    public async Task TwoPassLandsCloseUnderTheTargetAndCleansUp()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, Noisy);
        var output = Out("twopass.mp4");
        const long limit = 2_000_000;
        var stages = new List<EncodeProgress>();
        var result = await SizeTargeting.EncodeToSizeAsync(s.Tools, TwoPass(Noisy, output), media, limit,
            progress: new SyncProgress<EncodeProgress>(stages.Add), margin: 0.04);
        Assert.InRange(result.OutputBytes, (long)(limit * 0.85), limit);
        Assert.Equal(1, result.Attempts);
        Assert.Contains(stages, p => p.Stage.Contains("pass 1 of 2"));
        Assert.Contains(stages, p => p.Stage.Contains("pass 2 of 2"));
        var fractions = stages.Select(p => p.Fraction).ToList();
        Assert.Equal(fractions.OrderBy(f => f), fractions);   // monotonic across both passes
        Assert.Empty(TempFiles(Path.GetDirectoryName(output)!));
    }

    [Theory]
    [InlineData("pass 1 of 2")]
    [InlineData("pass 2 of 2")]
    public async Task CancellingDuringEitherPassLeavesNothingBehind(string stage)
    {
        var input = s.Get("long.mkv", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=30", "-c:v", "ffv1");
        var media = await MediaProbe.ProbeAsync(s.Tools, input);
        var output = Out("cancel2pass.mp4");
        var request = TwoPass(input, output) with { Quality = new QualitySettings(32, 6, 26, VideoBitrateKbps: 1500) };
        using var cts = new CancellationTokenSource();
        var reached = new TaskCompletionSource();
        var run = new EncodeJob(s.Tools, request, media).RunAsync(
            new SyncProgress<EncodeProgress>(p => { if (p.Stage.Contains(stage)) reached.TrySetResult(); }), cts.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(90));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(File.Exists(output));
        Assert.Empty(TempFiles(Path.GetDirectoryName(output)!));
    }

    [Fact]
    public async Task QualityFloorStopsBeforeAnyEncode()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, Noisy);
        var output = Out("floor.mp4");
        var ex = await Assert.ThrowsAsync<TargetUnreachableException>(() =>
            SizeTargeting.EncodeToSizeAsync(s.Tools, TwoPass(Noisy, output), media, 2_000_000, floorKbps: 5000));
        Assert.Equal(0, ex.Attempts);
        Assert.Equal(5000, ex.FloorKbps);
        Assert.Contains("quality floor", ex.Message);
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)) && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(output)!).Any());
    }

    // A negative margin aims 50% over the limit, which forces every attempt to come out too large: the only way to
    // exercise the retry paths deterministically.
    [Fact]
    public async Task RetryStopsAtTheQualityFloorInsteadOfUsingAllAttempts()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, Noisy);
        const long limit = 2_000_000;
        int first = SizeTargeting.CalculateVideoBitrateKbps(limit, media.Duration, 96, 1.5);
        var ex = await Assert.ThrowsAsync<TargetUnreachableException>(() =>
            SizeTargeting.EncodeToSizeAsync(s.Tools, TwoPass(Noisy, Out("floor-retry.mp4")), media, limit, margin: -0.5, floorKbps: first - 10));
        Assert.Equal(1, ex.Attempts);
        Assert.True(ex.LastAttemptBytes > limit);
        Assert.True(ex.NeededKbps < ex.FloorKbps);
    }

    [Fact]
    public async Task ExhaustedAttemptsKeepTheExistingFileAndLeaveNoTemps()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, Noisy);
        var output = Out("exhausted.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "keep me");
        var log = new List<string>();
        var ex = await Assert.ThrowsAsync<TargetUnreachableException>(() => SizeTargeting.EncodeToSizeAsync(s.Tools,
            TwoPass(Noisy, output) with { OverwriteExisting = true }, media, 2_000_000, log: log.Add, margin: -0.5));
        Assert.Equal(3, ex.Attempts);
        Assert.Contains("after 3 attempts", ex.Message);
        Assert.Equal(3, log.Count(l => l.StartsWith("Size target")));
        Assert.Equal("keep me", File.ReadAllText(output));
        Assert.Empty(TempFiles(Path.GetDirectoryName(output)!));
    }

    /// <summary>640x360 @ 30 fps: 10 s of clean test pattern, then 10 s of heavy noise.</summary>
    string CalmThenNoisy => s.Get("calm-noisy.mkv", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=10", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30:d=10,noise=alls=25:allf=t",
        "-filter_complex", "[0:v][1:v]concat=n=2:v=1[v]", "-map", "[v]", "-c:v", "ffv1");

    [Fact]
    public async Task AnalysisMeasuresOnlyTheSelectedRangeAndCleansUp()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, CalmThenNoisy);
        var before = Directory.GetFiles(Path.GetTempPath(), "av1-analysis-*").Length;
        var steps = new List<double>();
        var calm = await SizeTargeting.AnalyzeAsync(s.Tools, media, null, TimeSpan.FromSeconds(9.5), new SyncProgress<double>(steps.Add));
        var noisy = await SizeTargeting.AnalyzeAsync(s.Tools, media, TimeSpan.FromSeconds(10.5), null);
        Assert.False(calm.IsRough);
        Assert.True(noisy.Crf32Kbps > 3 * calm.Crf32Kbps, $"noisy {noisy.Crf32Kbps:0} vs calm {calm.Crf32Kbps:0} kbps");
        Assert.Equal(3, steps.Count);
        Assert.Equal(1.0, steps[^1]);
        Assert.True(calm.PeakCrf32Kbps >= calm.Crf32Kbps);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "av1-analysis-*").Length);
    }

    [Fact]
    public async Task ShortSelectionIsAnalysedWhole()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, s.Basic);   // 4 s
        var steps = new List<double>();
        var estimate = await SizeTargeting.AnalyzeAsync(s.Tools, media, null, null, new SyncProgress<double>(steps.Add));
        Assert.Equal([1.0], steps);
        Assert.True(estimate.Crf32Kbps > 0);
    }

    [Fact]
    public async Task AnalysisCanBeCancelled()
    {
        var media = await MediaProbe.ProbeAsync(s.Tools, CalmThenNoisy);
        using var cts = new CancellationTokenSource();
        var run = SizeTargeting.AnalyzeAsync(s.Tools, media, null, null, new SyncProgress<double>(_ => cts.Cancel()), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void BundledFfmpegIsTheCalibratedBuild()
    {
        // Size accuracy, pass-1 timing and the planner's constants were measured on this exact build. If FFmpeg is
        // refreshed, re-run the accuracy matrix and calibration (docs/DESIGN.md), then update these strings.
        var version = File.ReadAllLines(Path.Combine(s.Tools.Directory, "..", "VERSION.txt")).Single(l => l.StartsWith("Version:"));
        Assert.Contains("ffmpeg version n8.1.3-17-gc389840894-20261010", version);
        var (_, _, err) = Samples.Run(s.Tools.FfmpegPath, ["-hide_banner", "-f", "lavfi", "-i", "color=s=64x64:d=0.1", "-c:v", "libsvtav1", "-f", "null", "-"]);
        Assert.Contains("SVT-AV1 Encoder Lib v4.2.0-151-g5ed9ffbcb", err);
        var (_, stdout, _) = Samples.Run(s.Tools.FfmpegPath, ["-hide_banner", "-version"]);
        Assert.Contains(version["Version: ".Length..].Split(' ')[2], stdout);
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
