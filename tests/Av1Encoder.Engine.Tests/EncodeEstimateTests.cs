using Av1Encoder.Engine;

namespace Av1Encoder.Engine.Tests;

public class EncodeEstimateTests
{
    static MediaInfo Media(double seconds = 60, long? audioBps = 160_000) => new(
        @"C:\in\clip.mp4", "mov", "QuickTime", 100_000_000, TimeSpan.FromSeconds(seconds), null,
        new VideoStreamInfo(0, "h264", 1920, 1080, 60, false, 0, "yuv420p", 8, null),
        [new AudioStreamInfo(1, "aac", 2, "stereo", 48000, audioBps, null)], [], 0, 0, 0);

    static EncodeRequest Request(int crf = 32, int speed = 8) =>
        new() { InputPath = @"C:\in\clip.mp4", OutputPath = @"C:\out\x.mp4", Quality = new QualitySettings(crf, speed, 26) };

    // 4000 kbps at CRF 32 / preset 8; samples encoded 120 source frames per second.
    static readonly ComplexityEstimate Measured = new(4000, 4000, false, Preset8Fps: 120);

    static double Mid(EncodeEstimate e) => (e.MinBytes + e.MaxBytes) / 2.0;
    static double MidSeconds(EncodeEstimate e) => (e.MinTime!.Value + e.MaxTime!.Value).TotalSeconds / 2;

