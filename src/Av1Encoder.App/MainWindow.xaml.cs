using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Av1Encoder.App;

public partial class MainWindow : Window
{
    readonly MainViewModel vm = new();
    bool closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.State) && vm.State is JobState.Completed or JobState.Failed) Flash();
        };
    }

    /// <summary>Flashes the taskbar button until the window is brought forward, so a finished encode is noticed from another app.</summary>
    void Flash()
    {
        if (IsActive) return;
        var info = new FlashInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FlashInfo>(), Window = new System.Windows.Interop.WindowInteropHelper(this).Handle, Flags = 2 | 12 }; // tray, until foreground
        FlashWindowEx(ref info);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool FlashWindowEx(ref FlashInfo info);

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Skip(1).Where(File.Exists).ToList() is { Count: > 0 } files) await vm.AddAsync(files);
        await vm.DetectHardwareAsync();
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = vm.CanEdit && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        if (vm.CanEdit && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await vm.AddAsync(files);
    }

    async void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        if (!vm.CanEdit) return;
        var dlg = new OpenFileDialog
        {
            Title = vm.IsCompressMode ? "Choose videos" : "Choose a video",
            Multiselect = vm.IsCompressMode,
            Filter = "Videos|*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.flv;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.ogv|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) await vm.AddAsync(dlg.FileNames);
    }

    void OnRemoveVideo(object sender, RoutedEventArgs e) => vm.RemoveVideo();
    void OnClearQueue(object sender, RoutedEventArgs e) => vm.ClearQueue();
    void OnRemoveFromQueue(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is QueueItem item) vm.RemoveFromQueue(item);
    }

    void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save encoded videos" };
        if (Directory.Exists(vm.OutputFolder)) dlg.InitialDirectory = vm.OutputFolder;
        if (dlg.ShowDialog(this) == true) vm.OutputFolder = dlg.FolderName;
    }

    void OnUseSourceFolder(object sender, RoutedEventArgs e) => vm.UseSourceFolder();

    void OnStart(object sender, RoutedEventArgs e) => Start(ignoreFloor: false);
    void OnEncodeAnyway(object sender, RoutedEventArgs e) => Start(ignoreFloor: true);

    async void Start(bool ignoreFloor)
    {
        if (vm.IsBatch) { await StartBatch(); return; }
        var output = vm.ResolveOutputPath();
        if (output == null) return;

        bool overwrite = false;
        if (File.Exists(output))
        {
            if (MessageBox.Show(this, $"\"{Path.GetFileName(output)}\" already exists in this folder.\n\nReplace it?", "Replace file?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            overwrite = true;
        }

        if (vm.NotWorthItWarning() is { } warning &&
            MessageBox.Show(this, warning + "\n\nEncode anyway?", "Little saving expected", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var request = vm.IsDiscordMode ? null : vm.BuildRequest(output, overwrite);
        long needed = request != null ? vm.RequiredBytes(request) ?? 0 : vm.DiscordRequiredBytes;
        if (Engine.EncodeJob.AvailableBytes(output) is long free && free < needed)
        {
            if (MessageBox.Show(this,
                    $"The destination drive has {Format.Size(free)} free. This encode may need up to about {Format.Size(needed)} (an estimate).\n\nStart anyway?",
                    "Low disk space", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }

        if (request != null) { await vm.StartAsync(request); return; }

        if (!ignoreFloor && vm.PlanBelowFloor)
        {
            if (MessageBox.Show(this,
                    $"{vm.PlanHeadline}.\n\nAt this size limit the video will likely look very blocky. Trimming it or a lower resolution usually looks much better.\n\nEncode anyway?",
                    "Low quality expected", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            ignoreFloor = true;
        }
        await vm.StartDiscordAsync(output, overwrite, ignoreFloor);
    }

    async Task StartBatch()
    {
        if (vm.BatchOutputs() is not { } work) return;
        // Batches have no size estimate, so the codec is the hint: AV1 sources barely shrink.
        if (work.Where(w => w.Item.Media.Video?.Codec == "av1").ToList() is { Count: > 0 } av1)
        {
            var answer = MessageBox.Show(this, $"{av1.Count} of these videos are already AV1, so re-encoding them will likely save little and lose some quality.\n\nSkip them?",
                "Already AV1", MessageBoxButton.YesNoCancel, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                foreach (var w in av1) w.Item.Status = "Skipped: already AV1";
                work = work.Except(av1).ToList();
                if (work.Count == 0) return;
            }
        }
        int existing = work.Count(w => File.Exists(w.Request.OutputPath));
        bool overwrite = false;
        if (existing > 0)
        {
            var answer = MessageBox.Show(this, $"{existing} of the {work.Count} output files already exist.\n\nReplace them? Choose No to skip those videos.",
                "Replace files?", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.Cancel) return;
            overwrite = answer == MessageBoxResult.Yes;
        }
        foreach (var drive in work.GroupBy(w => Path.GetPathRoot(w.Request.OutputPath)))
        {
            long needed = drive.Sum(w => Engine.EncodeJob.EstimateRequiredBytes(w.Request, w.Item.Media));
            if (Engine.EncodeJob.AvailableBytes(drive.First().Request.OutputPath) is long free && free < needed &&
                MessageBox.Show(this, $"{drive.Key} has {Format.Size(free)} free. These encodes may need up to about {Format.Size(needed)} (an estimate).\n\nStart anyway?",
                    "Low disk space", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        await vm.StartBatchAsync(work, overwrite);
    }

    void OnCancelAnalysis(object sender, RoutedEventArgs e) => vm.CancelAnalysis();
    void OnSkipAnalysis(object sender, RoutedEventArgs e) => vm.SkipAnalysisNow();
    void OnResetOverrides(object sender, RoutedEventArgs e) => vm.ResetOverrides();

    void OnPause(object sender, RoutedEventArgs e) => vm.TogglePause();
    void OnCancel(object sender, RoutedEventArgs e) => vm.Cancel();

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (vm.LastOutputPath is { } p && File.Exists(p))
            Process.Start("explorer.exe", ["/select,", p]);
        else if (Directory.Exists(vm.OutputFolder))
            Process.Start("explorer.exe", [vm.OutputFolder]);
    }

    void OnCopyFile(object sender, RoutedEventArgs e)
    {
        if (vm.LastOutputPath is { } p && File.Exists(p)) MainViewModel.CopyToClipboard(p);
    }

    void OnShowLog(object sender, RoutedEventArgs e) => LogWindow.ShowSingle(this);

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!vm.IsBusy || closeConfirmed) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "A video is still encoding. Stop encoding and exit?\n\nThe unfinished file will be deleted.", "Exit?",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await vm.CancelAndWaitAsync();
        closeConfirmed = true;
        Close();
    }
}
