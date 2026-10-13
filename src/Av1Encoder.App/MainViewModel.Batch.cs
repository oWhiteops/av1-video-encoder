using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Av1Encoder.Engine;

namespace Av1Encoder.App;

/// <summary>One video in a batch, with its status line.</summary>
public sealed class QueueItem(MediaInfo media) : INotifyPropertyChanged
{
    public MediaInfo Media { get; } = media;
    public string Name => Path.GetFileName(Media.Path);
    public string Details => $"{Media.Video!.DisplaySize.Width} × {Media.Video.DisplaySize.Height} · {Format.Duration(Media.Duration)} · {Format.Size(Media.SizeBytes)}";
    public bool Done { get; set; }

    string status = "Waiting";
    public string Status { get => status; set { status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }

    // Estimate inputs, measured once per video (GPU samples once per encoder and quality).
    public ComplexityEstimate? Check { get; set; }
    public bool CheckFailed { get; set; }
    public Dictionary<(VideoEncoder, QualitySettings), HardwareSample> GpuSamples { get; } = new();
    public HashSet<VideoEncoder> GpuFailed { get; } = new();

    /// <summary>The estimate for the current settings; null until the video has been checked.</summary>
    public EncodeEstimate? Estimate { get; set; }
    string estimateText = "";
    public string EstimateText { get => estimateText; set { estimateText = value; PropertyChanged?.Invoke(this, new(nameof(EstimateText))); } }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Batch compression: two or more videos encoded one after another with the Compress settings. A single video keeps
/// the full single-video view (details, quality check, estimate); <c>media</c> is null while a batch is shown.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<QueueItem> Queue { get; } = new();
    public bool IsBatch => Queue.Count > 1;
    public bool IsSingle => !IsBatch;
    public bool ShowSingle => HasMedia && !IsBatch;
    /// <summary>Video to Discord plans around one video, so it can't run a batch.</summary>
    public bool ShowDiscordBatchNote => IsBatch && IsDiscordMode;
    public string BatchSummary => $"{Queue.Count} videos · {Format.Size(Queue.Sum(q => q.Media.SizeBytes))} · " +
        $"{Format.Duration(TimeSpan.FromSeconds(Queue.Sum(q => q.Media.Duration.TotalSeconds)))} in total";

    string batchPrefix = "";
    int batchIndex, batchCount;

    void OnQueueChanged()
    {
        Raise(nameof(IsBatch), nameof(IsSingle), nameof(HasMedia), nameof(ShowSingle), nameof(ShowDiscordBatchNote),
            nameof(BatchSummary), nameof(CanStart), nameof(StatusText));
        CheckBatch();
    }

    /// <summary>
    /// Adds videos from a drop or the file dialog. The first video alone opens the single-video view; more in Compress
    /// mode turn it into a batch. In Discord mode a new video replaces the current one, as before.
    /// </summary>
    public async Task AddAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0) return;
        if (paths.Any(Directory.Exists))
        {
            State = JobState.Probing;
            paths = await Task.Run(() => ExpandFolders(paths));
            State = media != null || Queue.Count > 0 ? JobState.Ready : JobState.Empty;
            if (paths.Count == 0) { ErrorMessage = "No videos were found in that folder."; return; }
        }
        if (paths.Count == 1 && !IsBatch && (media == null || IsDiscordMode)) { await LoadAsync(paths[0]); return; }
        if (tools == null) { ErrorMessage = "FFmpeg is missing, so videos cannot be opened. Please reinstall the application."; return; }
        ErrorMessage = null;
        State = JobState.Probing;
        var added = new List<QueueItem>();
        var errors = new List<string>();
        foreach (var path in paths)
        {
            bool Same(MediaInfo m) => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase);
            if (media != null && Same(media) || Queue.Any(q => Same(q.Media)) || added.Any(a => Same(a.Media))) continue;
            AppLog.Write($"Adding {path}");
            try
            {
                var info = await Task.Run(() => MediaProbe.ProbeAsync(tools, path));
                if (info.Video == null) throw new EncodeException("This file does not contain a video stream.");
                added.Add(new(info));
            }
            catch (EncodeException ex)
            {
                AppLog.Write($"Could not open {path}: {ex.Message} {ex.Details}");
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        if (added.Count > 0 && media != null) { var (current, check) = (media, complexity); ClearSingle(); Queue.Add(new(current) { Check = check }); }
        foreach (var a in added) Queue.Add(a);
        if (errors.Count > 0) ErrorMessage = "Some files could not be added:" + Environment.NewLine + string.Join(Environment.NewLine, errors);
        if (Queue.Count == 1) { var only = Queue[0].Media; Queue.Clear(); Show(only); }
        else State = media != null || Queue.Count > 0 ? JobState.Ready : JobState.Empty;
        CheckBatch(); // adding marks the app busy, which pauses the estimates
    }

