using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Lantern.App;

namespace Lantern.App.Tests;

public sealed class WpfApplicationFixture : IDisposable
{
    private readonly Thread thread;
    private readonly Dispatcher dispatcher;

    public WpfApplicationFixture()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                var current = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(current));
                var app = new App(startUserSession: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                ready.TrySetResult(current);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        dispatcher = ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    public void Run(Action action) => Run(() =>
    {
        action();
        return Task.CompletedTask;
    });

    public void Run(Func<Task> action) => dispatcher
        .InvokeAsync(action)
        .Task
        .Unwrap()
        .WaitAsync(TimeSpan.FromSeconds(30))
        .GetAwaiter()
        .GetResult();

    public void Dispose()
    {
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The isolated WPF dispatcher did not stop.");
        }
    }
}
