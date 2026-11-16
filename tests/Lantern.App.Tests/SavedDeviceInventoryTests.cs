using System.Net;
using Lantern.App.Services;
using Lantern.Core.Settings;
using Lantern.Core.Usage;
using Xunit;

namespace Lantern.App.Tests;

public sealed class SavedDeviceInventoryTests
{
    private const string Mac = "021122334455";

    [Fact]
    public void Build_RestoresUsageWithoutNeedingRediscoveryOrSavedAddress()
    {
        var ledger = new UsageLedger();
        ledger.Add(Mac, 1_000, 200);

        var row = Assert.Single(SavedDeviceInventory.Build(new AppSettings(), ledger));

        Assert.Equal(Mac, row.MacAddress.ToString());
        Assert.Equal(IPAddress.Any, row.IpAddress);
        Assert.True(row.IsRemembered);
        Assert.Equal(1_200, row.UsageTotalBytes);
        Assert.Equal(DateTimeOffset.MinValue, row.LastSeen);
        Assert.Equal(0, row.TotalBytesPerSecond);
    }

    [Fact]
    public void Build_KeepsManualAliasOutOfAutomaticNameEvidence()
    {
        var lastSeen = DateTimeOffset.Parse("2026-11-16T10:00:00Z");
        var settings = new AppSettings
        {
            Devices =
            {
                [Mac] = new DevicePreferences
                {
                    Alias = "My phone",
                    LearnedHostName = "Advertised phone",
                    LastKnownIp = "192.0.2.20",
                    LastSeenUtc = lastSeen,
                },
            },
        };

        var row = Assert.Single(SavedDeviceInventory.Build(settings, new UsageLedger()));

        Assert.Equal("Advertised phone", row.HostName);
        Assert.Equal(lastSeen, row.LastSeen);
        Assert.True(row.IsRemembered);
        Assert.Equal(IPAddress.Parse("192.0.2.20"), row.IpAddress);
        Assert.Equal("My phone", settings.Devices[Mac].Alias);
    }

    [Fact]
    public void Build_UsesOneRecordForNormalizedMacButDoesNotMergeDuplicateNames()
    {
        var settings = new AppSettings
        {
            Devices =
            {
                ["02:11:22:33:44:55"] = new DevicePreferences { LearnedHostName = "Android" },
                ["026677889900"] = new DevicePreferences { LearnedHostName = "Android" },
            },
        };
        var ledger = new UsageLedger();
        ledger.Add(Mac, 100, 20);

        var rows = SavedDeviceInventory.Build(settings, ledger);

        Assert.Equal(2, rows.Count);
        Assert.Equal(120, rows.Single(row => row.MacAddress.ToString() == Mac).UsageTotalBytes);
    }
}
