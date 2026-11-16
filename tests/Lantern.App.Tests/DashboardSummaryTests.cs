using System.Net;
using System.Net.NetworkInformation;
using Lantern.App.ViewModels;
using Lantern.Core.Devices;
using Lantern.Core.Settings;
using Xunit;

namespace Lantern.App.Tests;

public sealed class DashboardSummaryTests
{
    [Fact]
    public void From_ExcludesTheGatewayFromDashboardMetrics()
    {
        var gateway = CreateDevice(
            "64644A380A15",
            "192.168.31.1",
            "Gateway",
            9_000,
            3_000,
            isProtected: true,
            new DevicePreferences { DownloadKiloBytesPerSecond = 500 });
        var phone = CreateDevice(
            "0E4F69CCE4F0",
            "192.168.31.213",
            "POCO-F6",
            2_000,
            400,
            isProtected: false,
            new DevicePreferences { UploadKiloBytesPerSecond = 100 });
        var laptop = CreateDevice(
            "345A6063C052",
            "192.168.31.61",
            "Humane",
            800,
            100,
            isProtected: false,
            null);

        var summary = DashboardSummary.From([gateway, phone, laptop]);

        Assert.Equal(2, summary.ConnectedDevices);
        Assert.Equal(2_800, summary.DownloadBytesPerSecond);
        Assert.Equal(500, summary.UploadBytesPerSecond);
        Assert.Equal(1, summary.ActiveRules);
        Assert.Equal("POCO-F6", summary.TopDeviceName);
        Assert.Equal(2_000, summary.TopDeviceDownloadBytesPerSecond);
        Assert.Equal(400, summary.TopDeviceUploadBytesPerSecond);
        Assert.Collection(
            summary.DeviceTraffic,
            device =>
            {
                Assert.Equal("POCO-F6", device.DeviceName);
                Assert.Equal(2_000, device.DownloadBytesPerSecond);
                Assert.Equal(400, device.UploadBytesPerSecond);
            },
            device =>
            {
                Assert.Equal("Humane", device.DeviceName);
                Assert.Equal(800, device.DownloadBytesPerSecond);
                Assert.Equal(100, device.UploadBytesPerSecond);
            });
    }

    [Fact]
    public void From_HandlesAnEmptyDeviceList()
    {
        var summary = DashboardSummary.From([]);

        Assert.Equal(0, summary.ConnectedDevices);
        Assert.Equal(0, summary.DownloadBytesPerSecond);
        Assert.Equal(0, summary.UploadBytesPerSecond);
        Assert.Equal(0, summary.ActiveRules);
        Assert.Null(summary.TopDeviceName);
        Assert.Equal(0, summary.TopDeviceDownloadBytesPerSecond);
        Assert.Equal(0, summary.TopDeviceUploadBytesPerSecond);
        Assert.Empty(summary.DeviceTraffic);
    }

    [Fact]
    public void ToTrafficSample_PreservesPerDeviceTrafficForChartPresentation()
    {
        var phone = CreateDevice(
            "0E4F69CCE4F0",
            "192.168.31.213",
            "POCO-F6",
            2_000,
            400,
            isProtected: false,
            null);
        var summary = DashboardSummary.From([phone]);
        var timestamp = DateTimeOffset.Parse("2026-08-03T12:00:00Z");

        var sample = summary.ToTrafficSample(timestamp);

        Assert.Equal(timestamp, sample.Timestamp);
        var traffic = Assert.Single(sample.DeviceTraffic!);
        Assert.Equal("POCO-F6", traffic.DeviceName);
        Assert.Equal(2_000, traffic.DownloadBytesPerSecond);
        Assert.Equal(400, traffic.UploadBytesPerSecond);
        Assert.DoesNotContain(
            "No active device traffic",
            Lantern.App.Controls.TrafficChartPresentation.BuildLatestSummary(sample),
            StringComparison.Ordinal);
    }