    public static readonly string[] VideoExtensions =
        [".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".flv", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".ogv"];

    /// <summary>Replaces each folder with the videos in it and its subfolders, sorted by file name (episode order across season folders).</summary>
    static List<string> ExpandFolders(IEnumerable<string> paths) => paths.SelectMany<string, string>(p => Directory.Exists(p)
        ? Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
        : [p]).ToList();

    /// <summary>Removes the single loaded video, back to the empty drop area.</summary>
    public void RemoveVideo()
    {
        if (IsBusy) return;
        ClearSingle();
        ErrorMessage = null;
        State = JobState.Empty;
    }

    public void RemoveFromQueue(QueueItem item)
    {
        if (IsBusy || !Queue.Remove(item)) return;
        if (Queue.Count == 1) { var only = Queue[0].Media; Queue.Clear(); Show(only); return; }
        State = Queue.Count > 0 ? JobState.Ready : JobState.Empty;
    }

    public void ClearQueue()
    {
        if (IsBusy) return;
        Queue.Clear();
        ErrorMessage = null;
        State = JobState.Empty;
    }

    /// <summary>Forgets the single video and stops its background checks (quality check, GPU sample).</summary>
    void ClearSingle()
    {
        CancelAnalysis();
        trimDebounce?.Cancel();
        media = null;
        complexity = null;
        ResetHardwareSample();
        FileName = "";
        SourceInfo.Clear();
        OutputFileName = "";
        StartFrame = EndFrame = null;
        AnalysisStatus = "";
        if (settings.OutputFolder == null) { outputFolder = ""; Raise(nameof(OutputFolder)); }
        UpdateContainerNote();
        Replan();
    }

    /// <summary>
    /// The encodes Start would run: videos not yet done (all of them after a finished batch), each named like a single
    /// encode, in the chosen folder or next to its source. Null with ErrorMessage set when the folder is invalid.
    /// </summary>
    public List<(QueueItem Item, EncodeRequest Request)>? BatchOutputs()
    {
        var pending = Queue.Any(q => !q.Done) ? Queue.Where(q => !q.Done).ToList() : Queue.ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var work = new List<(QueueItem, EncodeRequest)>();
        foreach (var item in pending)
        {
            var folder = string.IsNullOrWhiteSpace(OutputFolder) ? Path.GetDirectoryName(item.Media.Path)! : OutputFolder;
            var name = DefaultOutputName(item.Media, false);
            string path;
            try { path = Path.GetFullPath(Path.Combine(folder, name)); }
            catch (Exception) { ErrorMessage = "The output folder path is not valid."; return null; }
            // Two sources with the same name from different folders must not overwrite each other's output.
            for (int k = 2; !used.Add(path); k++)
                path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}_{k}{Path.GetExtension(name)}");
            work.Add((item, BuildRequest(path, false, item.Media)));
        }
        return work;
    }

