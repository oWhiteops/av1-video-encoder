using System.Windows;
using System.Windows.Threading;

namespace Av1Encoder.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Write($"AV1 Video Encoder {typeof(App).Assembly.GetName().Version} starting.");
        base.OnStartup(e);
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("Unhandled error: " + e.Exception);
        MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nDetails were written to the application log.",
            "AV1 Video Encoder", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
