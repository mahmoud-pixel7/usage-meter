using System.IO;
using System.Security;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Lantern.App.Services;

namespace Lantern.App;

public partial class App : Application
{
    private readonly bool startUserSession;
    private SingleInstanceGuard? singleInstance;

    public App()
        : this(startUserSession: true)
    {
    }

    internal App(bool startUserSession)
    {
        this.startUserSession = startUserSession;
        if (startUserSession)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            SessionEnding += OnSessionEnding;
        }
    }

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        if (!startUserSession)
        {
            base.OnStartup(eventArgs);
            return;
        }

        try
        {
            singleInstance = SingleInstanceGuard.TryAcquireForCurrentUser();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or SecurityException or
            WaitHandleCannotBeOpenedException or InvalidOperationException)
        {
            MessageBox.Show(
                $"Usage Meter could not protect saved usage from another instance: {exception.Message}",
                "Usage Meter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (singleInstance is null)
        {
            MessageBox.Show(
                "Usage Meter is already running for this Windows user.",
                "Usage Meter",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            base.OnStartup(eventArgs);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch
        {
            Shutdown(1);
            throw;
        }
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

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        try
        {
            if (startUserSession)
            {
                SaveUsage();
            }

            base.OnExit(eventArgs);
        }
        finally
        {
            singleInstance?.Dispose();
            singleInstance = null;
        }
    }

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
