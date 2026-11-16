using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Settings;
using Lantern.Core.Usage;

namespace Lantern.Core.Tests;

public sealed class UsageLedgerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
    private static readonly PhysicalAddress Phone = PhysicalAddress.Parse("E261190DBD54");

    [Fact]
    public void TakeSnapshot_AddsEachDeltaToTheCumulativeTotalExactlyOnce()
    {
        var registry = new DeviceRegistry(Start);
        registry.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start);
        registry.RecordTraffic(Phone, TrafficDirection.Download, 2_000);
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 500);

        var first = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(2)));

        Assert.Equal(1_000, first.DownloadBytesPerSecond);
        Assert.Equal(250, first.UploadBytesPerSecond);
        Assert.Equal(2_000, first.UsageDownloadBytes);
        Assert.Equal(500, first.UsageUploadBytes);
        Assert.Equal(2_500, first.UsageTotalBytes);

        // No new traffic: the rate drops to zero but the total must not grow or vanish.
        var idle = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(3)));
        Assert.Equal(0, idle.TotalBytesPerSecond);
        Assert.Equal(2_500, idle.UsageTotalBytes);

        registry.RecordTraffic(Phone, TrafficDirection.Download, 300);
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 200);
        var second = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(4)));

        Assert.Equal(300, second.DownloadBytesPerSecond);
        Assert.Equal(2_300, second.UsageDownloadBytes);
        Assert.Equal(700, second.UsageUploadBytes);
        Assert.Equal(3_000, second.UsageTotalBytes);
        Assert.Equal(new DeviceUsage(2_300, 700), registry.Usage.Get("e2:61:19:0d:bd:54"));
    }

    [Fact]
    public async Task SaveThenLoad_ReturnsTheSameTotalsPerMac()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new UsageStore(directory);
            var saved = new Dictionary<string, DeviceUsage>
            {
                ["e2:61:19:0d:bd:54"] = new(5_368_709_120, 1_073_741_824),
                ["0E-4F-69-CC-E4-F0"] = new(12_345, 678),
            };

            await store.SaveAsync(saved);
            var loaded = await store.LoadAsync();

            Assert.Equal(2, loaded.Count);
            Assert.Equal(new DeviceUsage(5_368_709_120, 1_073_741_824), loaded["E261190DBD54"]);
            Assert.Equal(new DeviceUsage(12_345, 678), loaded["0E4F69CCE4F0"]);

            using var json = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(directory, "usage.json")));
            var entry = json.RootElement.GetProperty("devices").GetProperty("E261190DBD54");
            Assert.Equal(5_368_709_120, entry.GetProperty("downloadBytes").GetInt64());
            Assert.Equal(1_073_741_824, entry.GetProperty("uploadBytes").GetInt64());
            Assert.Equal(6_442_450_944, entry.GetProperty("totalBytes").GetInt64());
            Assert.True(File.Exists(Path.Combine(directory, "usage.backup.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Restart_LoadsSavedTotalsAndAddsNewTrafficOnTop()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var firstRun = new DeviceRegistry(Start);
            var firstTracker = new UsageTracker(firstRun, new UsageStore(directory));
            firstTracker.Load();
            firstRun.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start);
            firstRun.RecordTraffic(Phone, TrafficDirection.Download, 4_000);
            firstRun.RecordTraffic(Phone, TrafficDirection.Upload, 1_000);
            firstRun.TakeSnapshot(Start.AddSeconds(1));
            Assert.True(firstTracker.SaveIfChanged());

            var secondRun = new DeviceRegistry(Start.AddHours(1));
            var secondTracker = new UsageTracker(secondRun, new UsageStore(directory));
            secondTracker.Load();
            Assert.Equal(new DeviceUsage(4_000, 1_000), secondRun.Usage.Get(Phone.ToString()));

            secondRun.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start.AddHours(1));
            secondRun.RecordTraffic(Phone, TrafficDirection.Download, 10);
            var snapshot = Assert.Single(secondRun.TakeSnapshot(Start.AddHours(1).AddSeconds(1)));

            Assert.Equal(4_010, snapshot.UsageDownloadBytes);
            Assert.Equal(1_000, snapshot.UsageUploadBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SameMacWithNewIp_ContinuesTheExistingTotal()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var registry = new DeviceRegistry(Start);
            var tracker = new UsageTracker(registry, new UsageStore(directory));
            tracker.Load();
            registry.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start);
            registry.RecordTraffic(Phone, TrafficDirection.Download, 1_000);
            registry.TakeSnapshot(Start.AddSeconds(1));

            // DHCP hands the same phone a new address during the session.
            registry.Observe(IPAddress.Parse("192.168.31.99"), Phone, Start.AddSeconds(2));
            registry.RecordTraffic(Phone, TrafficDirection.Download, 500);
            var moved = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(3)));
            Assert.Equal(IPAddress.Parse("192.168.31.99"), moved.IpAddress);
            Assert.Equal(1_500, moved.UsageDownloadBytes);
            tracker.SaveIfChanged();

            // After a restart the phone appears with yet another address.
            var restarted = new DeviceRegistry(Start.AddDays(1));
            new UsageTracker(restarted, new UsageStore(directory)).Load();
            restarted.Observe(
                IPAddress.Parse("192.168.31.150"),
                PhysicalAddress.Parse("E2-61-19-0D-BD-54"),
                Start.AddDays(1));
            restarted.RecordTraffic(Phone, TrafficDirection.Upload, 25);
            var later = Assert.Single(restarted.TakeSnapshot(Start.AddDays(1).AddSeconds(1)));

            Assert.Equal(IPAddress.Parse("192.168.31.150"), later.IpAddress);
            Assert.Equal(1_500, later.UsageDownloadBytes);
            Assert.Equal(25, later.UsageUploadBytes);
            var only = Assert.Single(restarted.Usage.Export().Devices);
            Assert.Equal(TrafficPolicy.NormalizeMac(Phone.ToString()), only.Key);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Closing_SavesTheLastDeltaThatNeverReachedASnapshot()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var registry = new DeviceRegistry(Start);
            var tracker = new UsageTracker(registry, new UsageStore(directory));
            tracker.Load();
            registry.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start);
            registry.RecordTraffic(Phone, TrafficDirection.Download, 900);
            registry.TakeSnapshot(Start.AddSeconds(1));
            tracker.SaveIfChanged();

            // Traffic after the last periodic snapshot, then the app closes.
            registry.RecordTraffic(Phone, TrafficDirection.Download, 70);
            registry.RecordTraffic(Phone, TrafficDirection.Upload, 30);
            Assert.True(tracker.FlushAndSave());

            var reloaded = new UsageStore(directory).Load();
            Assert.Equal(new DeviceUsage(970, 30), reloaded["E261190DBD54"]);

            // The flushed bytes were consumed; a later snapshot must not add them again.
            var snapshot = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(2)));
            Assert.Equal(1_000, snapshot.UsageTotalBytes);
            Assert.False(tracker.SaveIfChanged());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BeginSession_KeepsInventoryTotalsAndPendingBytes()
    {
        var registry = new DeviceRegistry(Start);
        registry.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start);
        registry.RecordTraffic(Phone, TrafficDirection.Download, 100);
        registry.TakeSnapshot(Start.AddSeconds(1));
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 40);

        registry.BeginSession();

        var remembered = Assert.Single(registry.Peek());
        Assert.Equal(140, remembered.UsageTotalBytes);
        Assert.Equal(0, remembered.TotalBytesPerSecond);
        Assert.Equal(new DeviceUsage(100, 40), registry.Usage.Get(Phone.ToString()));

        registry.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start.AddMinutes(1));
        var peeked = Assert.Single(registry.Peek());
        Assert.Equal(140, peeked.UsageTotalBytes);
    }

    [Fact]
    public void Tracker_DoesNotOverwriteSavedUsageBeforeItHasLoaded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            new UsageStore(directory).Save(
                new Dictionary<string, DeviceUsage> { [Phone.ToString()] = new(9_999, 1) });
            var registry = new DeviceRegistry(Start);
            var tracker = new UsageTracker(registry, new UsageStore(directory));
            registry.Observe(IPAddress.Parse("192.168.31.61"), Phone, Start);
            registry.RecordTraffic(Phone, TrafficDirection.Download, 5);

            Assert.False(tracker.FlushAndSave());
            Assert.Equal(new DeviceUsage(9_999, 1), new UsageStore(directory).Load()[Phone.ToString()]);

            tracker.Load();
            Assert.True(tracker.SaveIfChanged());
            Assert.Equal(new DeviceUsage(10_004, 1), new UsageStore(directory).Load()[Phone.ToString()]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_FallsBackToBackupWhenPrimaryIsCorrupt()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new UsageStore(directory);
            await store.SaveAsync(
                new Dictionary<string, DeviceUsage> { [Phone.ToString()] = new(123, 45) });
            await File.WriteAllTextAsync(Path.Combine(directory, "usage.json"), "{ not json");

            var loaded = await store.LoadAsync();

            Assert.Equal(new DeviceUsage(123, 45), loaded["E261190DBD54"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_ThrowsInsteadOfReturningZeroWhenSavedUsageIsUnreadable()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "usage.json");
            File.WriteAllText(path, "{\"devices\":{\"E261190DBD54\":{\"downloadBytes\":1}}}");
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.Throws<IOException>(() => new UsageStore(directory).Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UsageFile_IsSeparateFromSettingsAndLivesUnderUsageMeter()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsStore = new SettingsStore(directory);
            await settingsStore.SaveAsync(new AppSettings
            {
                Devices = { [Phone.ToString()] = new DevicePreferences { Alias = "Omar's phone" } },
            });
            var settingsBefore = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));

            var usageStore = new UsageStore(directory);
            await usageStore.SaveAsync(
                new Dictionary<string, DeviceUsage> { [Phone.ToString()] = new(1, 2) });

            Assert.Equal(Path.Combine(directory, "usage.json"), usageStore.UsagePath);
            Assert.Equal(
                settingsBefore,
                await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")));
            Assert.Equal("Omar's phone", (await settingsStore.LoadAsync()).Devices[Phone.ToString()].Alias);

            var expectedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UsageMeter");
            Assert.Equal(expectedRoot, SettingsStore.DefaultDirectory);
            Assert.Equal(Path.Combine(expectedRoot, "usage.json"), new UsageStore().UsagePath);
            Assert.DoesNotContain("LANternControl", new UsageStore().UsagePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Ledger_AddOverflowDoesNotWrapCountersOrAdvanceTheRevision()
    {
        var ledger = new UsageLedger();
        ledger.Add(Phone.ToString(), long.MaxValue - 2, 1);
        var revision = ledger.Revision;

        Assert.Throws<OverflowException>(() => ledger.Add(Phone.ToString(), 2, 0));

        Assert.Equal(new DeviceUsage(long.MaxValue - 2, 1), ledger.Get(Phone.ToString()));
        Assert.Equal(long.MaxValue - 1, ledger.Get(Phone.ToString()).TotalBytes);
        Assert.Equal(revision, ledger.Revision);
        Assert.Throws<OverflowException>(() => new DeviceUsage(long.MaxValue, 1).TotalBytes);
    }

    [Fact]
    public void Ledger_RestoreInvalidInputDoesNotPartiallyApplyOrClampHistory()
    {
        var ledger = new UsageLedger();
        ledger.Add(Phone.ToString(), 10, 2);
        var revision = ledger.Revision;
        var saved = new Dictionary<string, DeviceUsage>
        {
            [Phone.ToString()] = new(100, 20),
            ["0E4F69CCE4F0"] = new(-1, 0),
        };

        Assert.Throws<ArgumentException>(() => ledger.Restore(saved));

        Assert.Equal(new DeviceUsage(10, 2), ledger.Get(Phone.ToString()));
        Assert.Single(ledger.Export().Devices);
        Assert.Equal(revision, ledger.Revision);
    }

    [Fact]
    public void Ledger_RestoreRejectsDuplicateNormalizedMacsInsteadOfAddingThemTwice()
    {
        var ledger = new UsageLedger();
        var saved = new Dictionary<string, DeviceUsage>
        {
            [Phone.ToString()] = new(100, 20),
            ["e2:61:19:0d:bd:54"] = new(100, 20),
        };

        Assert.Throws<ArgumentException>(() => ledger.Restore(saved));
        Assert.Empty(ledger.Export().Devices);
    }

    [Fact]
    public void Ledger_RestoreOverflowDoesNotPartiallyApplyEarlierEntries()
    {
        var ledger = new UsageLedger();
        ledger.Add(Phone.ToString(), 10, 0);
        ledger.Add("0E4F69CCE4F0", long.MaxValue - 1, 0);
        var saved = new Dictionary<string, DeviceUsage>
        {
            [Phone.ToString()] = new(100, 20),
            ["0E4F69CCE4F0"] = new(2, 0),
        };

        Assert.Throws<OverflowException>(() => ledger.Restore(saved));

        Assert.Equal(new DeviceUsage(10, 0), ledger.Get(Phone.ToString()));
        Assert.Equal(new DeviceUsage(long.MaxValue - 1, 0), ledger.Get("0E4F69CCE4F0"));
    }

    [Fact]
    public void Tracker_LoadIsIdempotentAndNeverAddsSavedTotalsTwice()
    {
        using var temporary = new UsageTestDirectory();
        var store = new UsageStore(temporary.DirectoryPath);
        store.Save(new Dictionary<string, DeviceUsage> { [Phone.ToString()] = new(100, 20) });
        var registry = new DeviceRegistry();
        var tracker = new UsageTracker(registry, store);

        tracker.Load();
        tracker.Load();

        Assert.Equal(new DeviceUsage(100, 20), registry.Usage.Get(Phone.ToString()));
        Assert.False(tracker.SaveIfChanged());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "opencode", $"UsageMeterTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
