namespace Av1Encoder.Engine;

/// <summary>Expected output size and encoding time, as ranges. Time is null when it can't be estimated (hardware encoder, no speed sample).</summary>
public sealed record EncodeEstimate(long MinBytes, long MaxBytes, TimeSpan? MinTime, TimeSpan? MaxTime);

/// <summary>What a hardware encoder reached on short samples with specific settings (<see cref="SizeTargeting.SampleHardwareAsync"/>).</summary>
public sealed record HardwareSample(VideoEncoder Encoder, QualitySettings Quality, double VideoKbps, double Fps)
{
    /// <summary>The sample only describes encodes with the same encoder and quality settings.</summary>
    public bool Matches(EncodeRequest r) => r.Encoder == Encoder && r.Quality == Quality;
}

/// <summary>
/// Predicts size and time for any request from one quick check (<see cref="SizeTargeting.AnalyzeAsync"/>), so the
/// estimate follows preset, quality, trim and resolution changes instantly. Pure: no FFmpeg, no I/O.
/// Ranges come from comparing estimates with 40 real encodes of the calibration clips (docs/CALIBRATION.md).
/// </summary>
public static class EncodeEstimator
{
    // Size: CRF 26 / 38 / 44 gave 1.5x / 0.64x / 0.42x of CRF 32 on 9 of 10 calibration clips, i.e. ~7.3% per CRF step.
    public const double CrfStep = 0.073;
    // Size range: 29 of 30 CRF encodes landed within it (0.89-1.21x); the miss was a screen recording at CRF 40 (1.42x).
    public const double SizeLow = 0.85, SizeHigh = 1.25;
    // Time: real encodes took a median 0.8x of the sample-based figure (full encodes reach steadier speeds), so it is
    // scaled by that; 37 of 40 then landed within 0.7-1.4x, and the three misses were faster, not slower.
    public const double TimeScale = 0.8, TimeLow = 0.7, TimeHigh = 1.4;

    /// <summary>
    /// Encoding time relative to preset 8, timed on 10 s of 1080p60 gameplay and 4K30 phone footage with this build
    /// (7, 9 and 11 interpolated).
    /// </summary>
    public static double TimeFactor(int speed) => Math.Clamp(speed, 2, 12) switch
    {
        2 => 8.3, 3 => 4.9, 4 => 2.4, 5 => 2.0, 6 => 1.7, 7 => 1.3, 8 => 1.0, 9 => 0.86, 10 => 0.74, 11 => 0.73, _ => 0.72,
    };

    public static double CrfFactor(int crf) => Math.Exp(-CrfStep * (crf - 32));

    // 30 AMF encodes of the calibration clips: size 27 of 30 within 0.85-1.15x (the misses, a screen recording at 0.53-0.56x,
    // came out smaller); time 1.00-1.42x, median 1.08 (full encodes never ran faster than the samples), 29 of 30 within 0.95-1.35x.
    public const double HardwareSizeLow = 0.85, HardwareSizeHigh = 1.15, HardwareTimeLow = 0.95, HardwareTimeHigh = 1.35;

    /// <summary>Size and time for a hardware encode, straight from samples taken with the same settings.</summary>
    public static EncodeEstimate EstimateHardware(MediaInfo media, EncodeRequest request, HardwareSample sample)
    {
        var duration = request.OutputDuration(media);
        double audioBytes = AudioBytes(media, request, duration);
        double point = sample.VideoKbps * 1000 / 8.0 * duration.TotalSeconds + audioBytes;
        double seconds = duration.TotalSeconds * Math.Max(media.Video?.FrameRate ?? 30, 1) / Math.Max(sample.Fps, 0.001);
        return new((long)(point * HardwareSizeLow), (long)(point * HardwareSizeHigh),
            TimeSpan.FromSeconds(seconds * HardwareTimeLow), TimeSpan.FromSeconds(seconds * HardwareTimeHigh));
    }

    static double AudioBytes(MediaInfo media, EncodeRequest request, TimeSpan duration) =>
        FfmpegArguments.KeptAudio(media, request).Sum(a =>
            (FfmpegArguments.CanCopyAudio(a, request) ? a.BitRate ?? 128_000 : FfmpegArguments.AudioBitrateKbps(a, request) * 1000.0) / 8)
        * duration.TotalSeconds;