    [Fact]
    public void PointEstimateMatchesTheMeasurement()
    {
        var e = EncodeEstimator.Estimate(Media(), Request(), Measured);
        double expected = 4000 * 1000 / 8.0 * 60 + 160_000 / 8.0 * 60;   // video + copied AAC
        Assert.Equal(expected * EncodeEstimator.SizeLow, e.MinBytes, tolerance: 10);
        Assert.Equal(expected * EncodeEstimator.SizeHigh, e.MaxBytes, tolerance: 10);
        // 3600 frames at 120 fps = 30 s, scaled by the calibrated factor
        Assert.Equal(30 * EncodeEstimator.TimeScale * EncodeEstimator.TimeLow, e.MinTime!.Value.TotalSeconds, 3);
        Assert.Equal(30 * EncodeEstimator.TimeScale * EncodeEstimator.TimeHigh, e.MaxTime!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void LowerCrfMeansBiggerAndSlowerPresetMeansLonger()
    {
        var baseline = EncodeEstimator.Estimate(Media(), Request(), Measured);
        Assert.True(Mid(EncodeEstimator.Estimate(Media(), Request(crf: 26), Measured)) > Mid(baseline));
        Assert.InRange(EncodeEstimator.CrfFactor(38), 0.62, 0.67);
        Assert.InRange(EncodeEstimator.CrfFactor(26), 1.5, 1.6);
        var slow = EncodeEstimator.Estimate(Media(), Request(speed: 4), Measured);
        Assert.Equal(EncodeEstimator.TimeFactor(4), MidSeconds(slow) / MidSeconds(baseline), 3);
        Assert.True(Mid(slow) < Mid(baseline));            // slower presets compress better
    }

    [Fact]
    public void TrimScalesSizeAndTime()
    {
        var whole = EncodeEstimator.Estimate(Media(), Request(), Measured);
        var half = EncodeEstimator.Estimate(Media(), Request() with { TrimStart = TimeSpan.FromSeconds(30) }, Measured);
        Assert.Equal(Mid(whole) / 2, Mid(half), tolerance: 100);
        Assert.Equal(MidSeconds(whole) / 2, MidSeconds(half), 3);
    }

    [Fact]
    public void DownscalingAndLowerFrameRateReduceSizeAndTime()
    {
        var whole = EncodeEstimator.Estimate(Media(), Request(), Measured);
        var small = EncodeEstimator.Estimate(Media(), Request() with { MaxShortSide = 540, MaxFrameRate = 30 }, Measured);
        Assert.True(Mid(small) < Mid(whole) * 0.6);
        Assert.Equal(MidSeconds(whole) / 8, MidSeconds(small), 3);   // a quarter of the pixels, half the frames
    }

    [Fact]
    public void TwoPassBitrateEncodeCountsBothPassesAndCapsSize()
    {
        var r = Request(speed: 6) with { Quality = new QualitySettings(32, 6, 26, VideoBitrateKbps: 2000), TwoPass = true, AudioBitrateKbps = 96 };
        var e = EncodeEstimator.Estimate(Media(), r, Measured);
        Assert.Equal((2000 + 96) * 1000 / 8.0 * 60, e.MaxBytes, tolerance: 10);
        double onePass = EncodeEstimator.TimeScale * 60 * 60 / (120 / EncodeEstimator.TimeFactor(6));
        Assert.Equal(onePass / (1 - EncodeJob.Pass1Share(6)) * (EncodeEstimator.TimeLow + EncodeEstimator.TimeHigh) / 2, MidSeconds(e), 3);
    }

    [Fact]
    public void HardwareEstimateComesStraightFromTheGpuSample()
    {
        var request = Request() with { Encoder = VideoEncoder.Amf };
        var sample = new HardwareSample(VideoEncoder.Amf, request.Quality, VideoKbps: 6000, Fps: 240);
        Assert.True(sample.Matches(request));
        Assert.False(sample.Matches(request with { Quality = QualitySettings.For(QualityPreset.HighQuality) }));
        Assert.False(sample.Matches(request with { Encoder = VideoEncoder.SvtAv1 }));

        var e = EncodeEstimator.EstimateHardware(Media(), request, sample);
        double point = 6000 * 1000 / 8.0 * 60 + 160_000 / 8.0 * 60;    // GPU video bitrate + copied audio
        Assert.Equal(point * EncodeEstimator.HardwareSizeLow, e.MinBytes, tolerance: 10);
        Assert.Equal(point * EncodeEstimator.HardwareSizeHigh, e.MaxBytes, tolerance: 10);
        Assert.Equal(15 * EncodeEstimator.HardwareTimeLow, e.MinTime!.Value.TotalSeconds, 3);   // 3600 frames at 240 fps
    }

    [Fact]
    public void NoTimeForHardwareOrWithoutASpeedSample()
    {
        Assert.Null(EncodeEstimator.Estimate(Media(), Request() with { Encoder = VideoEncoder.Amf }, Measured).MinTime);
        Assert.Null(EncodeEstimator.Estimate(Media(), Request(), Measured with { Preset8Fps = null }).MaxTime);
    }

    [Fact]
    public void ReencodedAndRemovedAudioAreCounted()
    {
        var copy = EncodeEstimator.Estimate(Media(audioBps: 320_000), Request(), Measured);
        var reencoded = EncodeEstimator.Estimate(Media(audioBps: 320_000), Request() with { AudioBitrateKbps = 96 }, Measured);
        var none = EncodeEstimator.Estimate(Media(audioBps: 320_000), Request() with { IncludeAudio = false }, Measured);
        Assert.True(Mid(copy) > Mid(reencoded) && Mid(reencoded) > Mid(none));
        Assert.Equal(96_000 / 8.0 * 60 * (EncodeEstimator.SizeLow + EncodeEstimator.SizeHigh) / 2, Mid(reencoded) - Mid(none), tolerance: 10);
    }
}

public class EstimateHistoryTests
{
    static readonly EncodeEstimate Raw = new(80, 120, TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(120));
    static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public void LearnsTheMedianRatioPerEncoderAfterEnoughEncodes()
    {
        var h = new EstimateHistory();
        h.Add(VideoEncoder.Amf, Raw, 100, TimeSpan.FromSeconds(150), Minute);
        h.Add(VideoEncoder.Amf, Raw, 100, TimeSpan.FromSeconds(150), Minute);
        Assert.Equal((1.0, 1.0, 2), h.Correction(VideoEncoder.Amf));        // too few encodes yet
        h.Add(VideoEncoder.Amf, Raw, 110, TimeSpan.FromSeconds(400), Minute); // one slow outlier
        var (size, time, count) = h.Correction(VideoEncoder.Amf);
        Assert.Equal(3, count);
        Assert.Equal(1.0, size, 3);
        Assert.Equal(1.5, time, 3);                                         // median ignores the outlier
        Assert.Equal(1.0, h.Correction(VideoEncoder.SvtAv1).Time);          // other encoders untouched
        var corrected = h.Apply(Raw, VideoEncoder.Amf);
        Assert.Equal(TimeSpan.FromSeconds(120), corrected.MinTime);
        Assert.Equal(80, corrected.MinBytes);
    }

    [Fact]
    public void SurvivesAJsonRoundTrip()
    {
        var h = new EstimateHistory();
        for (int i = 0; i < 3; i++) h.Add(VideoEncoder.Amf, Raw, 100, TimeSpan.FromSeconds(150), Minute);
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var back = System.Text.Json.JsonSerializer.Deserialize<EstimateHistory>(System.Text.Json.JsonSerializer.Serialize(h, options), options)!;
        Assert.Equal(h.Correction(VideoEncoder.Amf), back.Correction(VideoEncoder.Amf));
    }

    [Fact]
    public void IgnoresShortEncodesAbsurdRatiosAndKeepsTheLatest()
    {
        var h = new EstimateHistory();
        h.Add(VideoEncoder.SvtAv1, Raw, 100, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(5));
        h.Add(VideoEncoder.SvtAv1, Raw, 100_000, TimeSpan.FromSeconds(100), Minute);
        Assert.Empty(h.Outcomes);
        for (int i = 0; i < EstimateHistory.Keep + 5; i++) h.Add(VideoEncoder.SvtAv1, Raw, 100, TimeSpan.FromSeconds(400), Minute);
        Assert.Equal(EstimateHistory.Keep, h.Outcomes.Count);
        Assert.Equal(EstimateHistory.MaxCorrection, h.Correction(VideoEncoder.SvtAv1).Time); // 4x slower, capped
    }
}
