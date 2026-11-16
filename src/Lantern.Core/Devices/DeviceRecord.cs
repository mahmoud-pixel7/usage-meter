using System.Net;
using System.Net.NetworkInformation;

namespace Lantern.Core.Devices;

public sealed record DeviceSnapshot(
    PhysicalAddress MacAddress,
    IPAddress IpAddress,
    string? HostName,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    long UsageDownloadBytes = 0,
    long UsageUploadBytes = 0,
    bool IsRemembered = false)
{
    public double TotalBytesPerSecond => DownloadBytesPerSecond + UploadBytesPerSecond;

    public long UsageTotalBytes => UsageDownloadBytes + UsageUploadBytes;
}

internal sealed class DeviceRecord
{
    public object Sync { get; } = new();

    public required PhysicalAddress MacAddress { get; init; }

    public required IPAddress IpAddress { get; set; }

    public string? HostName { get; set; }

    public required DateTimeOffset FirstSeen { get; init; }

    public required DateTimeOffset LastSeen { get; set; }

    public bool IsRemembered { get; set; }

    public long PendingDownloadBytes;

    public long PendingUploadBytes;

    public long RateDownloadBytes;

    public long RateUploadBytes;
}