    /// <summary>Encodes the videos one after another. A failed video is reported and the batch moves on; Cancel stops it.</summary>
    public async Task StartBatchAsync(IReadOnlyList<(QueueItem Item, EncodeRequest Request)> work, bool overwrite)
    {
        if (!CanStart) return;
        ErrorMessage = null;
        CanEncodeAnyway = false;
        StopHardwareSample();
        StopBatchCheck();
        foreach (var (item, _) in work) { item.Done = false; item.Status = "Waiting"; }
        cts = new CancellationTokenSource();
        var token = cts.Token;
        State = JobState.Encoding;
        // Whole-batch progress is weighted by each video's estimated encoding time (by its length when it has none),
        // so a long 4K video counts for more than a short 720p one.
        var weights = BatchWeights(work.Select(w => w.Item).ToList());
        batchTotalWeight = weights.Sum();
        batchDoneWeight = 0;
        batchClock = System.Diagnostics.Stopwatch.StartNew();
        batchSavedBytes = 0;
        BatchPercent = 0;
        var results = new List<(QueueItem Item, EncodeResult Result)>();
        var failed = new List<string>();
        int skipped = 0, deletedOriginals = 0;
        bool cancelled = false;
        for (int n = 0; n < work.Count && !cancelled; n++)
        {
            var (item, request) = work[n];
            if (File.Exists(request.OutputPath) && !overwrite)
            {
                item.Status = "Skipped: the output file already exists"; skipped++;
                batchTotalWeight -= weights[n];
                continue;
            }
            request = request with { OverwriteExisting = overwrite };
            batchPrefix = $"Video {n + 1} of {work.Count}: ";
            (batchIndex, batchCount, batchWeight) = (n, work.Count, weights[n]);
            runRequest = request; runEstimate = item.Estimate;
            ResetProgress();
            item.Status = "Encoding…";
            var progress = new Progress<EncodeProgress>(p => { OnProgress(p); UpdateBatchProgress(p.Fraction); item.Status = $"Encoding · {p.Fraction * 100:0}%"; });
            AppLog.Write($"Batch {n + 1}/{work.Count}: {request.InputPath} -> {request.OutputPath} (CRF {request.Quality.Crf}, speed {request.Quality.Speed}, {request.Encoder}).");
            try
            {
                var r = await Task.Run(() => (job = new EncodeJob(tools!, request, item.Media, AppLog.Write)).RunAsync(progress, token));
                results.Add((item, r));
                item.Done = true;
                batchSavedBytes += r.SavedBytes;
                var done = $"Done · {Format.Size(r.OutputBytes)} · " + (r.SavedBytes >= 0 ? $"{r.SavedPercent:0}% smaller" : "larger than the source")
                    + (r.FellBackToSoftware ? " · GPU failed, encoded in software" : "");
                item.Status = done;
                LastOutputPath = r.OutputPath;
                if (DeleteOriginals)
                {
                    var check = new Progress<EncodeProgress>(p => { OnProgress(p); item.Status = $"Checking the new file · {p.Fraction * 100:0}%"; });
                    var (deleted, note) = await Task.Run(() => DeleteOriginalAsync(item.Media, request, r, check, token));
                    item.Status = done + " · " + note;
                    if (deleted) deletedOriginals++;
                    else results[^1] = (item, r with { Warnings = [.. r.Warnings, note] });
                }
            }
            catch (OperationCanceledException) { item.Status = "Cancelled"; cancelled = true; }
            catch (Exception ex)
            {
                AppLog.Write($"Batch encode of {request.InputPath} failed: {ex.Message}{Environment.NewLine}{(ex as EncodeException)?.Details ?? ex.ToString()}");
                var message = ex is EncodeException ? ex.Message : "An unexpected error occurred: " + ex.Message;
                item.Status = "Failed: " + message;
                failed.Add($"{item.Name}: {message}");
            }
            finally { job = null; }
            batchDoneWeight += weights[n];
            UpdateBatchProgress(0);
        }
        batchPrefix = "";
        (batchIndex, batchCount) = (0, 0);
        batchClock = null;
        Raise(nameof(ShowBatchProgress));
        cts.Dispose();
        cts = null;

        if (cancelled)
        {
            AppLog.Write($"Batch cancelled after {results.Count} video(s).");
            State = JobState.Cancelled;
        }
        else if (results.Count == 0)
        {
            ErrorMessage = failed.Count > 0 ? "No video was encoded:" + Environment.NewLine + string.Join(Environment.NewLine, failed)
                : "Nothing was encoded: every output file already exists.";
            State = JobState.Failed;
        }
        else
        {
            ShowBatchResult(results, work.Count, failed, skipped, deletedOriginals);
            State = JobState.Completed;
        }
    }

