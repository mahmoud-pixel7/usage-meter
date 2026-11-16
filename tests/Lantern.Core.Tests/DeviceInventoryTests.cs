using System.Net;
using System.Net.NetworkInformation;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Usage;

namespace Lantern.Core.Tests;

public sealed class DeviceInventoryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
    private static readonly PhysicalAddress Phone = PhysicalAddress.Parse("E261190DBD54");
    private static readonly PhysicalAddress Tablet = PhysicalAddress.Parse("0E4F69CCE4F0");
    private static readonly IPAddress Address = IPAddress.Parse("192.168.31.61");

    [Fact]
    public void RestoreInventory_UnionsMetadataAndLedgerMacsWithoutLivenessOrBackfill()
    {
        var usage = new UsageLedger();
        usage.Restore(new Dictionary<string, DeviceUsage> { [Tablet.ToString()] = new(123, 45) });
        var registry = new DeviceRegistry(Start, usage);
        registry.RestoreInventory([
            new DeviceSnapshot(Phone, Address, "Phone", Start.AddDays(-2), Start.AddMinutes(-1),
                99, 88, 7_000, 8_000),
        ]);

        var snapshots = registry.TakeSnapshot(Start.AddSeconds(1));

        Assert.Equal(2, snapshots.Count);
        Assert.All(snapshots, snapshot => Assert.True(snapshot.IsRemembered));
        Assert.All(snapshots, snapshot => Assert.Equal(0, snapshot.TotalBytesPerSecond));
        var phone = snapshots.Single(snapshot => snapshot.MacAddress.Equals(Phone));
        Assert.Equal(Address, phone.IpAddress);
        Assert.Equal("Phone", phone.HostName);
        Assert.Equal(Start.AddMinutes(-1), phone.LastSeen);
        Assert.Equal(0, phone.UsageTotalBytes);
        var tablet = snapshots.Single(snapshot => snapshot.MacAddress.Equals(Tablet));
        Assert.Equal(IPAddress.Any, tablet.IpAddress);
        Assert.Equal(DateTimeOffset.MinValue, tablet.LastSeen);
        Assert.Equal(168, tablet.UsageTotalBytes);
        Assert.Equal(new DeviceUsage(123, 45), usage.Get(Tablet.ToString()));
    }

    [Fact]
    public void RestoreInventory_DoesNotOverwriteCurrentObservationOrFreshLearnedName()
    {
        var registry = new DeviceRegistry(Start);
        registry.Observe(Address, Phone, Start, "Saved fallback");
        registry.SetHostName(Phone, "Fresh learned name");
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 150, Start.AddSeconds(1));

        registry.RestoreInventory([
            new DeviceSnapshot(Phone, IPAddress.Parse("192.168.31.200"), "Old learned name",
                Start.AddDays(-3), Start.AddDays(-1), 0, 0),
        ]);
        registry.Observe(Address, Phone, Start.AddSeconds(2), "Persisted name");
        registry.Remember(IPAddress.Parse("192.168.31.201"), Phone, Start.AddSeconds(3), "Cached name");
        var snapshot = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(3)));

        Assert.False(snapshot.IsRemembered);
        Assert.Equal(Address, snapshot.IpAddress);
        Assert.Equal("Fresh learned name", snapshot.HostName);
        Assert.Equal(Start.AddSeconds(2), snapshot.LastSeen);
        Assert.Equal(150, snapshot.UsageUploadBytes);
        Assert.Equal(50, snapshot.UploadBytesPerSecond);
    }

    [Fact]
    public void FlushPendingUsage_DoesNotConsumeTheRateWindowOrCountBytesTwice()
    {
        var registry = new DeviceRegistry(Start);
        registry.Observe(Address, Phone, Start);
        registry.RecordTraffic(Phone, TrafficDirection.Download, 2_000, Start);
        registry.RecordTraffic(Phone, TrafficDirection.Upload, 500, Start);
        registry.FlushPendingUsage();
        registry.FlushPendingUsage();
        Assert.Equal(new DeviceUsage(2_000, 500), registry.Usage.Get(Phone.ToString()));

        registry.RecordTraffic(Phone, TrafficDirection.Download, 1_000, Start.AddSeconds(1));
        registry.FlushPendingUsage();
        var snapshot = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(2)));

        Assert.Equal(1_500, snapshot.DownloadBytesPerSecond);
        Assert.Equal(250, snapshot.UploadBytesPerSecond);
        Assert.Equal(3_500, snapshot.UsageTotalBytes);
        var idle = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(3)));
        Assert.Equal(0, idle.TotalBytesPerSecond);
        Assert.Equal(3_500, idle.UsageTotalBytes);
    }

    [Fact]
    public void BeginSession_DrainsCumulativeBytesResetsRatesAndRetainsHistoricalLastSeen()
    {
        var registry = new DeviceRegistry(Start);
        registry.Observe(Address, Phone, Start, "Phone");
        registry.RecordTraffic(Phone, TrafficDirection.Download, 120, Start.AddSeconds(1));

        registry.BeginSession(Start.AddMinutes(1));
        var remembered = Assert.Single(registry.TakeSnapshot(Start.AddMinutes(1).AddSeconds(1)));

        Assert.True(remembered.IsRemembered);
        Assert.Equal(Start.AddSeconds(1), remembered.LastSeen);
        Assert.Equal(Address, remembered.IpAddress);
        Assert.Equal("Phone", remembered.HostName);
        Assert.Equal(0, remembered.TotalBytesPerSecond);
        Assert.Equal(120, remembered.UsageDownloadBytes);

        registry.Remember(Address, Phone, Start.AddMinutes(2));
        Assert.True(Assert.Single(registry.Peek()).IsRemembered);
        registry.Observe(Address, Phone, Start.AddMinutes(3));
        Assert.False(Assert.Single(registry.Peek()).IsRemembered);
    }

    [Fact]
    public void ActualZeroPayloadTrafficCanConfirmPresenceWithoutAddingUsage()
    {
        var registry = new DeviceRegistry(Start);
        registry.Remember(Address, Phone, Start);

        registry.RecordTraffic(Phone, TrafficDirection.Upload, 0, Start.AddSeconds(1));
        var snapshot = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(2)));

        Assert.False(snapshot.IsRemembered);
        Assert.Equal(Start.AddSeconds(1), snapshot.LastSeen);
        Assert.Equal(0, snapshot.UsageTotalBytes);
    }

    [Fact]
    public async Task ConcurrentRecordingFlushingAndSampling_KeepExactTotalsAndRates()
    {
        var registry = new DeviceRegistry(Start);
        registry.Observe(Address, Phone, Start);
        var recording = Task.Run(() => Parallel.For(0, 5_000, _ =>
        {
            registry.RecordTraffic(Phone, TrafficDirection.Download, 7, Start);
            registry.RecordTraffic(Phone, TrafficDirection.Upload, 3, Start);
        }));
        var flushing = Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                registry.FlushPendingUsage();
            }
        });
        await Task.WhenAll(recording, flushing);

        var snapshot = Assert.Single(registry.TakeSnapshot(Start.AddSeconds(1)));
        Assert.Equal(35_000, snapshot.DownloadBytesPerSecond);
        Assert.Equal(15_000, snapshot.UploadBytesPerSecond);
        Assert.Equal(35_000, snapshot.UsageDownloadBytes);
        Assert.Equal(15_000, snapshot.UsageUploadBytes);
        registry.FlushPendingUsage();
        Assert.Equal(50_000, registry.Usage.Get(Phone.ToString()).TotalBytes);
    }
}
