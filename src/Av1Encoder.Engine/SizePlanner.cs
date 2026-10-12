namespace Av1Encoder.Engine;

public enum DiscordStrategy { BestQuality, Balanced, Fastest }

/// <summary>How a Discord strategy maps onto the encoder. Users see only the strategy name.</summary>
/// <param name="Margin">Share of the size limit held back for container overhead and rate-control variance.</param>
public sealed record StrategySettings(int Speed, bool TwoPass, double Margin, int AudioKbps)
{
    // Two-pass landed within +3% of target in testing, one-pass between -24% and +5%; the margins cover that.
    // Presets: 4 is the app's quality preset; >= 10 is avoided (SVT-AV1 warns of visual artifacts).
    public static StrategySettings For(DiscordStrategy s) => s switch
    {
        DiscordStrategy.BestQuality => new(Speed: 4, TwoPass: true, Margin: 0.04, AudioKbps: 128),
        DiscordStrategy.Balanced => new(Speed: 6, TwoPass: true, Margin: 0.04, AudioKbps: 96),
        DiscordStrategy.Fastest => new(Speed: 8, TwoPass: false, Margin: 0.08, AudioKbps: 96),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}

public enum QualityLabel { Good, Softened, Blocky, Severe }

/// <summary>
/// How many kbps the video needs for Balanced-level quality (CRF 32) at its source resolution and frame rate, as SVT-AV1
/// preset 8 encodes it. From sample encodes (<see cref="SizeTargeting.AnalyzeAsync"/>) or, when rough, from resolution alone.
/// </summary>
public sealed record ComplexityEstimate(double Crf32Kbps, double PeakCrf32Kbps, bool IsRough);

/// <summary>What the user chose. Null overrides mean "Auto".</summary>
public sealed record PlanInput(MediaInfo Media, long TargetBytes, DiscordStrategy Strategy)
{
    public TimeSpan? TrimStart { get; init; }
    public TimeSpan? TrimEnd { get; init; }
    public ComplexityEstimate? Complexity { get; init; }
    /// <summary>Resolution override: short side in pixels, or 0 for the original.</summary>
    public int? ShortSide { get; init; }
    /// <summary>Frame-rate override: a cap, or 0 for the original.</summary>
    public double? FrameRate { get; init; }
    /// <summary>Audio override in kbps; 0 removes audio.</summary>
    public int? AudioKbps { get; init; }
}

/// <summary>A recommended encode for a size target, with plain-English reasons. Users can accept it or override parts.</summary>
public sealed record DiscordPlan(
    int? MaxShortSide, double? MaxFrameRate, int AudioKbps, int VideoKbps, TimeSpan Duration,
    double Score, QualityLabel Label, bool IsRough, int FloorKbps, long ExpectedBytes,
    string Summary, IReadOnlyList<string> Advice, IReadOnlyList<string> Warnings, StrategySettings Settings, int? AudioStreamIndex)
{
    /// <summary>The engine request for this plan.</summary>
    public EncodeRequest Apply(EncodeRequest r, PlanInput input) => r with
    {
        Quality = r.Quality with { Speed = Settings.Speed },
        TwoPass = Settings.TwoPass,
        TrimStart = input.TrimStart, TrimEnd = input.TrimEnd,
        MaxShortSide = MaxShortSide, MaxFrameRate = MaxFrameRate,
        IncludeAudio = AudioKbps > 0, AudioBitrateKbps = AudioKbps > 0 ? AudioKbps : null,
        AudioStreamIndexes = AudioStreamIndex is int i ? [i] : null, DownmixToStereo = true,
        IncludeSubtitles = true,
    };
}

/// <summary>
/// Chooses resolution, frame rate and audio for a size target and explains the trade-offs. Pure: no FFmpeg, no I/O.
/// Every constant marked PROVISIONAL is a starting point until the calibration study (docs/DESIGN.md) replaces it.
/// </summary>
public static class SizePlanner
{
    // PROVISIONAL. Score bands: budget / bitrate needed for CRF-32 quality. Sintel: 0.68x of CRF-32 size = CRF 38 (VMAF 93.7),
    // 0.47x = CRF 44 (VMAF 91.7). One clean clip only.
    public const double GoodScore = 0.7, SoftenedScore = 0.45, BlockyScore = 0.25;
    // PROVISIONAL. Bits needed scale sub-linearly with pixel count and frame rate.
    public const double PixelExponent = 0.75, FrameRateExponent = 0.5;
    // PROVISIONAL. Without sample encodes: bits per pixel per frame that CRF 32 needs on moderately busy footage.
    // Sintel (clean animation) needed 0.015; gameplay and noisy phone video are commonly 2-3x that.
    public const double RoughBitsPerPixel = 0.03;
    // Sintel benchmark, CRF 32: preset 4 = 4.74 MB, 6 = 5.22 MB, 8 = 5.89 MB. Analysis encodes at preset 8.
    public static double PresetFactor(int speed) => speed <= 4 ? 0.805 : speed <= 6 ? 0.886 : 1.0;

