using System.Windows;
using System.Windows.Threading;

namespace Lantern.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        SessionEnding += OnSessionEnding;
        Exit += OnExit;
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        MessageBox.Show(
            eventArgs.Exception.Message,
            "Usage Meter",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        eventArgs.Handled = true;
    }

    // Windows sign-out, restart and shutdown do not reliably raise Window.Closing,
    // so cumulative usage is flushed here as well.
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs eventArgs) =>
        SaveUsage();

    private void OnExit(object sender, ExitEventArgs eventArgs) => SaveUsage();

    private void SaveUsage()
    {
        foreach (var window in Windows.OfType<MainWindow>())
        {
            window.SaveUsageNow();
        }

        if (MainWindow is MainWindow main && !Windows.OfType<MainWindow>().Contains(main))
        {
            main.SaveUsageNow();
        }
    }
}
