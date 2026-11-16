using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Usage;

namespace Lantern.Core.Tests;

public sealed class UsagePersistenceServiceTests
{
    private static readonly PhysicalAddress Phone = PhysicalAddress.Parse("E261190DBD54");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-15T10:00:00Z");

    [Fact]
    public async Task ConstructionAndAnUnloadedStartDoNotTouchStorage()
    {
        using var temporary = new UsageTestDirectory();
        var tracker = new UsageTracker(new DeviceRegistry(), new UsageStore(temporary.DirectoryPath));
        await using var service = new UsagePersistenceService(tracker);

        Assert.Equal(TimeSpan.FromSeconds(30), UsagePersistenceService.DefaultInterval);
        Assert.Throws<InvalidOperationException>(service.Start);
        await service.StopAsync();
        Assert.Empty(Directory.GetFiles(temporary.DirectoryPath));
    }

    [Fact]
    public void Constructor_RejectsInvalidIntervals()
    {
        using var temporary = new UsageTestDirectory();
        var tracker = new UsageTracker(new DeviceRegistry(), new UsageStore(temporary.DirectoryPath));
        foreach (var interval in new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(-1),
            TimeSpan.FromTicks(1),
            TimeSpan.FromMilliseconds(uint.MaxValue),
        })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new UsagePersistenceService(tracker, interval));
        }

        Assert.Throws<ArgumentNullException>(() => new UsagePersistenceService(null!, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task PeriodicSave_FlushesWithoutUiSnapshotsAndDoesNotConsumeTheRateWindow()
    {
        using var temporary = new UsageTestDirectory();
        var registry = CreateRegistry();
        var store = new UsageStore(temporary.DirectoryPath);
        var tracker = new UsageTracker(registry, store);
        tracker.Load();
        await using var service = new UsagePersistenceService(tracker, TimeSpan.FromMilliseconds(20));
        var failures = new ConcurrentQueue<string>();
        service.SaveFailed += (_, message) => failures.Enqueue(message);
        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RejectingSynchronizationContext());
            service.Start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        registry.RecordTraffic(Phone, TrafficDirection.Download, 2_000);
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 500);
        await WaitUntilAsync(() => store.Load().GetValueOrDefault(Phone.ToString()) == new DeviceUsage(2_000, 500));
        await service.StopAsync();

        var snapshot = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(2)));
        Assert.Equal(1_000, snapshot.DownloadBytesPerSecond);
        Assert.Equal(250, snapshot.UploadBytesPerSecond);
        Assert.Equal(2_500, snapshot.UsageTotalBytes);
        Assert.False(tracker.SaveIfChanged());
        Assert.Empty(failures);
    }

    [Fact]
    public async Task StartIsIdempotentAndStopFlushesTheLastDeltaBeforeAllowingRestart()
    {
        using var temporary = new UsageTestDirectory();
        var registry = CreateRegistry();
        var store = new UsageStore(temporary.DirectoryPath);
        var tracker = new UsageTracker(registry, store);
        tracker.Load();
        await using var service = new UsagePersistenceService(tracker, TimeSpan.FromHours(1));
        service.Start();
        service.Start();
        registry.RecordTraffic(Phone, TrafficDirection.Download, 900);

        var stopped = service.StopAsync();
        Assert.Same(stopped, service.StopAsync());
        await stopped;
        Assert.Equal(new DeviceUsage(900, 0), store.Load()[Phone.ToString()]);

        registry.RecordTraffic(Phone, TrafficDirection.Download, 70);
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 30);
        service.Start();
        await service.StopAsync();

        Assert.Equal(new DeviceUsage(970, 30), store.Load()[Phone.ToString()]);
        Assert.False(tracker.FlushAndSave());
    }

    [Fact]
    public async Task TwoServicesSharingOneTrackerSaveEachDeltaExactlyOnce()
    {
        using var temporary = new UsageTestDirectory();
        var registry = CreateRegistry();
        var store = new UsageStore(temporary.DirectoryPath);
        var tracker = new UsageTracker(registry, store);
        tracker.Load();
        await using var first = new UsagePersistenceService(tracker, TimeSpan.FromMilliseconds(10));
        await using var second = new UsagePersistenceService(tracker, TimeSpan.FromMilliseconds(15));
        var failures = new ConcurrentQueue<string>();
        first.SaveFailed += (_, message) => failures.Enqueue(message);
        second.SaveFailed += (_, message) => failures.Enqueue(message);
        first.Start();
        second.Start();

        for (var batch = 0; batch < 20; batch++)
        {
            Parallel.For(0, 25, _ =>
            {
                registry.RecordTraffic(Phone, TrafficDirection.Download, 10);
                registry.RecordTraffic(Phone, TrafficDirection.Upload, 3);
            });
            await Task.Delay(5);
        }

        await Task.WhenAll(first.StopAsync(), second.StopAsync());

        Assert.Equal(new DeviceUsage(5_000, 1_500), store.Load()[Phone.ToString()]);
        Assert.Equal(6_500, Assert.Single(registry.TakeSnapshot(Start.AddSeconds(1))).UsageTotalBytes);
        Assert.False(tracker.SaveIfChanged());
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailureIsFollowedBySuccessWhenRetryWritesOrAnotherCallerAlreadySaved(bool saveBeforeRetry)
    {
        using var temporary = new UsageTestDirectory();
        var registry = CreateRegistry();
        var store = new UsageStore(temporary.DirectoryPath);
        store.Save(new Dictionary<string, DeviceUsage> { [Phone.ToString()] = new(100, 20) });
        var tracker = new UsageTracker(registry, store);
        tracker.Load();
        var valid = File.ReadAllText(temporary.PrimaryPath);
        File.WriteAllText(temporary.PrimaryPath, "broken primary");
        File.WriteAllText(temporary.BackupPath, "broken backup");
        registry.RecordTraffic(Phone, TrafficDirection.Download, 5);
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 3);
        await using var service = new UsagePersistenceService(tracker, TimeSpan.FromMilliseconds(20));
        using var repaired = new ManualResetEventSlim(false);
        var failure = new TaskCompletionSource<(string Message, SynchronizationContext? Context)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var success = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new ConcurrentQueue<string>();
        service.SaveSucceeded += (_, _) =>
        {
            notifications.Enqueue("succeeded");
            success.TrySetResult(SynchronizationContext.Current);
        };
        service.SaveFailed += (_, _) => throw new InvalidOperationException("A failed observer must not stop saving.");
        service.SaveFailed += (_, message) =>
        {
            notifications.Enqueue("failed");
            if (failure.TrySetResult((message, SynchronizationContext.Current)))
            {
                repaired.Wait(TimeSpan.FromSeconds(5));
            }
        };
        service.Start();
        try
        {
            var notification = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Could not save usage:", notification.Message, StringComparison.Ordinal);
            Assert.Null(notification.Context);
            Assert.Equal("broken primary", File.ReadAllText(temporary.PrimaryPath));
            Assert.Equal("broken backup", File.ReadAllText(temporary.BackupPath));
            Assert.False(success.Task.IsCompleted);
            File.WriteAllText(temporary.PrimaryPath, valid);
            File.WriteAllText(temporary.BackupPath, valid);
            if (saveBeforeRetry)
            {
                Assert.True(tracker.FlushAndSave());
                Assert.False(tracker.SaveIfChanged());
            }

            repaired.Set();
            Assert.Null(await success.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new DeviceUsage(105, 23), store.Load()[Phone.ToString()]);
            Assert.Equal(new[] { "failed", "succeeded" }, notifications.Take(2));
            await service.StopAsync();
            Assert.False(tracker.SaveIfChanged());
        }
        finally
        {
            repaired.Set();
            await service.StopAsync();
        }
    }

    [Fact]
    public async Task ThrowingSuccessSubscriberDoesNotStopLaterTicksOrReportASaveFailure()
    {
        using var temporary = new UsageTestDirectory();
        var registry = CreateRegistry();
        var store = new UsageStore(temporary.DirectoryPath);
        var tracker = new UsageTracker(registry, store);
        tracker.Load();
        await using var service = new UsagePersistenceService(tracker, TimeSpan.FromMilliseconds(20));
        var idleSuccess = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var savedSuccess = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<string>();
        service.SaveFailed += (_, message) => failures.Enqueue(message);
        service.SaveSucceeded += (_, _) => throw new InvalidOperationException("A failed observer must not stop saving.");
        service.SaveSucceeded += (_, _) =>
        {
            idleSuccess.TrySetResult(SynchronizationContext.Current);
            if (tracker.Ledger.Get(Phone.ToString()) == new DeviceUsage(42, 7))
            {
                savedSuccess.TrySetResult(SynchronizationContext.Current);
            }
        };
        service.Start();

        Assert.Null(await idleSuccess.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(Directory.GetFiles(temporary.DirectoryPath));
        registry.RecordTraffic(Phone, TrafficDirection.Download, 42);
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 7);
        Assert.Null(await savedSuccess.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(new DeviceUsage(42, 7), store.Load()[Phone.ToString()]);
        Assert.Empty(failures);
        await service.StopAsync();
        Assert.Empty(failures);
    }

    [Fact]
    public async Task IdleServiceDoesNotCreateEmptyUsageFiles()
    {
        using var temporary = new UsageTestDirectory();
        var tracker = new UsageTracker(new DeviceRegistry(), new UsageStore(temporary.DirectoryPath));
        tracker.Load();
        await using var service = new UsagePersistenceService(tracker, TimeSpan.FromMilliseconds(10));
        service.Start();
        await Task.Delay(50);

        await service.StopAsync();

        Assert.Empty(Directory.GetFiles(temporary.DirectoryPath));
    }

    [Fact]
    public async Task DisposeWaitsForTheFinalFlushAndPreventsRestart()
    {
        using var temporary = new UsageTestDirectory();
        var registry = CreateRegistry();
        var store = new UsageStore(temporary.DirectoryPath);
        var tracker = new UsageTracker(registry, store);
        tracker.Load();
        var service = new UsagePersistenceService(tracker, TimeSpan.FromHours(1));
        service.Start();
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 11);

        await service.DisposeAsync();

        Assert.Equal(new DeviceUsage(0, 11), store.Load()[Phone.ToString()]);
        Assert.Throws<ObjectDisposedException>(service.Start);
        await service.StopAsync();
        await service.DisposeAsync();
    }

    private static DeviceRegistry CreateRegistry()
    {
        var registry = new DeviceRegistry(Start);
        registry.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start);
        return registry;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), "The background usage save did not complete.");
            await Task.Delay(10);
        }
    }

    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Usage persistence must not dispatch work to the UI.");

        public override void Send(SendOrPostCallback callback, object? state) =>
            throw new InvalidOperationException("Usage persistence must not dispatch work to the UI.");
    }
}