    void ShowBatchResult(List<(QueueItem Item, EncodeResult Result)> results, int total, List<string> failed, int skipped, int deletedOriginals)
    {
        long input = results.Sum(r => r.Result.InputBytes), output = results.Sum(r => r.Result.OutputBytes);
        ResultSavedLabel = "Saved"; ResultTimeLabel = "Encoding time";
        ResultSize = $"{Format.Size(output)}  (was {Format.Size(input)}) · {results.Count} of {total} videos";
        ResultSaved = input >= output
            ? $"{Format.Size(input - output)} smaller ({(input - output) * 100.0 / Math.Max(input, 1):0.#}% reduction)"
            : $"{Format.Size(output - input)} larger";
        ResultTime = Format.Duration(TimeSpan.FromTicks(results.Sum(r => r.Result.EncodeTime.Ticks)));
        ResultNotes = string.Join(Environment.NewLine,
            failed.Select(f => "• Failed: " + f)
                .Concat(skipped > 0 ? [$"• {skipped} video(s) skipped because the output file already exists."] : [])
                .Concat(deletedOriginals > 0 ? [$"• {deletedOriginals} original(s) passed the check and were moved to the Recycle Bin."] : [])
                .Concat(results.SelectMany(r => r.Result.Warnings.Select(w => $"• {r.Item.Name}: {w}"))));
    }

    /// <summary>Delete originals after a verified encode (Compress mode). Deliberately not saved: off at every start.</summary>
    public bool DeleteOriginals { get; set { field = value; Raise(nameof(DeleteOriginals)); } }

    /// <summary>
    /// Runs <see cref="OriginalCheck"/> on a finished encode and, when it passes, sends the source to the Recycle Bin
    /// (deleted outright on drives without one, such as network shares). Cancelling the check keeps the original.
    /// </summary>
    async Task<(bool Deleted, string Note)> DeleteOriginalAsync(MediaInfo source, EncodeRequest request, EncodeResult r,
        IProgress<EncodeProgress> progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? reason;
        try
        {
            reason = await OriginalCheck.VerifyAsync(tools!, source, request, r,
                f => progress.Report(new(f, sw.Elapsed, null, null, null, null, "Checking the new file before deleting the original…")), AppLog.Write, ct);
        }
        catch (OperationCanceledException) { reason = "the check was cancelled"; }
        catch (EncodeException ex) { reason = ex.Message; }
        if (reason != null)
        {
            AppLog.Write($"Kept {source.Path}: {reason}.");
            return (false, $"Original kept: {reason}.");
        }
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(source.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            AppLog.Write($"Verified {r.OutputPath}; moved {source.Path} to the Recycle Bin.");
            return (true, "Original moved to the Recycle Bin.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not delete {source.Path}: {ex}");
            return (false, $"Original kept: it could not be deleted ({ex.Message}).");
        }
    }

    // ---------- Batch estimates ----------

    CancellationTokenSource? batchCheckCts;
    (VideoEncoder, QualitySettings?)? batchCheckKey;
    bool batchChecking;
    string batchCheckStatus = "";
    double batchCheckPercent;
    public bool IsBatchChecking => batchChecking;
    public string BatchCheckStatus => batchCheckStatus;
    public double BatchCheckPercent => batchCheckPercent;

    /// <summary>"Estimated after compression: 4.1–5.2 GB (now 12.4 GB) · encoding ≈ 2–3 h on this PC".</summary>
    public string BatchEstimate { get; private set; } = "";
    /// <summary>"Space saved if the originals are deleted: ≈ 7.2–8.3 GB (63%)", with how much of it is extrapolated.</summary>
    public string BatchSaving { get; private set; } = "";
    /// <summary>Videos expected to shrink by less than 10%, which are hardly worth encoding.</summary>
    public string LowSavingNote { get; private set; } = "";
    public bool HasLowSaving => LowSavingNote.Length > 0;

    EncodeRequest BatchRequest(QueueItem q) => BuildRequest("x.mp4", false, q.Media);

    bool NeedsCheck(QueueItem q, VideoEncoder encoder, QualitySettings quality) => !q.CheckFailed &&
        (q.Check == null || encoder != VideoEncoder.SvtAv1 && !q.GpuFailed.Contains(encoder) && !q.GpuSamples.ContainsKey((encoder, quality)));