    [Fact]
    public void From_IncludesProtectedLocalComputerTrafficButOnlyCountsRecentPresence()
    {
        var gateway = CreateDevice(
            "64644A380A15", "192.168.31.1", "Gateway", 90_000, 30_000, true, null);
        var local = CreateDevice(
            "345A6063C052",
            "192.168.31.61",
            "This desktop",
            5_000,
            800,
            true,
            new DevicePreferences { DownloadKiloBytesPerSecond = 500 },
            "This computer — protected");
        var phone = CreateDevice(
            "0E4F69CCE4F0", "192.168.31.213", "Phone", 2_000, 400, false, null);
        phone.SetPresence(false);

        var summary = DashboardSummary.From([gateway, local, phone]);

        Assert.Equal(1, summary.ConnectedDevices);
        Assert.Equal(7_000, summary.DownloadBytesPerSecond);
        Assert.Equal(1_200, summary.UploadBytesPerSecond);
        Assert.Equal(0, summary.ConfiguredRules);
        Assert.Equal("This desktop", summary.TopDeviceName);
        Assert.Equal(new[] { "This desktop", "Phone" }, summary.DeviceTraffic.Select(device => device.DeviceName));
    }

    [Fact]
    public void From_ReportsConfiguredPoliciesSeparatelyFromRecentPresenceAndMonitoring()
    {
        var recent = CreateDevice(
            "345A6063C052", "192.168.31.61", "Desktop", 0, 0, false, null);
        var remembered = CreateDevice(
            "0E4F69CCE4F0",
            "192.168.31.213",
            "Phone",
            2_000,
            400,
            false,
            new DevicePreferences { PauseInternet = true });
        remembered.SetPresence(false);
        remembered.SetMonitoring(false);

        var summary = DashboardSummary.From([recent, remembered]);

        Assert.Equal(1, summary.ConnectedDevices);
        Assert.Equal(1, summary.ConfiguredRules);
        Assert.Equal(summary.ConfiguredRules, summary.ActiveRules);
        Assert.Equal(0, summary.DownloadBytesPerSecond);
        Assert.Equal(0, summary.UploadBytesPerSecond);
        Assert.False(remembered.HasActiveRule);
        Assert.True(remembered.HasConfiguredRule);
    }

    [Fact]
    public void From_StoppedMonitoringDoesNotContributeStaleRatesOrInventATopDevice()
    {
        var device = CreateDevice(
            "0E4F69CCE4F0", "192.168.31.213", "Phone", 2_000, 400, false, null);
        device.SetMonitoring(false);

        var summary = DashboardSummary.From([device]);

        Assert.Equal(0, summary.DownloadBytesPerSecond);
        Assert.Equal(0, summary.UploadBytesPerSecond);
        Assert.Null(summary.TopDeviceName);
        Assert.Equal(0, summary.TopDeviceDownloadBytesPerSecond);
        Assert.Equal(0, summary.TopDeviceUploadBytesPerSecond);
        var traffic = Assert.Single(summary.DeviceTraffic);
        Assert.Equal(0, traffic.TotalBytesPerSecond);
    }

    [Fact]
    public void From_ReclassifyingAPausedPeerAsLocalExcludesItsPolicyButKeepsItsTraffic()
    {
        var device = CreateDevice(
            "0E4F69CCE4F0",
            "192.168.31.213",
            "Desktop",
            2_000,
            400,
            false,
            new DevicePreferences { PauseInternet = true });

        device.SetProtection(true, "This computer — protected");
        var summary = DashboardSummary.From([device]);

        Assert.Equal(0, summary.ConfiguredRules);
        Assert.Equal(2_000, summary.DownloadBytesPerSecond);
        Assert.Equal("Desktop", summary.TopDeviceName);
    }

    private static DeviceViewModel CreateDevice(
        string mac,
        string ip,
        string name,
        double download,
        double upload,
        bool isProtected,
        DevicePreferences? preferences,
        string? protectedReason = null)
    {
        var now = DateTimeOffset.Parse("2026-07-31T00:00:00Z");
        var snapshot = new DeviceSnapshot(
            PhysicalAddress.Parse(mac),
            IPAddress.Parse(ip),
            name,
            now,
            now,
            download,
            upload);
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(
            snapshot,
            preferences,
            isProtected,
            protectedReason ?? (isProtected ? "Gateway — protected" : "Online"));
        return viewModel;
    }
}
