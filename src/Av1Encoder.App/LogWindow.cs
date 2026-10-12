using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Av1Encoder.App;

/// <summary>Read-only live view of the application log.</summary>
public sealed class LogWindow : Window
{
    static LogWindow? open;
    readonly TextBox text = new()
    {
        IsReadOnly = true, FontFamily = new("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public static void ShowSingle(Window owner)
    {
        if (open != null) { open.Activate(); return; }
        open = new LogWindow { Owner = owner };
        open.Show();
    }

    LogWindow()
    {
        Title = "Application log";
        Width = 900; Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var copy = new Button { Content = "Copy all", Margin = new(0, 0, 8, 0) };
        copy.Click += (_, _) => Clipboard.SetText(text.Text);
        var folder = new Button { Content = "Open log folder" };
        folder.Click += (_, _) => { Directory.CreateDirectory(AppLog.LogDir); Process.Start("explorer.exe", [AppLog.LogDir]); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0), Children = { copy, folder } };

        var root = new DockPanel { Margin = new(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(text);
        Content = root;

        text.Text = AppLog.Snapshot();
        text.ScrollToEnd();
        AppLog.LineWritten += OnLine;
        Closed += (_, _) => { AppLog.LineWritten -= OnLine; open = null; };
    }

    void OnLine(string line) => Dispatcher.BeginInvoke(() =>
    {
        text.AppendText(Environment.NewLine + line);
        text.ScrollToEnd();
    });
}
