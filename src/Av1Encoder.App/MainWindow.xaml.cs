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
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) await vm.LoadAsync(args[1]);
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
            await vm.LoadAsync(files[0]);
    }

    async void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        if (!vm.CanEdit) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose a video",
            Filter = "Videos|*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.flv;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.ogv|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) await vm.LoadAsync(dlg.FileName);
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
        var output = vm.ResolveOutputPath();
        if (output == null) return;

        bool overwrite = false;
        if (File.Exists(output))
        {
            if (MessageBox.Show(this, $"\"{Path.GetFileName(output)}\" already exists in this folder.\n\nReplace it?", "Replace file?",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            overwrite = true;
        }

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
