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
    long UsageUploadBytes = 0)
{
    public double TotalBytesPerSecond => DownloadBytesPerSecond + UploadBytesPerSecond;

    public long UsageTotalBytes => UsageDownloadBytes + UsageUploadBytes;
}

internal sealed class DeviceRecord
{
    public required PhysicalAddress MacAddress { get; init; }

    public required IPAddress IpAddress { get; set; }

    public string? HostName { get; set; }

    public required DateTimeOffset FirstSeen { get; init; }

    public required DateTimeOffset LastSeen { get; set; }

    // Bytes since the last snapshot or flush. DeviceRegistry moves them into its
    // UsageLedger exactly once when it resets them.
    public long DownloadBytes;

    public long UploadBytes;
}
