using System.Windows;
using MobileBridge.Services;

namespace MobileBridge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Info("Unexpected error: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "MobileBridge — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Info("Background error: " + args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }
}
