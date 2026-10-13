using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Av1Encoder.Engine;

namespace Av1Encoder.App;

public enum JobState { Empty, Probing, Ready, Encoding, Paused, Completed, Failed, Cancelled }

public sealed record EncoderOption(VideoEncoder Encoder, string Label);

public sealed record InfoItem(string Label, string Value);

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    readonly AppSettings settings = AppSettings.Load();
    readonly EstimateHistory history = EstimateHistoryFile.Load();
    FfmpegTools? tools;
    MediaInfo? media;
    EncodeJob? job;
    CancellationTokenSource? cts;

    public MainViewModel()
    {
        Encoders.Add(new(VideoEncoder.SvtAv1, "Software, SVT-AV1 (best compression)"));
        selectedEncoder = Encoders[0];
        Queue.CollectionChanged += (_, _) => OnQueueChanged();
        try { tools = FfmpegTools.Locate(); }
        catch (EncodeException ex) { ErrorMessage = ex.Message; AppLog.Write(ex.Message); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(name);
    }

    void Raise(params string[] names) { foreach (var n in names) Notify(n); }

    /// <summary>
    /// The size/time estimate depends on almost every setting, so any change refreshes it; it is cheap, pure arithmetic.
    /// </summary>
    void Notify(string? name)
    {
        PropertyChanged?.Invoke(this, new(name));
        if (name is not (nameof(EstimateLine) or nameof(HighQualityEstimate) or nameof(BalancedEstimate) or nameof(SmallestEstimate)))
            foreach (var derived in new[] { nameof(EstimateLine), nameof(HighQualityEstimate), nameof(BalancedEstimate), nameof(SmallestEstimate) })
                PropertyChanged?.Invoke(this, new(derived));
        if (name is nameof(IsAnalyzing) or nameof(AnalysisStatus) or nameof(AnalysisPercent))
            foreach (var check in new[] { nameof(IsChecking), nameof(CheckStatus), nameof(CheckPercent) })
                PropertyChanged?.Invoke(this, new(check));
        // Encoder or quality changes need a new GPU measurement (a no-op for software or when it still matches).
        if (name is nameof(SelectedEncoder) or nameof(UseCustom) or nameof(IsHighQuality) or nameof(CustomCrf) or nameof(CustomSpeed) or nameof(IsDiscordMode))
        {
            SampleHardware();
            CheckBatch();
        }
        if (name is nameof(KeepAudio)) RefreshBatchEstimates();
    }

    // ---------- State ----------

    JobState state = JobState.Empty;
    public JobState State
    {
        get => state;
        private set
        {
            Set(ref state, value);
            Raise(nameof(HasMedia), nameof(ShowSingle), nameof(IsBusy), nameof(CanEdit), nameof(CanStart), nameof(ShowProgress), nameof(ShowResult),
                nameof(StartLabel), nameof(PauseLabel), nameof(StatusText), nameof(IsPaused), nameof(TaskbarState));
        }
    }

    public bool HasMedia => (media != null || IsBatch) && State != JobState.Probing;
    public bool IsBusy => State is JobState.Encoding or JobState.Paused or JobState.Probing;
    public bool IsPaused => State == JobState.Paused;
    public bool CanEdit => !IsBusy;
    public bool CanStart => tools != null && !IsBusy && (IsBatch ? !IsDiscordMode : media != null && (!IsDiscordMode || plan != null));
    public bool ShowProgress => State is JobState.Encoding or JobState.Paused;
    public bool ShowResult => State == JobState.Completed;
    public string StartLabel => State is JobState.Failed or JobState.Cancelled ? "Retry" : State == JobState.Completed ? "Encode again" : "Start";
    public string PauseLabel => State == JobState.Paused ? "Resume" : "Pause";

    public string StatusText => State switch
    {
        JobState.Empty => "Add a video to get started.",
        JobState.Probing => "Reading video…",
        JobState.Cancelled when IsBatch => "Cancelled. Videos that had finished were kept.",
        JobState.Ready => "Ready to encode.",
        JobState.Encoding => Stage,
        JobState.Paused => "Paused.",
        JobState.Completed => "Done.",
        JobState.Failed => "Encoding failed.",
        JobState.Cancelled => "Cancelled. No output file was created.",
        _ => "",
    };

    string? errorMessage;
    public string? ErrorMessage { get => errorMessage; set { Set(ref errorMessage, value); Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // ---------- Source ----------

    string fileName = "";
    public string FileName { get => fileName; private set => Set(ref fileName, value); }
    public ObservableCollection<InfoItem> SourceInfo { get; } = new();

    public async Task LoadAsync(string path)
    {
        if (IsBusy) return;
        if (tools == null) { ErrorMessage = "FFmpeg is missing, so videos cannot be opened. Please reinstall the application."; return; }
        ErrorMessage = null;
        State = JobState.Probing;
        AppLog.Write($"Opening {path}");
        try
        {
            var info = await Task.Run(() => MediaProbe.ProbeAsync(tools, path));
            if (info.Video == null) throw new EncodeException("This file does not contain a video stream.");
            Show(info);
        }
        catch (EncodeException ex)
        {
            AppLog.Write($"Could not open {path}: {ex.Message} {ex.Details}");
            ErrorMessage = ex.Message;
            State = media == null ? JobState.Empty : JobState.Ready;
        }
    }

    /// <summary>Opens the single-video view for a probed video.</summary>
    void Show(MediaInfo info)
    {
        media = info;
        FileName = Path.GetFileName(info.Path);
        FillSourceInfo(info);
        OutputFileName = DefaultOutputName(info, IsDiscordMode);
        if (settings.OutputFolder == null || !Directory.Exists(settings.OutputFolder)) outputFolder = Path.GetDirectoryName(info.Path)!;
        Raise(nameof(OutputFolder));
        UpdateContainerNote();
        State = JobState.Ready;
        OnDiscordMediaLoaded();
        ResetHardwareSample();
        AppLog.Write($"Loaded: {info.FormatName}, {info.Video!.Width}x{info.Video.Height}, {info.Duration}, {info.Audio.Count} audio, {info.Subtitles.Count} subtitle stream(s).");
    }

    /// <summary>Discord output is always MP4 (what Discord plays); Compress picks MKV only when MP4 can't hold the streams.</summary>
    static string DefaultOutputName(MediaInfo m, bool discord) => Path.GetFileNameWithoutExtension(m.Path) +
        (discord ? "_discord.mp4" : "_av1" + (FfmpegArguments.PreferredContainer(m) == OutputContainer.Mkv ? ".mkv" : ".mp4"));

    void UpdateContainerNote() => ContainerNote = !IsDiscordMode && media != null && FfmpegArguments.PreferredContainer(media) == OutputContainer.Mkv
        ? "Saving as MKV because this video has image-based subtitles or attachments that MP4 cannot hold."
        : "";

    void FillSourceInfo(MediaInfo m)
    {
        var v = m.Video!;
        var (w, h) = v.DisplaySize;
        SourceInfo.Clear();
        SourceInfo.Add(new("Format", $"{Path.GetExtension(m.Path).TrimStart('.').ToUpperInvariant()} · {m.FormatLongName}"));
        SourceInfo.Add(new("Resolution", $"{w} × {h}" + (v.Rotation != 0 ? $" (rotated {Math.Abs(v.Rotation)}°)" : "")));
        SourceInfo.Add(new("Frame rate", $"{v.FrameRate:0.###} fps" + (v.IsVariableFrameRate ? " (variable)" : "")));
        SourceInfo.Add(new("Duration", Format.Duration(m.Duration)));
        SourceInfo.Add(new("File size", Format.Size(m.SizeBytes)));
        SourceInfo.Add(new("Video", $"{v.Codec.ToUpperInvariant()} · {v.BitDepth}-bit" + (v.IsHdr ? " · HDR" : "") +
            (m.BitRate is long br ? $" · {br / 1000:N0} kbps total" : "")));
        SourceInfo.Add(new("Audio", m.Audio.Count == 0 ? "None" : string.Join("; ", m.Audio.Select(a =>
            $"{a.Codec.ToUpperInvariant()} {Format.Channels(a.Channels)} {a.SampleRate / 1000.0:0.#} kHz" + (a.BitRate is long b ? $" {b / 1000} kbps" : "") +
            (a.Language is { } l and not "und" ? $" ({l})" : "")))));
        SourceInfo.Add(new("Subtitles", m.Subtitles.Count == 0 ? "None" :
            $"{m.Subtitles.Count} track(s)" + (m.Subtitles.Any(s => !s.IsText) ? $", {m.Subtitles.Count(s => !s.IsText)} image-based" : "")));
    }

    // ---------- Output ----------

    string outputFolder = "";
    public string OutputFolder
    {
        get => settings.OutputFolder ?? outputFolder;
        set { settings.OutputFolder = string.IsNullOrWhiteSpace(value) ? null : value; outputFolder = value; settings.Save(); Raise(nameof(OutputFolder)); }
    }

    public void UseSourceFolder()
    {
        settings.OutputFolder = null;
        settings.Save();
        outputFolder = media != null ? Path.GetDirectoryName(media.Path)! : "";
        Raise(nameof(OutputFolder));
    }

    string outputFileName = "";
    public string OutputFileName { get => outputFileName; set => Set(ref outputFileName, value); }

    string containerNote = "";
    public string ContainerNote { get => containerNote; private set { Set(ref containerNote, value); Raise(nameof(HasContainerNote)); } }
    public bool HasContainerNote => ContainerNote.Length > 0;

    // ---------- Quality ----------

    public bool IsHighQuality { get => !UseCustom && settings.Preset == QualityPreset.HighQuality; set { if (value) SetPreset(QualityPreset.HighQuality); } }
    public bool IsBalanced { get => !UseCustom && settings.Preset == QualityPreset.Balanced; set { if (value) SetPreset(QualityPreset.Balanced); } }
    public bool IsSmallest { get => !UseCustom && settings.Preset == QualityPreset.SmallestFile; set { if (value) SetPreset(QualityPreset.SmallestFile); } }

    void SetPreset(QualityPreset p)
    {
        settings.Preset = p;
        settings.UseCustomQuality = false;
        settings.Save();
        Raise(nameof(IsHighQuality), nameof(IsBalanced), nameof(IsSmallest), nameof(UseCustom));
    }

    public bool UseCustom
    {
        get => settings.UseCustomQuality;
        set
        {
            if (value && !settings.UseCustomQuality)
            {
                // Start custom values from the current preset so the sliders mean something.
                var q = settings.QualityFor(settings.Preset);
                settings.CustomCrf = q.Crf; settings.CustomSpeed = q.Speed;
                Raise(nameof(CustomCrf), nameof(CustomSpeed));
            }
            settings.UseCustomQuality = value; settings.Save();
            Raise(nameof(UseCustom), nameof(IsHighQuality), nameof(IsBalanced), nameof(IsSmallest));
        }
    }

    public int CustomCrf { get => settings.CustomCrf; set { settings.CustomCrf = Math.Clamp(value, 10, 60); settings.Save(); Raise(nameof(CustomCrf)); } }
    public int CustomSpeed { get => settings.CustomSpeed; set { settings.CustomSpeed = Math.Clamp(value, 2, 12); settings.Save(); Raise(nameof(CustomSpeed)); } }

    public string CrfHint => "Lower = better quality and larger files. Presets use " +
        string.Join(" / ", Enum.GetValues<QualityPreset>().Select(p => settings.QualityFor(p).Crf)) + ".";

    public bool KeepAudio { get => settings.KeepAudio; set { settings.KeepAudio = value; settings.Save(); Raise(nameof(KeepAudio)); } }

    public ObservableCollection<EncoderOption> Encoders { get; } = new();
    EncoderOption selectedEncoder;
    public EncoderOption SelectedEncoder
    {
        get => selectedEncoder;
        set { if (value == null) return; Set(ref selectedEncoder, value); settings.Encoder = value.Encoder; settings.Save(); }
    }

    string hardwareStatus = "Checking for hardware AV1 encoders…";
    public string HardwareStatus { get => hardwareStatus; private set => Set(ref hardwareStatus, value); }

    public async Task DetectHardwareAsync()
    {
        if (tools == null) { HardwareStatus = ""; return; }
        try
        {
            var results = await Task.Run(() => HardwareDetector.DetectAsync(tools));
            foreach (var r in results) AppLog.Write($"Hardware AV1 encoder {r.Encoder}: {(r.Available ? "available" : "unavailable: " + r.Diagnostic)}");
            foreach (var r in results.Where(r => r.Available))
                Encoders.Add(new(r.Encoder, $"Hardware, {HardwareDetector.DisplayName(r.Encoder)} (uses the GPU; larger files)"));
            HardwareStatus = results.Any(r => r.Available) ? "" : "No hardware AV1 encoder was found on this PC; software encoding will be used.";
            SelectedEncoder = Encoders.FirstOrDefault(e => e.Encoder == settings.Encoder) ?? Encoders[0];
        }
        catch (Exception ex)
        {
            AppLog.Write("Hardware detection failed: " + ex.Message);
            HardwareStatus = "Hardware encoders could not be checked; software encoding will be used.";
        }
    }

    // ---------- Estimate ----------

    /// <summary>GPU measurements by encoder and quality; every preset is measured, so switching presets needs none.</summary>
    readonly Dictionary<(VideoEncoder, QualitySettings), HardwareSample> hwSamples = new();
    /// <summary>Encoders that could not encode this video; their estimates fall back to software.</summary>
    readonly Dictionary<VideoEncoder, string> hwErrors = new();
    /// <summary>The measurements the running (or about to run) GPU check covers.</summary>
    List<EncodeRequest> hwQueue = [];
    bool hwSampling, hwPending;
    double hwPercent;
    string hwStatus = "";
    CancellationTokenSource? hwCts;

    HardwareSample? SampleFor(EncodeRequest r) => hwSamples.GetValueOrDefault((r.Encoder, r.Quality));
    string? HwError(EncodeRequest r) => hwErrors.GetValueOrDefault(r.Encoder);

    /// <summary>The quality check line: the CPU check while it runs, then the GPU measurement, then the outcome.</summary>
    public bool IsChecking => IsAnalyzing || hwSampling;
    public string CheckStatus => IsAnalyzing || !hwSampling ? AnalysisStatus : hwStatus;
    public double CheckPercent => IsAnalyzing ? AnalysisPercent : hwPercent;

    /// <summary>The request Start would run now, or null when it can't be built yet.</summary>
    EncodeRequest? CurrentRequest()
    {
        if (media?.Video == null) return null;
        if (IsDiscordMode)
            return plan == null || planInput == null ? null
                : plan.Apply(new EncodeRequest { InputPath = media.Path, OutputPath = "x.mp4", Quality = QualitySettings.For(QualityPreset.Balanced) }, planInput)
                    with { Quality = new QualitySettings(32, plan.Settings.Speed, 26, VideoBitrateKbps: plan.VideoKbps) };
        return BuildRequest(OutputFileName.Length > 0 ? OutputFileName : "x.mp4", false) with { TrimStart = null, TrimEnd = null };
    }

    /// <summary>
    /// Size/time estimate for a request: GPU encodes from GPU samples, software from the quick check. When the GPU
    /// can't encode the video, the estimate is for the software encode it would fall back to. Corrected by what
    /// earlier encodes on this PC measured, unless <paramref name="corrected"/> is false (the baseline they are measured against).
    /// </summary>
    EncodeEstimate? EstimateFor(EncodeRequest request, bool corrected = true) =>
        EstimateFor(media!, request, complexity, SampleFor(request), HwError(request) != null, corrected);

    /// <summary>The same for any video, given its quality check, GPU sample and whether the GPU failed on it.</summary>
    EncodeEstimate? EstimateFor(MediaInfo m, EncodeRequest request, ComplexityEstimate? check, HardwareSample? sample, bool gpuFailed, bool corrected = true)
    {
        EncodeEstimate? Learned(EncodeEstimate? e) => e != null && corrected ? history.Apply(e, request.Encoder) : e;
        if (request.Encoder != VideoEncoder.SvtAv1)
        {
            if (sample != null) return Learned(EncodeEstimator.EstimateHardware(m, request, sample));
            if (!gpuFailed) return null;
            request = request with { Encoder = VideoEncoder.SvtAv1 };
        }
        return Learned(check == null ? null : EncodeEstimator.Estimate(m, request, check));
    }

    /// <summary>
    /// A warning when compressing will likely save little or nothing: the estimate is close to the source size, or,
    /// without an estimate, the source is already AV1. Null when it looks worthwhile. Compress mode only.
    /// </summary>
    public string? NotWorthItWarning()
    {
        if (IsDiscordMode || media == null || CurrentRequest() is not { } request) return null;
        if (EstimateFor(request) is { } e)
        {
            double expected = (e.MinBytes + e.MaxBytes) / 2.0;
            return expected < media.SizeBytes * WorthItRatio ? null
                : $"This video is already efficiently compressed: the output is estimated at {Format.SizeRange(e.MinBytes, e.MaxBytes)}, " +
                  $"about the same as or more than the source ({Format.Size(media.SizeBytes)}).";
        }
        return media.Video?.Codec == "av1" ? "This video is already AV1, so re-encoding it will likely save little and lose some quality." : null;
    }

    /// <summary>Below 90% of the source, compressing is worth it.</summary>
    const double WorthItRatio = 0.9;

    /// <summary>Records how a finished encode compared with its uncorrected estimate, so later estimates learn from it.</summary>
    void Learn(EncodeEstimate? uncorrected, EncodeResult r)
    {
        if (uncorrected == null || r.FellBackToSoftware) return; // a fallback ran a different encoder than was estimated
        history.Add(r.EncoderUsed, uncorrected, r.OutputBytes, r.EncodeTime, r.Output.Duration);
        EstimateHistoryFile.Save(history);
        var (size, time, count) = history.Correction(r.EncoderUsed);
        AppLog.Write($"Estimate learning ({r.EncoderUsed}): now size x{size:0.00}, time x{time:0.00}, from {count} encode(s).");
    }

    /// <summary>"Estimated output ≈ 48–62 MB · encoding time ≈ 3–5 min on this PC", from the quick check or GPU samples.</summary>
    public string EstimateLine
    {
        get
        {
            if (media?.Video == null || tools == null || CurrentRequest() is not { } request) return "";
            bool gpu = request.Encoder != VideoEncoder.SvtAv1;
            string? hwError = gpu ? HwError(request) : null;
            if (gpu && SampleFor(request) == null && hwError == null)
                return hwSampling ? $"Measuring the {HardwareDetector.DisplayName(request.Encoder)} encoder…" : "";
            if (EstimateFor(request) is not { } e)
                return IsAnalyzing ? "Estimating output size and encoding time…"
                    : SkipQualityAnalysis ? "Size and time estimates need the quality check, which is turned off." : "";
            string where = gpu && hwError == null ? "on this PC's GPU" : "on this PC";
            string time = e.MinTime is { } lo && e.MaxTime is { } hi ? $"encoding time ≈ {Format.TimeRange(lo, hi)} {where}" : "no time estimate";
            int learned = history.Correction(gpu && hwError != null ? VideoEncoder.SvtAv1 : request.Encoder).Count;
            if (learned >= EstimateHistory.MinOutcomes) time += $" (adjusted from your last {learned} encodes)";
            string prefix = gpu && hwError != null
                ? "The GPU encoder can't encode this video, so it would fall back to slower software encoding. "
                : "";
            if (!IsDiscordMode && (e.MinBytes + e.MaxBytes) / 2.0 >= media.SizeBytes * WorthItRatio)
                prefix += "Little or no saving expected: this video is already efficiently compressed. ";
            // Discord mode already shows its expected size next to the limit.
            return prefix + (IsDiscordMode ? $"Estimated {time}." : $"Estimated output ≈ {Format.SizeRange(e.MinBytes, e.MaxBytes)} · {time}.");
        }
    }

    /// <summary>"≈ 14–21 MB · 1–2 min" under each preset card, so the presets can be compared before encoding.</summary>
    public string HighQualityEstimate => PresetEstimate(QualityPreset.HighQuality);
    public string BalancedEstimate => PresetEstimate(QualityPreset.Balanced);
    public string SmallestEstimate => PresetEstimate(QualityPreset.SmallestFile);

    string PresetEstimate(QualityPreset preset)
    {
        if (IsBatch && !IsDiscordMode) return BatchPresetEstimate(preset);
        if (IsDiscordMode || media?.Video == null || tools == null || CurrentRequest() is not { } request) return "";
        request = request with { Quality = settings.QualityFor(preset) };
        if (EstimateFor(request) is not { } e)
            return IsAnalyzing || hwSampling && HwError(request) == null && request.Encoder != VideoEncoder.SvtAv1 ? "Estimating…" : "";
        return $"≈ {Format.SizeRange(e.MinBytes, e.MaxBytes)}" + (e.MinTime is { } lo && e.MaxTime is { } hi ? $" · {Format.TimeRange(lo, hi)}" : "");
    }

    /// <summary>
    /// Measures the selected GPU encoder: the current quality first, then each preset not measured yet, one after another
    /// (the GPU encodes one sample at a time, so running them together would only skew the speed). A change re-runs
    /// only what it leaves unmeasured: a new encoder or custom quality.
    /// </summary>
    async void SampleHardware()
    {
        if (media?.Video == null || tools == null || IsBusy || IsDiscordMode || SkipQualityAnalysis) return;
        if (CurrentRequest() is not { } request || request.Encoder == VideoEncoder.SvtAv1 || HwError(request) != null) return;
        var todo = Enum.GetValues<QualityPreset>().Select(p => request with { Quality = settings.QualityFor(p) }).Prepend(request)
            .DistinctBy(r => r.Quality).Where(r => SampleFor(r) == null).ToList();
        if (todo.Count == 0) return;
        // Already being measured for this encoder: let it finish rather than restart.
        if ((hwSampling || hwPending) && hwQueue.Any(q => q.Encoder == request.Encoder) &&
            (SampleFor(request) != null || hwQueue.Any(q => q.Quality == request.Quality))) return;
        hwCts?.Cancel();
        var cts = hwCts = new CancellationTokenSource();
        hwQueue = todo;
        hwPending = true;
        try { await Task.Delay(400, cts.Token); } // settle while the user clicks through settings
        catch (OperationCanceledException) { return; }
        hwPending = false;
        hwSampling = true;
        var m = media;
        string name = HardwareDetector.DisplayName(request.Encoder);
        void Report(int i, double p)
        {
            if (cts != hwCts) return;
            hwPercent = (i + p) * 100 / todo.Count;
            hwStatus = $"Measuring the {name} encoder at each quality ({i + 1} of {todo.Count})…";
            Raise(nameof(IsChecking), nameof(CheckStatus), nameof(CheckPercent));
        }
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var (r, n) = (todo[i], i);
                Report(n, 0);
                var progress = new Progress<double>(p => Report(n, p)); // created here: reports land on the UI thread
                var sample = await Task.Run(() => SizeTargeting.SampleHardwareAsync(tools, m, r, progress, cts.Token));
                if (cts != hwCts) return;
                hwSamples[(r.Encoder, r.Quality)] = sample;
                AppLog.Write($"GPU sample ({r.Encoder}, quality {r.Quality.HardwareQuality}): {sample.VideoKbps:0} kbps, {sample.Fps:0.#} fps.");
                Raise(nameof(EstimateLine));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) // async void: nothing may escape
        {
            AppLog.Write($"GPU sample failed: {ex.Message} {(ex as EncodeException)?.Details}");
            if (cts == hwCts) hwErrors[request.Encoder] = ex.Message;
        }
        finally
        {
            if (cts == hwCts)
            {
                hwSampling = false;
                Raise(nameof(IsChecking), nameof(CheckStatus), nameof(CheckPercent), nameof(EstimateLine));
            }
        }
    }

    /// <summary>Stops the GPU measurement; a later settings change resumes what is left.</summary>
    void StopHardwareSample()
    {
        hwCts?.Cancel();
        hwCts = null;
        hwSampling = hwPending = false;
        Raise(nameof(IsChecking), nameof(CheckStatus), nameof(CheckPercent));
    }

    /// <summary>A new video invalidates the GPU measurements.</summary>
    void ResetHardwareSample()
    {
        StopHardwareSample();
        hwSamples.Clear();
        hwErrors.Clear();
        SampleHardware();
    }

    // ---------- Progress & result ----------

    double percent;
    public double Percent { get => percent; private set { Set(ref percent, value); Raise(nameof(TaskbarProgress)); } }

    /// <summary>Taskbar button progress (0-1), across the whole batch when one is running.</summary>
    public double TaskbarProgress => batchCount > 0 ? BatchPercent / 100 : Percent / 100;
    public System.Windows.Shell.TaskbarItemProgressState TaskbarState => State switch
    {
        JobState.Encoding => System.Windows.Shell.TaskbarItemProgressState.Normal,
        JobState.Paused => System.Windows.Shell.TaskbarItemProgressState.Paused,
        JobState.Failed => System.Windows.Shell.TaskbarItemProgressState.Error,
        _ => System.Windows.Shell.TaskbarItemProgressState.None,
    };
    string stage = "";
    public string Stage { get => stage; private set { Set(ref stage, value); Raise(nameof(StatusText)); } }
    string percentText = "", elapsedText = "", remainingText = "", speedText = "", estimateText = "";
    public string PercentText { get => percentText; private set => Set(ref percentText, value); }
    public string ElapsedText { get => elapsedText; private set => Set(ref elapsedText, value); }
    public string RemainingText { get => remainingText; private set => Set(ref remainingText, value); }
    public string SpeedText { get => speedText; private set => Set(ref speedText, value); }
    public string EstimateText { get => estimateText; private set => Set(ref estimateText, value); }

    string resultSize = "", resultSaved = "", resultTime = "", resultNotes = "";
    public string ResultSize { get => resultSize; private set => Set(ref resultSize, value); }
    public string ResultSaved { get => resultSaved; private set => Set(ref resultSaved, value); }
    public string ResultTime { get => resultTime; private set => Set(ref resultTime, value); }
    public string ResultNotes { get => resultNotes; private set { Set(ref resultNotes, value); Raise(nameof(HasResultNotes)); } }
    public bool HasResultNotes => ResultNotes.Length > 0;
    public string? LastOutputPath { get; private set; }

    /// <summary>Pre-encode estimate for the running encode; shown until live numbers are reliable.</summary>
    EncodeEstimate? runEstimate;
    EncodeRequest? runRequest;

    void OnProgress(EncodeProgress p)
    {
        // After a GPU-to-software fallback the GPU estimate no longer applies.
        if (p.Encoder is { } actual && runRequest != null && runRequest.Encoder != actual && media != null)
        {
            runRequest = runRequest with { Encoder = actual };
            runEstimate = complexity == null ? null : EncodeEstimator.Estimate(media, runRequest, complexity);
        }
        Percent = p.Fraction * 100;
        PercentText = $"{p.Fraction * 100:0.0}%";
        ElapsedText = Format.Duration(p.Elapsed);
        RemainingText = p.Remaining is { } r ? "≈ " + Format.Duration(r)
            : runEstimate is { MinTime: { } lo, MaxTime: { } hi } && hi > p.Elapsed
                ? $"≈ {Format.TimeRange(lo > p.Elapsed ? lo - p.Elapsed : TimeSpan.FromSeconds(1), hi - p.Elapsed)} (estimate)"
                : "calculating…";
        SpeedText = p.Speed is double s ? $"{s:0.00}× realtime" + (p.Fps is double f ? $" ({f:0} fps)" : "")
            : p.Fps is double fps and > 0 ? $"{fps:0} fps" : "starting…";
        EstimateText = p.EstimatedOutputBytes is long e ? "≈ " + Format.Size(e)
            : runEstimate != null ? $"≈ {Format.SizeRange(runEstimate.MinBytes, runEstimate.MaxBytes)} (estimate)" : "calculating…";
        Stage = batchPrefix + p.Stage;
    }

    void ResetProgress()
    {
        Percent = 0; PercentText = "0.0%"; ElapsedText = "0:00"; RemainingText = "calculating…"; SpeedText = "–"; EstimateText = "calculating…";
        Stage = batchPrefix + "Starting…";
    }

    // ---------- Commands ----------

    /// <summary>Validates the output path. Returns null with ErrorMessage set when invalid.</summary>
    public string? ResolveOutputPath()
    {
        var name = OutputFileName.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ErrorMessage = "Enter a valid output file name (it cannot contain \\ / : * ? \" < > |).";
            return null;
        }
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (IsDiscordMode)
            name = ext is ".mp4" or ".mkv" ? Path.ChangeExtension(name, ".mp4") : name + ".mp4";
        else if (ext is not (".mp4" or ".mkv"))
            name += FfmpegArguments.PreferredContainer(media!) == OutputContainer.Mkv ? ".mkv" : ".mp4";
        if (string.IsNullOrWhiteSpace(OutputFolder)) { ErrorMessage = "Choose an output folder."; return null; }
        try { return Path.GetFullPath(Path.Combine(OutputFolder, name)); }
        catch (Exception) { ErrorMessage = "The output folder path is not valid."; return null; }
    }

    public EncodeRequest BuildRequest(string outputPath, bool overwrite, MediaInfo? source = null)
    {
        var quality = UseCustom
            ? new QualitySettings(CustomCrf, CustomSpeed, QualitySettings.HardwareQualityFromCrf(CustomCrf))
            : settings.QualityFor(settings.Preset);
        return new EncodeRequest
        {
            InputPath = (source ?? media)!.Path, OutputPath = outputPath, Quality = quality, Encoder = SelectedEncoder.Encoder,
            IncludeAudio = KeepAudio, OverwriteExisting = overwrite, SvtParams = settings.SvtParams,
        };
    }

    public long? RequiredBytes(EncodeRequest r) => EncodeJob.EstimateRequiredBytes(r, media!);

    public Task StartAsync(EncodeRequest request)
    {
        runRequest = request;
        runEstimate = EstimateFor(request);
        var uncorrected = EstimateFor(request, corrected: false);
        AppLog.Write($"Encoding {request.InputPath} -> {request.OutputPath} (CRF {request.Quality.Crf}, speed {request.Quality.Speed}, {request.Encoder}, audio {(request.IncludeAudio ? "kept" : "removed")}).");
        var source = media!;
        bool deleteOriginal = DeleteOriginals;
        return RunEncodeAsync(async (progress, token) =>
        {
            job = new EncodeJob(tools!, request, source, AppLog.Write);
            var r = await job.RunAsync(progress, token);
            if (!deleteOriginal) return r;
            var (_, note) = await DeleteOriginalAsync(source, request, r, progress, token);
            return r with { Warnings = [.. r.Warnings, note] };
        }, r => { ShowResultFor(r); Learn(uncorrected, r); });
    }

    /// <summary>Shared run loop for both modes: state, progress, cancellation and error reporting.</summary>
    async Task RunEncodeAsync(Func<IProgress<EncodeProgress>, CancellationToken, Task<EncodeResult>> work, Action<EncodeResult> showResult)
    {
        if (!CanStart) return;
        ErrorMessage = null;
        CanEncodeAnyway = false;
        StopHardwareSample(); // a GPU sample would compete with the encode
        ResetProgress();
        cts = new CancellationTokenSource();
        State = JobState.Encoding;
        var progress = new Progress<EncodeProgress>(OnProgress);
        try
        {
            var token = cts.Token;
            var result = await Task.Run(() => work(progress, token));
            showResult(result);
            State = JobState.Completed;
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("Encoding cancelled; temporary output removed.");
            State = JobState.Cancelled;
        }
        catch (TargetUnreachableException ex)
        {
            AppLog.Write($"Size target not reached: {ex.Message} {ex.Details}");
            ErrorMessage = ex.Message + " Nothing was saved. Try trimming the video, a lower resolution, or a larger size limit, or encode anyway at lower quality.";
            CanEncodeAnyway = true;
            State = JobState.Failed;
        }
        catch (EncodeException ex)
        {
            AppLog.Write($"Encoding failed: {ex.Message}{Environment.NewLine}{ex.Details}");
            ErrorMessage = ex.Message + " See the log for details.";
            State = JobState.Failed;
        }
        catch (Exception ex)
        {
            AppLog.Write("Unexpected error: " + ex);
            ErrorMessage = "An unexpected error occurred: " + ex.Message;
            State = JobState.Failed;
        }
        finally
        {
            job = null;
            cts.Dispose();
            cts = null;
        }
    }

    void ShowResultFor(EncodeResult r)
    {
        LastOutputPath = r.OutputPath;
        ResultSavedLabel = "Saved"; ResultTimeLabel = "Encoding time";
        ResultSize = $"{Format.Size(r.OutputBytes)}  (was {Format.Size(r.InputBytes)})";
        ResultSaved = r.SavedBytes >= 0
            ? $"{Format.Size(r.SavedBytes)} smaller ({r.SavedPercent:0.#}% reduction)"
            : $"{Format.Size(-r.SavedBytes)} larger ({-r.SavedPercent:0.#}% bigger; the source was already very efficient)";
        ResultTime = $"{Format.Duration(r.EncodeTime)} using {HardwareDetector.DisplayName(r.EncoderUsed)}";
        ResultNotes = string.Join(Environment.NewLine, r.Warnings.Select(w => "• " + w));
    }

    public void TogglePause()
    {
        if (job == null) return;
        if (State == JobState.Encoding) { job.Pause(); if (job.IsPaused) State = JobState.Paused; }
        else if (State == JobState.Paused) { job.Resume(); if (!job.IsPaused) State = JobState.Encoding; }
    }

    public void Cancel()
    {
        if (cts == null) return;
        Stage = "Cancelling…";
        cts.Cancel();
    }

    /// <summary>Lets the window cancel and wait for cleanup before closing.</summary>
    public async Task CancelAndWaitAsync()
    {
        Cancel();
        for (int i = 0; i < 150 && IsBusy; i++) await Task.Delay(100); // up to 15 s for temp-file and pass-log cleanup
    }
}