    /// <summary>
    /// Runs the quality check (and, for a GPU encoder, a GPU sample) on each batch video in turn, so each gets its own
    /// size and time estimate. Videos already checked are skipped, so a setting change only measures what it must:
    /// software estimates follow presets instantly, a GPU needs one sample per video at the new quality.
    /// </summary>
    async void CheckBatch()
    {
        if (!IsBatch || tools == null || IsBusy || IsDiscordMode || SkipQualityAnalysis) { StopBatchCheck(); RefreshBatchEstimates(); return; }
        RefreshBatchEstimates();
        var probe = BatchRequest(Queue[0]);
        // The software check covers every quality; a GPU needs a sample per quality.
        var key = (probe.Encoder, probe.Encoder == VideoEncoder.SvtAv1 ? null : probe.Quality);
        if (batchCheckCts != null && batchCheckKey == key) return; // already running for these settings; it picks up added videos
        if (!Queue.Any(q => NeedsCheck(q, probe.Encoder, probe.Quality))) return;
        StopBatchCheck();
        var cts = batchCheckCts = new CancellationTokenSource();
        batchCheckKey = key;
        try { await Task.Delay(400, cts.Token); } // settle while the user clicks through settings
        catch (OperationCanceledException) { return; }
        batchChecking = true;
        int done = 0;
        try
        {
            while (cts == batchCheckCts && Queue.FirstOrDefault(q => NeedsCheck(q, probe.Encoder, probe.Quality)) is { } item)
            {
                int total = done + Queue.Count(q => NeedsCheck(q, probe.Encoder, probe.Quality));
                void Report(double p)
                {
                    if (cts != batchCheckCts) return;
                    batchCheckPercent = (done + p) * 100 / total;
                    batchCheckStatus = $"Estimating video {done + 1} of {total}: {item.Name}";
                    Raise(nameof(IsBatchChecking), nameof(BatchCheckStatus), nameof(BatchCheckPercent));
                }
                Report(0);
                var progress = new Progress<double>(p => Report(key.Encoder == VideoEncoder.SvtAv1 || item.Check != null ? p : p / 2));
                var m = item.Media;
                try
                {
                    if (item.Check == null)
                    {
                        item.Check = await Task.Run(() => SizeTargeting.AnalyzeAsync(tools, m, null, null, progress, cts.Token));
                        AppLog.Write($"Batch quality check of {m.Path}: CRF-32 needs {item.Check.Crf32Kbps:0} kbps.");
                    }
                    if (key.Encoder != VideoEncoder.SvtAv1 && !item.GpuFailed.Contains(key.Encoder) && cts == batchCheckCts)
                    {
                        var request = BatchRequest(item);
                        try { item.GpuSamples[(key.Encoder, request.Quality)] = await Task.Run(() => SizeTargeting.SampleHardwareAsync(tools, m, request, null, cts.Token)); }
                        catch (EncodeException ex)
                        {
                            AppLog.Write($"Batch GPU sample of {m.Path} failed: {ex.Message} {ex.Details}");
                            item.GpuFailed.Add(key.Encoder);
                        }
                    }
                }
                catch (EncodeException ex)
                {
                    AppLog.Write($"Batch quality check of {m.Path} failed: {ex.Message} {ex.Details}");
                    item.CheckFailed = true;
                }
                done++;
                RefreshBatchEstimates();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("Batch estimates failed: " + ex); } // async void: nothing may escape
        finally
        {
            if (cts == batchCheckCts) { batchCheckCts = null; batchCheckKey = null; batchChecking = false; RefreshBatchEstimates(); }
        }
    }

    void StopBatchCheck()
    {
        batchCheckCts?.Cancel();
        batchCheckCts = null;
        batchCheckKey = null;
        batchChecking = false;
        Raise(nameof(IsBatchChecking));
    }

    public void CancelBatchCheck() { StopBatchCheck(); RefreshBatchEstimates(); }

    /// <summary>Recomputes each video's estimate and the batch totals for the current settings (pure arithmetic).</summary>
    void RefreshBatchEstimates()
    {
        if (!IsBatch) return;
        foreach (var q in Queue)
        {
            var r = BatchRequest(q);
            q.Estimate = EstimateFor(q.Media, r, q.Check, q.GpuSamples.GetValueOrDefault((r.Encoder, r.Quality)), q.GpuFailed.Contains(r.Encoder));
            q.EstimateText = q.Estimate is { } e ? ItemEstimateText(q.Media, e)
                : q.CheckFailed ? "No estimate: the quality check failed on this video." : "";
        }

        long totalBytes = Queue.Sum(q => q.Media.SizeBytes);
        var known = Queue.Where(q => q.Estimate != null).ToList();
        if (BatchTotals(q => q.Estimate) is not var (min, max, minTime, maxTime, _))
        {
            BatchEstimate = batchChecking ? "Estimating the size and time of each video…"
                : SkipQualityAnalysis ? "Size and time estimates need the quality check, which is turned off." : "";
            BatchSaving = LowSavingNote = "";
        }
        else
        {
            string time = minTime is { } lo && maxTime is { } hi ? $" · encoding ≈ {Format.TimeRange(lo, hi)} on this PC" : "";
            BatchEstimate = $"Estimated after compression: {Format.SizeRange(min, max)} (now {Format.Size(totalBytes)}){time}";
            double percent = 100.0 * (totalBytes - (min + max) / 2.0) / Math.Max(totalBytes, 1);
            BatchSaving = (totalBytes - max > 0
                    ? $"Space saved if the originals are deleted: ≈ {Format.SizeRange(Math.Max(totalBytes - max, 0), totalBytes - min)} ({percent:0}%)"
                    : "Little or no space would be saved: these videos are already efficiently compressed.")
                + (known.Count < Queue.Count ? $" · extrapolated from {known.Count} of {Queue.Count} videos checked so far" : "");
            int low = known.Count(q => IsLowSaving(q.Media, q.Estimate!));
            LowSavingNote = low == 0 ? "" : $"{low} video(s) would shrink by less than {(1 - WorthItRatio) * 100:0}%, so encoding them gains little.";
        }
        Raise(nameof(IsBatchChecking), nameof(BatchCheckStatus), nameof(BatchCheckPercent), nameof(BatchEstimate), nameof(BatchSaving),
            nameof(LowSavingNote), nameof(HasLowSaving));
    }

    /// <summary>
    /// Batch totals from per-video estimates. Videos without one yet are assumed to compress like the estimated ones
    /// (by size) and encode at the same pace (by length). Null when no video has an estimate.
    /// </summary>
    (long Min, long Max, TimeSpan? MinTime, TimeSpan? MaxTime, int Known)? BatchTotals(Func<QueueItem, EncodeEstimate?> estimate)
    {
        var known = Queue.Select(q => (q.Media, Estimate: estimate(q))).Where(k => k.Estimate != null).ToList();
        if (known.Count == 0) return null;
        double sizeScale = (double)Queue.Sum(q => q.Media.SizeBytes) / Math.Max(known.Sum(k => k.Media.SizeBytes), 1);
        double timeScale = Queue.Sum(q => q.Media.Duration.TotalSeconds) / Math.Max(known.Sum(k => k.Media.Duration.TotalSeconds), 1);
        bool timed = known.All(k => k.Estimate!.MinTime != null && k.Estimate.MaxTime != null);
        TimeSpan Sum(Func<EncodeEstimate, TimeSpan> f) => TimeSpan.FromTicks(known.Sum(k => f(k.Estimate!).Ticks)) * timeScale;
        return ((long)(known.Sum(k => k.Estimate!.MinBytes) * sizeScale), (long)(known.Sum(k => k.Estimate!.MaxBytes) * sizeScale),
            timed ? Sum(e => e.MinTime!.Value) : null, timed ? Sum(e => e.MaxTime!.Value) : null, known.Count);
    }

    /// <summary>A preset card's batch total, so presets can be compared for the whole batch.</summary>
    string BatchPresetEstimate(QualityPreset preset)
    {
        var quality = settings.QualityFor(preset);
        var totals = BatchTotals(q =>
        {
            var r = BatchRequest(q) with { Quality = quality };
            return EstimateFor(q.Media, r, q.Check, q.GpuSamples.GetValueOrDefault((r.Encoder, r.Quality)), q.GpuFailed.Contains(r.Encoder));
        });
        if (totals is not var (min, max, lo, hi, _)) return batchChecking ? "Estimating…" : "";
        return $"≈ {Format.SizeRange(min, max)} in total" + (lo is { } a && hi is { } b ? $" · {Format.TimeRange(a, b)}" : "");
    }

    bool IsLowSaving(MediaInfo m, EncodeEstimate e) => (e.MinBytes + e.MaxBytes) / 2.0 >= m.SizeBytes * WorthItRatio;

    string ItemEstimateText(MediaInfo m, EncodeEstimate e)
    {
        double percent = 100.0 * (m.SizeBytes - (e.MinBytes + e.MaxBytes) / 2.0) / Math.Max(m.SizeBytes, 1);
        return $"Estimated ≈ {Format.SizeRange(e.MinBytes, e.MaxBytes)} " + (percent >= 0 ? $"({percent:0}% smaller)" : "(larger)")
            + (e.MinTime is { } lo && e.MaxTime is { } hi ? $" · {Format.TimeRange(lo, hi)}" : "")
            + (IsLowSaving(m, e) ? " · little saving" : "");
    }

    /// <summary>Removes the videos expected to shrink by less than 10%.</summary>
    public void RemoveLowSaving()
    {
        if (IsBusy) return;
        foreach (var q in Queue.Where(q => q.Estimate is { } e && IsLowSaving(q.Media, e)).ToList()) Queue.Remove(q);
        if (Queue.Count == 1) { var only = Queue[0].Media; Queue.Clear(); Show(only); }
        else if (Queue.Count == 0) State = JobState.Empty;
    }

    // ---------- Whole-batch progress ----------

    double batchTotalWeight, batchDoneWeight, batchWeight;
    long batchSavedBytes;
    System.Diagnostics.Stopwatch? batchClock;
    double batchPercent;
    public double BatchPercent { get => batchPercent; private set => Set(ref batchPercent, value); }
    string batchProgressText = "";
    public string BatchProgressText { get => batchProgressText; private set => Set(ref batchProgressText, value); }
    public bool ShowBatchProgress => batchCount > 0;

    /// <summary>Estimated encoding seconds per video (mid-range), or its length scaled by the checked videos' pace.</summary>
    double[] BatchWeights(List<QueueItem> items)
    {
        var timed = items.Where(q => q.Estimate?.MinTime != null).ToList();
        double pace = timed.Count > 0
            ? timed.Sum(q => (q.Estimate!.MinTime!.Value + q.Estimate.MaxTime!.Value).TotalSeconds / 2) / Math.Max(timed.Sum(q => q.Media.Duration.TotalSeconds), 1)
            : 1;
        return items.Select(q => q.Estimate is { MinTime: { } lo, MaxTime: { } hi } ? (lo + hi).TotalSeconds / 2
            : Math.Max(q.Media.Duration.TotalSeconds, 1) * pace).ToArray();
    }

    /// <summary>Whole-batch percent and time left. Time left starts from the estimates and is corrected by the pace so far.</summary>
    void UpdateBatchProgress(double fraction)
    {
        if (batchClock == null || batchTotalWeight <= 0) return;
        double done = batchDoneWeight + fraction * batchWeight, p = Math.Clamp(done / batchTotalWeight, 0, 1);
        BatchPercent = p * 100;
        var elapsed = batchClock.Elapsed;
        // Weights are estimated seconds; after a little progress the real pace (elapsed per weight) replaces the estimate.
        double pace = done > batchTotalWeight * 0.03 ? elapsed.TotalSeconds / done : 1;
        var left = TimeSpan.FromSeconds((batchTotalWeight - done) * pace);
        BatchProgressText = $"All videos: {p * 100:0}% · video {batchIndex + 1} of {batchCount} · {Format.Duration(elapsed)} elapsed · ≈ {Format.Duration(left)} left"
            + (batchSavedBytes > 0 ? $" · {Format.Size(batchSavedBytes)} saved so far" : "");
        Raise(nameof(ShowBatchProgress), nameof(TaskbarProgress));
    }
}
