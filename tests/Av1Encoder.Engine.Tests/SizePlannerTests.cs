using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

/// <summary>
/// Recommendation scenarios from the plan. Complexity is given explicitly (CRF-32 kbps at source size, preset 8) so the
/// tests pin the decision logic, not the provisional calibration constants' exact values.
/// </summary>
public class SizePlannerTests
{
    const long Free = 19_922_944;

    static AudioStreamInfo Track(int index, int channels = 2, string? title = null, bool isDefault = false) =>
        new(index, "aac", channels, null, 48000, null, null, title, isDefault);

    static MediaInfo Media(int w, int h, double fps, double seconds, long size = 500_000_000, string? transfer = null, params AudioStreamInfo[] audio) => new(
        @"C:\in\clip.mp4", "mov", "QuickTime", size, TimeSpan.FromSeconds(seconds), null,
        new VideoStreamInfo(0, "h264", w, h, fps, false, 0, "yuv420p", 8, transfer),
        audio.Length > 0 ? audio : [Track(1)], [], 0, 0, 0);

    static ComplexityEstimate Measured(double kbps) => new(kbps, kbps, IsRough: false);

    [Fact]
    public void A_LongHighResGameplay_RecommendsTrimmingAndListsDroppedTracks()
    {
        var media = Media(2560, 1440, 60, 600, audio: [Track(1, title: "Game", isDefault: true), Track(2, 1, "Mic"), Track(3, title: "Discord")]);
        var plan = SizePlanner.Plan(new(media, Free, DiscordStrategy.Balanced) { Complexity = Measured(11_000) });
        Assert.Equal(QualityLabel.Severe, plan.Label);
        Assert.Equal(480, plan.MaxShortSide);
        Assert.Equal(30, plan.MaxFrameRate);
        Assert.Contains(plan.Advice, a => a.StartsWith("Trimming keeps the most quality: at 1080p60") && a.Contains("up to about 0:"));
        Assert.Contains(plan.Warnings, w => w.Contains("track 2 (Mic)") && w.Contains("track 3 (Discord)"));
        Assert.Equal(1, plan.AudioStreamIndex);
    }

    [Fact]
    public void B_ShortGameplay_KeepsOriginal()
    {
        var plan = SizePlanner.Plan(new(Media(1920, 1080, 60, 45), Free, DiscordStrategy.Balanced) { Complexity = Measured(4000) });
        Assert.Null(plan.MaxShortSide);
        Assert.Null(plan.MaxFrameRate);
        Assert.Equal(QualityLabel.Good, plan.Label);
        Assert.StartsWith("Keeps 1080p60.", plan.Summary);
        Assert.DoesNotContain(plan.Advice, a => a.Contains("Trimming"));
    }

    [Fact]
    public void C_TalkingHead_KeepsOriginalAtGoodQuality()
    {
        var plan = SizePlanner.Plan(new(Media(1920, 1080, 30, 180), Free, DiscordStrategy.BestQuality) { Complexity = Measured(600) });
        Assert.Null(plan.MaxShortSide);
        Assert.Equal(QualityLabel.Good, plan.Label);
        Assert.Equal(128, plan.AudioKbps);
    }

    [Fact]
    public void D_PortraitHdr4K_DownscalesShortSideAndWarnsAboutHdr()
    {
        var plan = SizePlanner.Plan(new(Media(2160, 3840, 30, 120, transfer: "smpte2084"), Free, DiscordStrategy.Balanced) { Complexity = Measured(8000) });
        Assert.Equal(1080, plan.MaxShortSide);
        Assert.Null(plan.MaxFrameRate);
        Assert.Contains(plan.Warnings, w => w.Contains("HDR"));
        Assert.Contains("Lowers to 1080p30: at 2160p30", plan.Summary);
    }

    [Fact]
    public void E_LongScreenRecording_AudioAdviceAndOverrideChangeTheOutcome()
    {
        var media = Media(1920, 1080, 30, 1200);
        var auto = SizePlanner.Plan(new(media, Free, DiscordStrategy.Balanced) { Complexity = Measured(150) });
        Assert.Equal(QualityLabel.Severe, auto.Label);            // 96 kbps audio leaves the video under the engine minimum
        Assert.Contains(auto.Advice, a => a.Contains("too long for the size limit"));
        Assert.Contains(auto.Advice, a => a.StartsWith("Audio uses"));

        var lowAudio = SizePlanner.Plan(new(media, Free, DiscordStrategy.Balanced) { Complexity = Measured(150), AudioKbps = 64 });
        Assert.True(lowAudio.VideoKbps > auto.VideoKbps);
        Assert.Null(lowAudio.MaxShortSide);                     // 1080p is acceptable once audio gives up 32 kbps
        Assert.Equal(QualityLabel.Softened, lowAudio.Label);
    }

    [Fact]
    public void F_SourceAlreadyUnderTheLimit_SaysSo()
    {
        var plan = SizePlanner.Plan(new(Media(1920, 1080, 30, 60, size: 12_000_000), Free, DiscordStrategy.Balanced));
        Assert.Contains(plan.Advice, a => a.StartsWith("This file already fits (11.4 MB)"));
        Assert.True(plan.IsRough);
        Assert.EndsWith("(rough estimate).", plan.Summary);
    }