static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };

    public static string Duration(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>"48–62 MB", "1.2–1.6 GB", "820 KB–1.1 MB".</summary>
    public static string SizeRange(long lo, long hi)
    {
        (double div, string unit) = hi >= 1L << 30 ? (1L << 30, "GB") : hi >= 1L << 20 ? (1L << 20, "MB") : (1024.0, "KB");
        string F(long b) => b / div is var x && x >= 10 ? $"{x:0}" : $"{x:0.0}";
        return lo / div < 0.1 ? $"{Size(lo)}–{F(hi)} {unit}" : $"{F(lo)}–{F(hi)} {unit}";
    }

    /// <summary>"40–60 s", "3–5 min", "1.2–1.7 h"; a range that rounds to one value is shown once.</summary>
    public static string TimeRange(TimeSpan lo, TimeSpan hi)
    {
        if (hi.TotalSeconds < 90) return Join(Math.Max(1, Math.Floor(lo.TotalSeconds)), Math.Ceiling(hi.TotalSeconds), "s");
        if (hi.TotalMinutes < 90) return Join(Math.Max(1, Math.Floor(lo.TotalMinutes)), Math.Ceiling(hi.TotalMinutes), "min");
        return Join(Math.Floor(lo.TotalHours * 10) / 10, Math.Ceiling(hi.TotalHours * 10) / 10, "h");
        static string Join(double a, double b, string unit) => a >= b ? $"{b:0.#} {unit}" : $"{a:0.#}–{b:0.#} {unit}";
    }

    public static string Channels(int c) => c switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{c} ch" };
}