    public static EncodeEstimate Estimate(MediaInfo media, EncodeRequest request, ComplexityEstimate complexity)
    {
        var v = media.Video ?? throw new EncodeException("This file does not contain a video stream.");
        var duration = request.OutputDuration(media);
        var (w, h) = v.DisplaySize;
        int shortSide = Math.Max(1, Math.Min(w, h));
        double pixels = request.MaxShortSide is int side && side < shortSide ? Math.Pow((double)side / shortSide, 2) : 1;
        double sourceFps = Math.Max(v.FrameRate, 1);
        double fps = request.MaxFrameRate is double f && f < sourceFps - 0.01 ? f : sourceFps;
        var q = request.Quality;

        double audioBytes = AudioBytes(media, request, duration);

        long min, max;
        if (q.VideoBitrateKbps is int kbps)
        {
            // Size-targeted: the encoder aims at the bitrate; calibration encodes landed at 93-97% of it, static footage lower.
            double point = kbps * 1000 / 8.0 * duration.TotalSeconds + audioBytes;
            (min, max) = ((long)(point * 0.6), (long)point);
        }
        else
        {
            double videoKbps = complexity.Crf32Kbps * CrfFactor(q.Crf) * SizePlanner.PresetFactor(q.Speed)
                * Math.Pow(pixels, SizePlanner.PixelExponent) * Math.Pow(fps / sourceFps, SizePlanner.FrameRateExponent);
            double point = videoKbps * 1000 / 8.0 * duration.TotalSeconds + audioBytes;
            (min, max) = ((long)(point * SizeLow), (long)(point * SizeHigh));
        }

        if (request.Encoder != VideoEncoder.SvtAv1 || complexity.Preset8Fps is not double samplesFps || samplesFps <= 0)
            return new(min, max, null, null);
        // Speed scales with the pixels encoded per frame; fewer frames when the frame rate is lowered.
        double seconds = TimeScale * duration.TotalSeconds * fps / (samplesFps / TimeFactor(q.Speed) / pixels);
        if (request.TwoPass && q.VideoBitrateKbps != null) seconds /= 1 - EncodeJob.Pass1Share(q.Speed);
        return new(min, max, TimeSpan.FromSeconds(seconds * TimeLow), TimeSpan.FromSeconds(seconds * TimeHigh));
    }
}

/// <summary>How far one finished encode landed from its uncorrected estimate (actual / range midpoint).</summary>
public sealed record EstimateOutcome(VideoEncoder Encoder, double SizeRatio, double? TimeRatio, DateTime When);

/// <summary>
/// Learns from finished encodes on this PC: estimates are scaled by the median ratio of the last encodes with the same
/// encoder. Ratios are always taken against the uncorrected estimate, so corrections don't compound.
/// </summary>
public sealed class EstimateHistory
{
    public const int Keep = 30, MinOutcomes = 3;
    // One odd encode (a game running, a network hiccup) is outvoted by the median; this bounds what a run of them can do.
    public const double MinCorrection = 0.5, MaxCorrection = 2.0;

    public List<EstimateOutcome> Outcomes { get; set; } = [];

    /// <summary>Records a finished encode. Ignores encodes too short to time reliably and absurd ratios.</summary>
    public void Add(VideoEncoder encoder, EncodeEstimate uncorrected, long actualBytes, TimeSpan actualTime, TimeSpan outputDuration)
    {
        if (outputDuration < TimeSpan.FromSeconds(10)) return;
        double size = actualBytes / Mid(uncorrected.MinBytes, uncorrected.MaxBytes);
        double? time = uncorrected is { MinTime: { } lo, MaxTime: { } hi } ? actualTime.TotalSeconds / Mid(lo.TotalSeconds, hi.TotalSeconds) : null;
        if (size is < 0.2 or > 5 || time is < 0.2 or > 5) return;
        Outcomes.Add(new(encoder, size, time, DateTime.Now));
        foreach (var old in Outcomes.Where(o => o.Encoder == encoder).SkipLast(Keep).ToList()) Outcomes.Remove(old);
    }

    /// <summary>Size and time factors for an encoder, and how many encodes they come from (1.0 below <see cref="MinOutcomes"/>).</summary>
    public (double Size, double Time, int Count) Correction(VideoEncoder encoder)
    {
        var mine = Outcomes.Where(o => o.Encoder == encoder).ToList();
        var times = mine.Where(o => o.TimeRatio != null).Select(o => o.TimeRatio!.Value).ToList();
        double Factor(List<double> r) => r.Count < MinOutcomes ? 1 : Math.Clamp(Median(r), MinCorrection, MaxCorrection);
        return (Factor(mine.Select(o => o.SizeRatio).ToList()), Factor(times), mine.Count);
    }

    public EncodeEstimate Apply(EncodeEstimate e, VideoEncoder encoder)
    {
        var (size, time, _) = Correction(encoder);
        return new((long)(e.MinBytes * size), (long)(e.MaxBytes * size), e.MinTime * time, e.MaxTime * time);
    }

    static double Mid(double a, double b) => Math.Max((a + b) / 2, 1e-9);
    static double Median(List<double> v) { v.Sort(); return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2; }
}
