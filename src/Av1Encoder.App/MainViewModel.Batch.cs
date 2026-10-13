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

    void OnQueueChanged() => Raise(nameof(IsBatch), nameof(IsSingle), nameof(HasMedia), nameof(ShowSingle), nameof(ShowDiscordBatchNote),
        nameof(BatchSummary), nameof(CanStart), nameof(StatusText));

    /// <summary>
    /// Adds videos from a drop or the file dialog. The first video alone opens the single-video view; more in Compress
    /// mode turn it into a batch. In Discord mode a new video replaces the current one, as before.
    /// </summary>
    public async Task AddAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0) return;
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
        if (added.Count > 0 && media != null) { var current = media; ClearSingle(); Queue.Add(new(current)); }
        foreach (var a in added) Queue.Add(a);
        if (errors.Count > 0) ErrorMessage = "Some files could not be added:" + Environment.NewLine + string.Join(Environment.NewLine, errors);
        if (Queue.Count == 1) { var only = Queue[0].Media; Queue.Clear(); Show(only); }
        else State = media != null || Queue.Count > 0 ? JobState.Ready : JobState.Empty;
    }

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
        hwCts?.Cancel();
        media = null;
        complexity = null;
        hwSample = null; hwError = null; hwSampling = false;
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
        foreach (var (item, _) in work) { item.Done = false; item.Status = "Waiting"; }
        cts = new CancellationTokenSource();
        var token = cts.Token;
        State = JobState.Encoding;
        var results = new List<(QueueItem Item, EncodeResult Result)>();
        var failed = new List<string>();
        int skipped = 0;
        bool cancelled = false;
        for (int n = 0; n < work.Count && !cancelled; n++)
        {
            var (item, request) = work[n];
            if (File.Exists(request.OutputPath) && !overwrite) { item.Status = "Skipped: the output file already exists"; skipped++; continue; }
            request = request with { OverwriteExisting = overwrite };
            batchPrefix = $"Video {n + 1} of {work.Count}: ";
            (batchIndex, batchCount) = (n, work.Count);
            runRequest = null; runEstimate = null; // no per-video estimate in a batch
            ResetProgress();
            item.Status = "Encoding…";
            var progress = new Progress<EncodeProgress>(p => { OnProgress(p); item.Status = $"Encoding · {p.Fraction * 100:0}%"; });
            AppLog.Write($"Batch {n + 1}/{work.Count}: {request.InputPath} -> {request.OutputPath} (CRF {request.Quality.Crf}, speed {request.Quality.Speed}, {request.Encoder}).");
            try
            {
                var r = await Task.Run(() => (job = new EncodeJob(tools!, request, item.Media, AppLog.Write)).RunAsync(progress, token));
                results.Add((item, r));
                item.Done = true;
                item.Status = $"Done · {Format.Size(r.OutputBytes)} · " + (r.SavedBytes >= 0 ? $"{r.SavedPercent:0}% smaller" : "larger than the source")
                    + (r.FellBackToSoftware ? " · GPU failed, encoded in software" : "");
                LastOutputPath = r.OutputPath;
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
        }
        batchPrefix = "";
        (batchIndex, batchCount) = (0, 0);
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
            ShowBatchResult(results, work.Count, failed, skipped);
            State = JobState.Completed;
        }
    }

    void ShowBatchResult(List<(QueueItem Item, EncodeResult Result)> results, int total, List<string> failed, int skipped)
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
                .Concat(results.SelectMany(r => r.Result.Warnings.Select(w => $"• {r.Item.Name}: {w}"))));
    }
}