    [Fact]
    public void FrameRateDropsOnlyAfterResolutionStepsDownTo720p()
    {
        // Needs 5x the budget at 1080p60. With the calibrated exponents: 720p60 scores 0.34, 1080p30 0.31, 720p30 0.53
        // (accepted from 0.35).
        var media = Media(1920, 1080, 60, 60);
        var budget = SizePlanner.Plan(new(media, Free, DiscordStrategy.Balanced) { Complexity = Measured(1) }).VideoKbps;
        double needed1080p60 = budget * 5 / SizePlanner.PresetFactor(6);
        var plan = SizePlanner.Plan(new(media, Free, DiscordStrategy.Balanced) { Complexity = Measured(needed1080p60) });
        Assert.Equal(720, plan.MaxShortSide);
        Assert.Equal(30, plan.MaxFrameRate);
        Assert.Contains(plan.Advice, a => a.Contains("less smooth"));
    }

    [Fact]
    public void OverridesPinTheirDimensionAndNeverUpscale()
    {
        var media = Media(1280, 720, 60, 30);
        var plan = SizePlanner.Plan(new(media, Free, DiscordStrategy.Balanced) { Complexity = Measured(50_000), ShortSide = 1080, FrameRate = 0 });
        Assert.Null(plan.MaxShortSide);   // asked for 1080p on a 720p source: stays 720p
        Assert.Null(plan.MaxFrameRate);   // "Original" frame rate kept even though it hurts
        var forced = SizePlanner.Plan(new(media, Free, DiscordStrategy.Balanced) { Complexity = Measured(100), ShortSide = 480 });
        Assert.Equal(480, forced.MaxShortSide);
    }

    [Fact]
    public void FloorFollowsStrategyAndChosenSize()
    {
        var media = Media(1920, 1080, 30, 60);
        var best = SizePlanner.Plan(new(media, Free, DiscordStrategy.BestQuality) { Complexity = Measured(2000) });
        var fast = SizePlanner.Plan(new(media, Free, DiscordStrategy.Fastest) { Complexity = Measured(2000) });
        Assert.True(best.FloorKbps > fast.FloorKbps);
        Assert.True(fast.FloorKbps >= SizeTargeting.MinVideoKbps);
    }

    [Fact]
    public void ApplyBuildsTheEngineRequest()
    {
        var media = Media(1920, 1080, 60, 120, audio: [Track(1, 6, isDefault: true), Track(2)]);
        var input = new PlanInput(media, Free, DiscordStrategy.Balanced) { Complexity = Measured(20_000), TrimStart = TimeSpan.FromSeconds(10), TrimEnd = TimeSpan.FromSeconds(70) };
        var plan = SizePlanner.Plan(input);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.Duration);
        Assert.Contains(plan.Warnings, w => w.Contains("downmixed to stereo"));
        var r = plan.Apply(new EncodeRequest { InputPath = media.Path, OutputPath = @"C:\out\x.mp4", Quality = QualitySettings.For(QualityPreset.Balanced) }, input);
        Assert.Equal(6, r.Quality.Speed);
        Assert.True(r.TwoPass);
        Assert.True(r.DownmixToStereo);
        Assert.Equal([1], r.AudioStreamIndexes);
        Assert.Equal(96, r.AudioBitrateKbps);
        Assert.Equal(TimeSpan.FromSeconds(10), r.TrimStart);
        Assert.Equal(plan.MaxShortSide, r.MaxShortSide);

        var silent = SizePlanner.Plan(input with { AudioKbps = 0 });
        Assert.False(silent.Apply(r, input with { AudioKbps = 0 }).IncludeAudio);
        Assert.True(silent.VideoKbps > plan.VideoKbps);
    }

    // The 30 validation encodes from docs/CALIBRATION.md: score with the corrected estimate, and the band their mean
    // VMAF measured (>= 95 good, >= 90 slightly soft, >= 80 noticeably blocky).
    [Theory]
    [InlineData(0.30, "noticeably blocky"), InlineData(0.61, "good"), InlineData(1.01, "good")]   // gameplay, portrait 60 fps A
    [InlineData(0.30, "noticeably blocky"), InlineData(0.61, "slightly soft"), InlineData(1.02, "good")] // gameplay, 1440p60
    [InlineData(0.76, "good"), InlineData(1.51, "good"), InlineData(2.52, "good")]                // screen recording
    [InlineData(0.31, "slightly soft"), InlineData(0.62, "good"), InlineData(1.04, "good")]       // phone, portrait 60 fps
    [InlineData(0.33, "slightly soft"), InlineData(0.65, "good"), InlineData(1.09, "good")]       // phone, 4K portrait A
    [InlineData(0.30, "noticeably blocky"), InlineData(0.61, "good"), InlineData(1.02, "good")]   // uneven 720p30 clip
    [InlineData(0.33, "slightly soft"), InlineData(0.66, "good"), InlineData(1.11, "good")]       // 720p30 clip
    [InlineData(0.31, "slightly soft"), InlineData(0.62, "good"), InlineData(1.04, "good")]       // phone, 4K portrait B
    [InlineData(0.31, "noticeably blocky"), InlineData(0.62, "slightly soft"), InlineData(1.03, "good")] // gameplay, portrait 60 fps B
    [InlineData(0.32, "slightly soft"), InlineData(0.64, "good"), InlineData(1.07, "good")]       // phone, portrait 30 fps
    public void DescriptionContainsTheMeasuredQuality(double score, string measured) =>
        Assert.Contains(measured, SizePlanner.DescribeScore(score));

    [Fact]
    public void EmptySelectionIsAClearError() =>
        Assert.Throws<EncodeException>(() => SizePlanner.Plan(new(Media(1920, 1080, 30, 60), Free, DiscordStrategy.Balanced)
            { TrimStart = TimeSpan.FromSeconds(50), TrimEnd = TimeSpan.FromSeconds(40) }));
}
