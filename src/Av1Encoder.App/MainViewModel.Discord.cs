using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using Av1Encoder.Engine;

namespace Av1Encoder.App;

public sealed record Choice<T>(string Label, T Value);

/// <summary>Video to Discord mode: size target, strategy, trim, quality check and recommendation.</summary>
public sealed partial class MainViewModel
{
    /// <summary>19 MiB: under Discord's free limit, which the upload test put between 20,429,630 and 21,561,137 bytes.</summary>
    public const long DiscordFreeBytes = 19_922_944;

    ComplexityEstimate? complexity;
    CancellationTokenSource? analysisCts, trimDebounce;
    DiscordPlan? plan;
    PlanInput? planInput;
    TimeSpan? trimStart, trimEnd;
    string? trimError, targetError;

    // ---------- Mode ----------

    public bool IsDiscordMode
    {
        get => settings.DiscordMode;
        set
        {
            if (settings.DiscordMode == value) return;
            settings.DiscordMode = value;
            settings.Save();
            if (media != null && OutputFileName == DefaultOutputName(media, !value)) OutputFileName = DefaultOutputName(media, value);
            UpdateContainerNote();
            Raise(nameof(IsDiscordMode), nameof(IsCompressMode));
            if (complexity == null && !IsAnalyzing) StartAnalysis();
            Replan();
        }
    }
    public bool IsCompressMode { get => !IsDiscordMode; set => IsDiscordMode = !value; }

    void OnDiscordMediaLoaded()
    {
        var v = media!.Video!;
        var (w, h) = v.DisplaySize;
        int shortSide = Math.Min(w, h);
        ResolutionChoices.Clear();
        ResolutionChoices.Add(new("Auto (recommended)", null));
        ResolutionChoices.Add(new($"Original ({w} × {h})", 0));
        foreach (var side in new[] { 1080, 720, 480 }.Where(s => s < shortSide)) ResolutionChoices.Add(new($"{side}p", side));
        FrameRateChoices.Clear();
        FrameRateChoices.Add(new("Auto (recommended)", null));
        FrameRateChoices.Add(new($"Original ({v.FrameRate:0.##} fps)", 0));
        foreach (var fps in new[] { 60.0, 30, 24 }.Where(f => f < v.FrameRate - 0.5)) FrameRateChoices.Add(new($"{fps:0} fps", fps));
        AudioChoices.Clear();
        if (media.Audio.Count == 0) AudioChoices.Add(new("No audio in this video", null));
        else
        {
            AudioChoices.Add(new("Auto (recommended)", null));
            foreach (var kbps in new[] { 128, 96, 64 }) AudioChoices.Add(new($"{kbps} kbps", kbps));
            AudioChoices.Add(new("Remove audio", 0));
        }
        selectedResolution = ResolutionChoices[0]; selectedFrameRate = FrameRateChoices[0]; selectedAudio = AudioChoices[0];
        Raise(nameof(SelectedResolution), nameof(SelectedFrameRate), nameof(SelectedAudio));

        trimStartText = Clock(TimeSpan.Zero);
        trimEndText = Clock(media.Duration);
        trimStart = trimEnd = null;
        trimError = null;
        Raise(nameof(TrimStartText), nameof(TrimEndText), nameof(TrimError), nameof(HasTrimError));
        complexity = null;
        StartAnalysis();
        RefreshThumbnails();
        Replan();
    }

    // ---------- Size target ----------

    public bool UseFreeTarget { get => !settings.DiscordUseCustomTarget; set { if (value) SetCustomTarget(false); } }
    public bool UseCustomTarget { get => settings.DiscordUseCustomTarget; set { if (value) SetCustomTarget(true); } }

    void SetCustomTarget(bool custom)
    {
        settings.DiscordUseCustomTarget = custom;
        settings.Save();
        Raise(nameof(UseFreeTarget), nameof(UseCustomTarget), nameof(TargetBytesText));
        Replan();
    }

