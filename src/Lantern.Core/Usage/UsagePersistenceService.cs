namespace Lantern.Core.Usage;

/// <summary>
/// Periodically flushes and saves usage on the ThreadPool, without a UI timer.
/// Start after the tracker has loaded; await StopAsync before closing or restarting.
/// Multiple services may safely share the same tracker.
/// </summary>
public sealed class UsagePersistenceService : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly UsageTracker tracker;
    private readonly TimeSpan interval;
    private readonly object lifecycleSync = new();
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private bool disposed;

    public UsagePersistenceService(UsageTracker tracker)
        : this(tracker, DefaultInterval)
    {
    }

    public UsagePersistenceService(UsageTracker tracker, TimeSpan interval)
    {
        this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        if (interval.TotalMilliseconds < 1 || interval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        this.interval = interval;
    }

    /// <summary>
    /// Raised on a ThreadPool thread with a user-facing failure message. UI callers
    /// must dispatch the notification themselves. Failed saves retry on the next tick.
    /// </summary>
    public event EventHandler<string>? SaveFailed;

    /// <summary>
    /// Raised on a ThreadPool thread after a successful flush, including an unchanged
    /// ledger that another caller already saved. UI callers must dispatch notifications.
    /// </summary>
    public event EventHandler? SaveSucceeded;

    /// <summary>
    /// Starts the periodic loop. Repeated calls while running are harmless.
    /// A failed or incomplete initial load must be resolved before calling Start.
    /// </summary>
    public void Start()
    {
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is { IsCompleted: false })
            {
                if (cancellation!.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Await StopAsync before restarting usage persistence.");
                }

                return;
            }

            if (!tracker.IsLoaded)
            {
                throw new InvalidOperationException("Saved usage must be loaded before persistence starts.");
            }

            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            worker = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>
    /// Stops a started loop and waits for its in-flight save and final flush. This
    /// does not cancel a disk write. Save failures are reported through SaveFailed.
    /// Calling StopAsync before Start does nothing; a stopped service can restart.
    /// </summary>
    public Task StopAsync()
    {
        lock (lifecycleSync)
        {
            cancellation?.Cancel();
            return worker ?? Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task stopped;
        lock (lifecycleSync)
        {
            disposed = true;
            cancellation?.Cancel();
            stopped = worker ?? Task.CompletedTask;
        }

        await stopped.ConfigureAwait(false);
        lock (lifecycleSync)
        {
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                TryFlushAndSave();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            TryFlushAndSave();
        }
    }

    private void TryFlushAndSave()
    {
        try
        {
            tracker.FlushAndSave();
        }
        catch (Exception exception)
        {
            var handlers = SaveFailed;
            if (handlers is null)
            {
                return;
            }

            var message = $"Could not save usage: {exception.Message}";
            foreach (EventHandler<string> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, message);
                }
                catch (Exception)
                {
                    // A failed notification must not stop future saves.
                }
            }

            return;
        }

        var succeeded = SaveSucceeded;
        if (succeeded is null)
        {
            return;
        }

        foreach (EventHandler handler in succeeded.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A failed notification must not stop future saves.
            }
        }
    }
}