    /// <summary>PROVISIONAL quality floor per strategy (minimum score); retries stop below it.</summary>
    public static double FloorScore(DiscordStrategy s) => s switch
    {
        DiscordStrategy.BestQuality => 0.45, DiscordStrategy.Balanced => 0.3, _ => 0.25,
    };

    static double AcceptScore(DiscordStrategy s) => s == DiscordStrategy.BestQuality ? GoodScore : SoftenedScore;

    public static QualityLabel LabelFor(double score) =>
        score >= GoodScore ? QualityLabel.Good : score >= SoftenedScore ? QualityLabel.Softened : score >= BlockyScore ? QualityLabel.Blocky : QualityLabel.Severe;

    public static ComplexityEstimate RoughComplexity(VideoStreamInfo v) =>
        new(RoughBitsPerPixel * v.Width * v.Height * Math.Max(v.FrameRate, 1) / 1000, 0, IsRough: true);

    record Candidate(int? Side, double? Fps, int ShortSide, double FrameRate, double NeededKbps);

    public static DiscordPlan Plan(PlanInput input)
    {
        var media = input.Media;
        var v = media.Video ?? throw new EncodeException("This file does not contain a video stream.");
        var settings = StrategySettings.For(input.Strategy);
        var probe = new EncodeRequest { InputPath = media.Path, OutputPath = "", Quality = new(32, settings.Speed, 26), TrimStart = input.TrimStart, TrimEnd = input.TrimEnd };
        var duration = probe.OutputDuration(media);
        if (duration <= TimeSpan.Zero) throw new EncodeException("The selected part of the video is empty. Check the start and end times.");

        var audioTrack = media.DefaultAudio;
        int audioKbps = audioTrack is null ? 0 : input.AudioKbps ?? settings.AudioKbps;
        int videoKbps = SizeTargeting.CalculateVideoBitrateKbps(input.TargetBytes, duration, audioKbps, 1 - settings.Margin);
        var complexity = input.Complexity ?? RoughComplexity(v);

        var (w, h) = v.DisplaySize;
        int sourceShort = Math.Min(w, h);
        double sourceFps = v.FrameRate > 0 ? v.FrameRate : 30;
        Candidate Make(int? side, double? fps)
        {
            int s = side is int x && x < sourceShort ? x : sourceShort;
            double f = fps is double y && y < sourceFps - 0.01 ? y : sourceFps;
            double needed = complexity.Crf32Kbps * PresetFactor(settings.Speed)
                * Math.Pow((double)s / sourceShort, 2 * PixelExponent) * Math.Pow(f / sourceFps, FrameRateExponent);
            return new(s < sourceShort ? s : null, f < sourceFps ? f : null, s, f, needed);
        }
        double Score(Candidate c) => videoKbps / c.NeededKbps;

        // Auto ladder, in order of preference: keep frame rate and step resolution down to 720p; then 30 fps at
        // 720p or above; then 480p. Overrides pin one dimension.
        int[] sides = input.ShortSide is int fixedSide ? [fixedSide == 0 ? sourceShort : fixedSide]
            : new[] { sourceShort, 1080, 720, 480 }.Where(x => x <= sourceShort).Distinct().ToArray();
        double?[] rates = input.FrameRate is double fixedFps ? [fixedFps == 0 ? null : fixedFps]
            : sourceFps > 31 ? [null, 30] : [null];
        var ladder = new List<Candidate>();
        foreach (var fps in rates)
            foreach (var side in sides.Where(x => x >= 720 || x == sourceShort))
                ladder.Add(Make(side, fps));
        foreach (var fps in rates)
            foreach (var side in sides.Where(x => x < 720 && x != sourceShort))
                ladder.Add(Make(side, fps));

        double accept = AcceptScore(input.Strategy);
        var chosen = ladder.FirstOrDefault(c => Score(c) >= accept) ?? ladder.MaxBy(Score)!;
        // Below the engine's absolute minimum nothing can be encoded, however small the picture.
        double score = videoKbps < SizeTargeting.MinVideoKbps ? 0 : Score(chosen);
        var label = LabelFor(score);
        int floor = Math.Max(SizeTargeting.MinVideoKbps, (int)Math.Ceiling(FloorScore(input.Strategy) * chosen.NeededKbps));

        var advice = new List<string>();
        if (media.SizeBytes <= input.TargetBytes && input.TrimStart is null && input.TrimEnd is null)
            advice.Add($"This file already fits ({media.SizeBytes / 1048576.0:0.0} MB). You can upload the original as it is.");
        var original = ladder[0];
        string Name(Candidate c) => $"{c.ShortSide}p{Math.Round(c.FrameRate)}";

        // Trimming keeps resolution and frame rate; offer it when the plan had to give one of them up, or when nothing fits well.
        var reference = Make(Math.Min(sourceShort, 1080), null);
        bool gaveUpSomething = chosen.Side != null && chosen.ShortSide < reference.ShortSide || chosen.Fps != null;
        if (Score(reference) < accept && (gaveUpSomething || score < accept))
        {
            var fits = TimeSpan.FromSeconds(Math.Floor(duration.TotalSeconds * Score(reference) / accept));
            if (fits >= TimeSpan.FromSeconds(3))
                advice.Add($"Trimming keeps the most quality: at {Name(reference)} this would likely look {(accept >= GoodScore ? "good" : "acceptable")} for clips up to about {Clock(fits)}. Pick the important part?");
        }
        if (chosen.Fps != null)
            advice.Add($"Frame rate lowered to {Math.Round(chosen.FrameRate)} fps to save space. Fast camera movement and gameplay will look less smooth.");
        if (audioKbps > 0 && audioKbps >= 0.10 * (audioKbps + videoKbps) && audioKbps > 64)
            advice.Add($"Audio uses {100.0 * audioKbps / (audioKbps + videoKbps):0}% of the space; 64 kbps audio would give the video {100.0 * (audioKbps - 64) / videoKbps:0}% more room.");
        if (videoKbps < SizeTargeting.MinVideoKbps)
            advice.Add("This selection is too long for the size limit at any setting. Trim it, lower or remove the audio, or use a larger limit.");
        else if (score < BlockyScore)
            advice.Add("Even with these changes the video will likely look very blocky. Trimming it, or a larger size limit, is the only real fix.");

        string summary = (chosen == original
                ? $"Keeps {Name(chosen)}."
                : $"Lowers to {Name(chosen)}: at {Name(original)} it would likely look {Describe(LabelFor(Score(original)))}.")
            + $" Expected quality: {Describe(label)}" + (complexity.IsRough ? " (rough estimate)." : ".");

        var warnings = new List<string>();
        if (v.IsHdr)
            warnings.Add("This is an HDR video. Discord and most screens show HDR AV1 with washed-out or wrong colours; the colours may not look like the original.");
        if (audioKbps > 0 && media.Audio.Count > 1)
            warnings.Add($"Only the main audio track is kept. Not included: {string.Join("; ", media.Audio.Where(a => a != audioTrack).Select(a => a.Describe()))}.");
        if (audioKbps > 0 && audioTrack!.Channels > 2)
            warnings.Add($"The {audioTrack.Channels}-channel audio will be downmixed to stereo.");
        if (media.Subtitles.Any(s => !s.IsText))
            warnings.Add("Image-based subtitles can't be stored in MP4 and will not be included.");

        long expected = (long)((videoKbps + audioKbps) * 1000 / 8.0 * duration.TotalSeconds);
        return new DiscordPlan(chosen.Side, chosen.Fps, audioKbps, videoKbps, duration, score, label, complexity.IsRough, floor,
            Math.Min(expected, input.TargetBytes), summary, advice, warnings, settings, audioKbps > 0 ? audioTrack?.Index : null);
    }

    public static string Describe(QualityLabel l) => l switch
    {
        QualityLabel.Good => "good", QualityLabel.Softened => "slightly soft", QualityLabel.Blocky => "noticeably blocky", _ => "very blocky",
    };

    static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
