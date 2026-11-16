namespace Lantern.App.ViewModels;

public sealed record DashboardSummary(
    int ConnectedDevices,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    int ActiveRules,
    string? TopDeviceName,
    double TopDeviceDownloadBytesPerSecond,
    double TopDeviceUploadBytesPerSecond,
    IReadOnlyList<DeviceTrafficSnapshot> DeviceTraffic)
{
    public TrafficSample ToTrafficSample(DateTimeOffset timestamp) =>
        new(
            timestamp,
            DownloadBytesPerSecond,
            UploadBytesPerSecond,
            TopDeviceName,
            TopDeviceDownloadBytesPerSecond,
            TopDeviceUploadBytesPerSecond,
            DeviceTraffic);

    public int ConfiguredRules => ActiveRules;

    public static DashboardSummary From(IEnumerable<DeviceViewModel> devices)
    {
        var clients = devices.Where(device => !device.IsGateway).ToArray();
        var topDevice = clients
            .Where(device => device.TotalRate > 0)
            .OrderByDescending(device => device.TotalRate)
            .FirstOrDefault();

        return new DashboardSummary(
            clients.Count(device => device.IsOnline),
            clients.Sum(device => device.DownloadBytesPerSecond),
            clients.Sum(device => device.UploadBytesPerSecond),
            clients.Count(device => device.HasConfiguredRule),
            topDevice?.DisplayName,
            topDevice?.DownloadBytesPerSecond ?? 0,
            topDevice?.UploadBytesPerSecond ?? 0,
            clients
                .Select(device => new DeviceTrafficSnapshot(
                    device.DisplayName,
                    device.DownloadBytesPerSecond,
                    device.UploadBytesPerSecond))
                .OrderByDescending(device => device.TotalBytesPerSecond)
                .ToArray());
    }
}
