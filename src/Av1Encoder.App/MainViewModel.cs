using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Av1Encoder.Engine;

namespace Av1Encoder.App;

public enum JobState { Empty, Probing, Ready, Encoding, Paused, Completed, Failed, Cancelled }

public sealed record EncoderOption(VideoEncoder Encoder, string Label);

public sealed record InfoItem(string Label, string Value);

public sealed class MainViewModel : INotifyPropertyChanged
{
    readonly AppSettings settings = AppSettings.Load();
    FfmpegTools? tools;
    MediaInfo? media;
    EncodeJob? job;
    CancellationTokenSource? cts;

    public MainViewModel()
    {
        Encoders.Add(new(VideoEncoder.SvtAv1, "Software, SVT-AV1 (best compression)"));
        selectedEncoder = Encoders[0];
        try { tools = FfmpegTools.Locate(); }
        catch (EncodeException ex) { ErrorMessage = ex.Message; AppLog.Write(ex.Message); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }

    void Raise(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new(n)); }

    // ---------- State ----------

    JobState state = JobState.Empty;
    public JobState State
    {
        get => state;
        private set
        {
            Set(ref state, value);
            Raise(nameof(HasMedia), nameof(IsBusy), nameof(CanEdit), nameof(CanStart), nameof(ShowProgress), nameof(ShowResult),
                nameof(StartLabel), nameof(PauseLabel), nameof(StatusText), nameof(IsPaused));
        }
    }

    public bool HasMedia => media != null && State != JobState.Probing;
    public bool IsBusy => State is JobState.Encoding or JobState.Paused or JobState.Probing;
    public bool IsPaused => State == JobState.Paused;
    public bool CanEdit => !IsBusy;
    public bool CanStart => tools != null && media != null && !IsBusy;
    public bool ShowProgress => State is JobState.Encoding or JobState.Paused;
    public bool ShowResult => State == JobState.Completed;
    public string StartLabel => State is JobState.Failed or JobState.Cancelled ? "Retry" : State == JobState.Completed ? "Encode again" : "Start";
    public string PauseLabel => State == JobState.Paused ? "Resume" : "Pause";

    public string StatusText => State switch
    {
        JobState.Empty => "Add a video to get started.",
        JobState.Probing => "Reading video…",
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
            media = info;
            FileName = Path.GetFileName(path);
            FillSourceInfo(info);
            var ext = FfmpegArguments.PreferredContainer(info) == OutputContainer.Mkv ? ".mkv" : ".mp4";
            OutputFileName = Path.GetFileNameWithoutExtension(path) + "_av1" + ext;
            if (settings.OutputFolder == null || !Directory.Exists(settings.OutputFolder)) outputFolder = Path.GetDirectoryName(path)!;
            Raise(nameof(OutputFolder));
            ContainerNote = ext == ".mkv"
                ? "Saving as MKV because this video has image-based subtitles or attachments that MP4 cannot hold."
                : "";
            State = JobState.Ready;
            AppLog.Write($"Loaded: {info.FormatName}, {info.Video.Width}x{info.Video.Height}, {info.Duration}, {info.Audio.Count} audio, {info.Subtitles.Count} subtitle stream(s).");
        }
        catch (EncodeException ex)
        {
            AppLog.Write($"Could not open {path}: {ex.Message} {ex.Details}");
            ErrorMessage = ex.Message;
            State = media == null ? JobState.Empty : JobState.Ready;
        }
    }

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

    // ---------- Progress & result ----------

    double percent;
    public double Percent { get => percent; private set => Set(ref percent, value); }
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

    void OnProgress(EncodeProgress p)
    {
        Percent = p.Fraction * 100;
        PercentText = $"{p.Fraction * 100:0.0}%";
        ElapsedText = Format.Duration(p.Elapsed);
        RemainingText = p.Remaining is { } r ? "≈ " + Format.Duration(r) : "calculating…";
        SpeedText = p.Speed is double s ? $"{s:0.00}× realtime" + (p.Fps is double f ? $" ({f:0} fps)" : "") : "–";
        EstimateText = p.EstimatedOutputBytes is long e ? "≈ " + Format.Size(e) + " (estimate)" : "calculating…";
        Stage = p.Stage;
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
        if (ext is not (".mp4" or ".mkv"))
            name += FfmpegArguments.PreferredContainer(media!) == OutputContainer.Mkv ? ".mkv" : ".mp4";
        if (string.IsNullOrWhiteSpace(OutputFolder)) { ErrorMessage = "Choose an output folder."; return null; }
        try { return Path.GetFullPath(Path.Combine(OutputFolder, name)); }
        catch (Exception) { ErrorMessage = "The output folder path is not valid."; return null; }
    }

    public EncodeRequest BuildRequest(string outputPath, bool overwrite)
    {
        var quality = UseCustom
            ? new QualitySettings(CustomCrf, CustomSpeed, QualitySettings.HardwareQualityFromCrf(CustomCrf))
            : settings.QualityFor(settings.Preset);
        return new EncodeRequest
        {
            InputPath = media!.Path, OutputPath = outputPath, Quality = quality, Encoder = SelectedEncoder.Encoder,
            IncludeAudio = KeepAudio, OverwriteExisting = overwrite, SvtParams = settings.SvtParams,
        };
    }

    public long? RequiredBytes(EncodeRequest r) => EncodeJob.EstimateRequiredBytes(r, media!);

    public async Task StartAsync(EncodeRequest request)
    {
        if (!CanStart) return;
        ErrorMessage = null;
        Percent = 0; PercentText = "0.0%"; ElapsedText = "0:00"; RemainingText = "calculating…"; SpeedText = "–"; EstimateText = "calculating…";
        Stage = "Starting…";
        cts = new CancellationTokenSource();
        job = new EncodeJob(tools!, request, media!, AppLog.Write);
        State = JobState.Encoding;
        AppLog.Write($"Encoding {request.InputPath} -> {request.OutputPath} (CRF {request.Quality.Crf}, speed {request.Quality.Speed}, {request.Encoder}, audio {(request.IncludeAudio ? "kept" : "removed")}).");
        var progress = new Progress<EncodeProgress>(OnProgress);
        try
        {
            var token = cts.Token;
            var result = await Task.Run(() => job.RunAsync(progress, token));
            ShowResultFor(result);
            State = JobState.Completed;
        }
        catch (OperationCanceledException)
        {
            AppLog.Write("Encoding cancelled; temporary output removed.");
            State = JobState.Cancelled;
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
        for (int i = 0; i < 50 && IsBusy; i++) await Task.Delay(100);
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

    public static string Channels(int c) => c switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{c} ch" };
}