    string? customTargetText;
    public string CustomTargetText
    {
        get => customTargetText ??= settings.DiscordCustomTargetMB.ToString("0.##", CultureInfo.CurrentCulture);
        set
        {
            customTargetText = value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var mb) && mb is >= 1 and <= 100_000)
            {
                settings.DiscordCustomTargetMB = mb;
                settings.Save();
                targetError = null;
            }
            else targetError = "Enter a size limit in MB between 1 and 100,000, for example 49.";
            Raise(nameof(CustomTargetText), nameof(TargetBytesText));
            Replan();
        }
    }

    /// <summary>The limit in bytes. MB here means MiB (1,048,576 bytes), matching Explorer and the rest of the app.</summary>
    public long TargetBytes => UseCustomTarget ? (long)Math.Round(settings.DiscordCustomTargetMB * 1048576) : DiscordFreeBytes;
    public string TargetBytesText => $"= {TargetBytes:N0} bytes";

    // ---------- Strategy ----------

    public bool IsBestStrategy { get => settings.DiscordStrategy == DiscordStrategy.BestQuality; set { if (value) SetStrategy(DiscordStrategy.BestQuality); } }
    public bool IsBalancedStrategy { get => settings.DiscordStrategy == DiscordStrategy.Balanced; set { if (value) SetStrategy(DiscordStrategy.Balanced); } }
    public bool IsFastestStrategy { get => settings.DiscordStrategy == DiscordStrategy.Fastest; set { if (value) SetStrategy(DiscordStrategy.Fastest); } }

    void SetStrategy(DiscordStrategy s)
    {
        settings.DiscordStrategy = s;
        settings.Save();
        Raise(nameof(IsBestStrategy), nameof(IsBalancedStrategy), nameof(IsFastestStrategy));
        Replan();
    }

    // ---------- Trim ----------

    string trimStartText = "", trimEndText = "";
    public string TrimStartText { get => trimStartText; set { Set(ref trimStartText, value); OnTrimChanged(); } }
    public string TrimEndText { get => trimEndText; set { Set(ref trimEndText, value); OnTrimChanged(); } }
    public string? TrimError => trimError;
    public bool HasTrimError => trimError != null;

    BitmapImage? startFrame, endFrame;
    public BitmapImage? StartFrame { get => startFrame; private set => Set(ref startFrame, value); }
    public BitmapImage? EndFrame { get => endFrame; private set => Set(ref endFrame, value); }

    void OnTrimChanged()
    {
        if (media == null) return;
        var start = ParseTime(trimStartText);
        var end = ParseTime(trimEndText);
        trimError = start is null ? "Start time: use m:ss, for example 1:05 or 1:05.5."
            : end is null ? "End time: use m:ss, for example 2:30."
            : end > media.Duration + TimeSpan.FromSeconds(0.05) ? $"End time is past the end of the video ({Clock(media.Duration)})."
            : start >= end ? "The end time must be after the start time."
            : null;
        if (trimError == null)
        {
            trimStart = start > TimeSpan.Zero ? start : null;
            trimEnd = end < media.Duration - TimeSpan.FromSeconds(0.05) ? end : null;
        }
        Raise(nameof(TrimError), nameof(HasTrimError));
        Replan();
        DebounceTrimWork();
    }

    /// <summary>The quality check and thumbnails follow the trim range, a second after typing stops.</summary>
    async void DebounceTrimWork()
    {
        trimDebounce?.Cancel();
        var cts = trimDebounce = new CancellationTokenSource();
        try { await Task.Delay(1000, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (trimError != null) return;
        StartAnalysis();
        RefreshThumbnails();
    }

    async void RefreshThumbnails()
    {
        if (media == null || tools == null) return;
        var m = media;
        var start = trimStart ?? TimeSpan.Zero;
        var end = (trimEnd ?? m.Duration) - TimeSpan.FromSeconds(0.1);
        StartFrame = await GrabFrame(m, start);
        EndFrame = await GrabFrame(m, end > start ? end : start);
    }

    async Task<BitmapImage?> GrabFrame(MediaInfo m, TimeSpan at)
    {
        var png = Path.Combine(Path.GetTempPath(), $"av1-frame-{Guid.NewGuid():N}.png");
        try
        {
            await Task.Run(() => MediaProbe.ExtractFrameAsync(tools!, m, at, png));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read now so the file can be deleted
            image.UriSource = new Uri(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is EncodeException or IOException or NotSupportedException)
        {
            AppLog.Write($"Preview frame at {at} failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(png); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>"1:05", "1:05.5", "1:02:03" or plain seconds "65".</summary>
    internal static TimeSpan? ParseTime(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return null;
        double seconds = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0) return null;
            seconds = seconds * 60 + n;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    static string Clock(TimeSpan t) => (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss")) + $".{t.Milliseconds / 100}";

    // ---------- Overrides ----------

    public ObservableCollection<Choice<int?>> ResolutionChoices { get; } = new();
    public ObservableCollection<Choice<double?>> FrameRateChoices { get; } = new();
    public ObservableCollection<Choice<int?>> AudioChoices { get; } = new();

    Choice<int?>? selectedResolution, selectedAudio;
    Choice<double?>? selectedFrameRate;
    public Choice<int?>? SelectedResolution { get => selectedResolution; set { if (value == null) return; Set(ref selectedResolution, value); Replan(); } }
    public Choice<double?>? SelectedFrameRate { get => selectedFrameRate; set { if (value == null) return; Set(ref selectedFrameRate, value); Replan(); } }
    public Choice<int?>? SelectedAudio { get => selectedAudio; set { if (value == null) return; Set(ref selectedAudio, value); Replan(); } }

    public void ResetOverrides()
    {
        if (ResolutionChoices.Count == 0) return;
        SelectedResolution = ResolutionChoices[0];
        SelectedFrameRate = FrameRateChoices[0];
        SelectedAudio = AudioChoices[0];
    }

    // ---------- Quality check ----------

    public bool SkipQualityAnalysis
    {
        get => settings.SkipQualityAnalysis;
        set
        {
            settings.SkipQualityAnalysis = value;
            settings.Save();
            Raise(nameof(SkipQualityAnalysis));
            if (value) CancelAnalysis(); else StartAnalysis();
        }
    }

    bool isAnalyzing;
    public bool IsAnalyzing { get => isAnalyzing; private set => Set(ref isAnalyzing, value); }
    double analysisPercent;
    public double AnalysisPercent { get => analysisPercent; private set => Set(ref analysisPercent, value); }
    string analysisStatus = "";
    public string AnalysisStatus { get => analysisStatus; private set => Set(ref analysisStatus, value); }

    public void CancelAnalysis() => analysisCts?.Cancel();

    /// <summary>Skip: stop now and don't run it again until the box is unticked.</summary>
    public void SkipAnalysisNow() => SkipQualityAnalysis = true;

    async void StartAnalysis()
    {
        analysisCts?.Cancel();
        complexity = null;
        // Feeds both modes: Discord recommendations and the size/time estimate. It follows the Discord trim range;
        // Compress mode scales the result to the whole video.
        if (media?.Video == null || tools == null || IsBusy) return;
        if (SkipQualityAnalysis)
        {
            AnalysisStatus = "Quality check skipped.";
            Replan();
            return;
        }
        var cts = analysisCts = new CancellationTokenSource();
        var (m, s, e) = (media, trimStart, trimEnd);
        IsAnalyzing = true;
        AnalysisPercent = 0;
        AnalysisStatus = "Checking how this video compresses (a few seconds)…";
        var progress = new Progress<double>(p => { if (cts == analysisCts) AnalysisPercent = p * 100; }); // created here: reports land on the UI thread
        try
        {
            var estimate = await Task.Run(() => SizeTargeting.AnalyzeAsync(tools, m, s, e, progress, cts.Token));
            if (cts != analysisCts) return;
            complexity = estimate;
            AnalysisStatus = "Quality check done.";
            AppLog.Write($"Quality check: CRF-32 needs {estimate.Crf32Kbps:0} kbps (peak sample {estimate.PeakCrf32Kbps:0}).");
        }
        catch (OperationCanceledException)
        {
            if (cts == analysisCts) AnalysisStatus = "Quality check stopped.";
        }
        catch (Exception ex) // async void: nothing may escape
        {
            AppLog.Write($"Quality check failed: {ex.Message} {(ex as EncodeException)?.Details}");
            if (cts == analysisCts) AnalysisStatus = "The quality check failed; see the log.";
        }
        finally
        {
            if (cts == analysisCts)
            {
                IsAnalyzing = false;
                Replan();
            }
        }
    }

    // ---------- Recommendation ----------

    string? planError;
    public string? PlanError { get => planError; private set { Set(ref planError, value); Raise(nameof(HasPlanError)); } }
    public bool HasPlanError => planError != null;
    public bool HasPlan => plan != null;
    public string PlanSummary => plan?.Summary ?? "";
    public string PlanHeadline => plan == null ? "" : $"Expected about {Format.Size(plan.ExpectedBytes)} · quality likely {SizePlanner.DescribeScore(plan.Score)}";
    public bool PlanIsRisky => plan is { Label: QualityLabel.Blocky or QualityLabel.Severe };
    public string PlanAdvice => plan == null ? "" : string.Join(Environment.NewLine, plan.Advice.Select(a => "• " + a));
    public bool HasPlanAdvice => plan is { Advice.Count: > 0 };
    public string PlanWarnings => plan == null ? "" : string.Join(Environment.NewLine, plan.Warnings.Select(w => "• " + w));
    public bool HasPlanWarnings => plan is { Warnings.Count: > 0 };
    public string SizeLine => media == null ? "" :
        $"Source {Format.Size(media.SizeBytes)}  →  Limit {Format.Size(TargetBytes)}" + (plan != null ? $"  →  Expected ≈ {Format.Size(plan.ExpectedBytes)}" : "");
    public string SelectedDurationText => media == null ? "" : $"Selected {Clock(plan?.Duration ?? new EncodeRequest { InputPath = "", OutputPath = "", Quality = QualitySettings.For(QualityPreset.Balanced), TrimStart = trimStart, TrimEnd = trimEnd }.OutputDuration(media))}";
    /// <summary>The plan's bitrate is under its quality floor: encoding needs the user's explicit go-ahead.</summary>
    public bool PlanBelowFloor => plan != null && plan.VideoKbps < plan.FloorKbps;

    void Replan()
    {
        plan = null;
        planInput = null;
        string? error = null;
        if (IsDiscordMode && media?.Video != null)
        {
            error = trimError ?? (UseCustomTarget ? targetError : null);
            if (error == null)
            {
                try
                {
                    planInput = new PlanInput(media, TargetBytes, settings.DiscordStrategy)
                    {
                        TrimStart = trimStart, TrimEnd = trimEnd, Complexity = complexity,
                        ShortSide = SelectedResolution?.Value, FrameRate = SelectedFrameRate?.Value, AudioKbps = SelectedAudio?.Value,
                    };
                    plan = SizePlanner.Plan(planInput);
                }
                catch (EncodeException ex) { error = ex.Message; }
            }
        }
        PlanError = error;
        Raise(nameof(HasPlan), nameof(PlanSummary), nameof(PlanHeadline), nameof(PlanIsRisky), nameof(PlanAdvice), nameof(HasPlanAdvice),
            nameof(PlanWarnings), nameof(HasPlanWarnings), nameof(SizeLine), nameof(SelectedDurationText), nameof(PlanBelowFloor), nameof(CanStart));
    }

    // ---------- Encode ----------

    bool canEncodeAnyway;
    /// <summary>After the size target failed at the quality floor: offer to encode below it.</summary>
    public bool CanEncodeAnyway { get => canEncodeAnyway; private set => Set(ref canEncodeAnyway, value); }

    /// <summary>Space the Discord encode may need: the finished file plus a temp copy and pass log.</summary>
    public long DiscordRequiredBytes => 2 * TargetBytes + 16L * 1024 * 1024;

    public Task StartDiscordAsync(string outputPath, bool overwrite, bool ignoreFloor)
    {
        if (plan == null || planInput == null) return Task.CompletedTask;
        CancelAnalysis();
        var (p, input, target, m) = (plan, planInput, TargetBytes, media!);
        var request = p.Apply(new EncodeRequest
        {
            InputPath = m.Path, OutputPath = outputPath, Quality = QualitySettings.For(QualityPreset.Balanced),
            OverwriteExisting = overwrite, SvtParams = settings.SvtParams,
        }, input);
        int floor = ignoreFloor ? SizeTargeting.MinVideoKbps : p.FloorKbps;
        runRequest = request with { Quality = request.Quality with { VideoBitrateKbps = p.VideoKbps } };
        runEstimate = EstimateFor(runRequest);
        AppLog.Write($"Discord encode {m.Path} -> {outputPath}: limit {target:N0} bytes, {settings.DiscordStrategy}, " +
            $"short side {p.MaxShortSide?.ToString() ?? "original"}, fps {p.MaxFrameRate?.ToString() ?? "original"}, audio {p.AudioKbps} kbps, floor {floor} kbps.");
        return RunEncodeAsync((progress, token) => SizeTargeting.EncodeToSizeAsync(tools!, request, m, target, progress: progress, log: AppLog.Write,
            ct: token, margin: p.Settings.Margin, floorKbps: floor, started: j => job = j), r => ShowDiscordResult(r, p, m, target));
    }

    string resultSavedLabel = "Saved", resultTimeLabel = "Encoding time";
    public string ResultSavedLabel { get => resultSavedLabel; private set => Set(ref resultSavedLabel, value); }
    public string ResultTimeLabel { get => resultTimeLabel; private set => Set(ref resultTimeLabel, value); }

    void ShowDiscordResult(EncodeResult r, DiscordPlan p, MediaInfo source, long target)
    {
        LastOutputPath = r.OutputPath;
        ResultSavedLabel = "Adjustments";
        ResultTimeLabel = "Length and time";
        ResultSize = $"{Format.Size(r.OutputBytes)} of {Format.Size(target)}  ✔ fits ({r.OutputBytes:N0} bytes)";
        var changes = new List<string>();
        var (sw, sh) = source.Video!.DisplaySize;
        if (r.Output.Video is { } ov && ov.DisplaySize != (sw, sh)) changes.Add($"resolution {sw}×{sh} → {ov.DisplaySize.Width}×{ov.DisplaySize.Height}");
        if (p.MaxFrameRate is double fps) changes.Add($"{source.Video.FrameRate:0.##} → {fps:0} fps");
        if (p.Duration < source.Duration - TimeSpan.FromSeconds(0.1)) changes.Add($"trimmed to {Format.Duration(p.Duration)}");
        changes.Add(p.AudioKbps > 0 ? $"audio {p.AudioKbps} kbps" : source.Audio.Count > 0 ? "audio removed" : "no audio");
        ResultSaved = string.Join(", ", changes);
        ResultTime = $"{Format.Duration(r.Output.Duration)} video · encoded in {Format.Duration(r.EncodeTime)}" + (r.Attempts > 1 ? $" · {r.Attempts} attempts" : "");
        // The engine already reports dropped or converted streams; only the planner's HDR note adds something.
        ResultNotes = string.Join(Environment.NewLine, r.Warnings.Concat(p.Warnings.Where(w => w.Contains("HDR"))).Select(w => "• " + w));
    }
}
