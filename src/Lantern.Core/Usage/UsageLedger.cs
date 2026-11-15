using Lantern.Core.Control;

namespace Lantern.Core.Usage;

public sealed record DeviceUsage(long DownloadBytes, long UploadBytes)
{
    public static DeviceUsage Empty { get; } = new(0, 0);

    public long TotalBytes => DownloadBytes + UploadBytes;
}

public sealed record UsageLedgerSnapshot(
    long Revision,
    IReadOnlyDictionary<string, DeviceUsage> Devices);

/// <summary>
/// Cumulative per-device byte totals keyed by normalized MAC address. Unlike the
/// per-second counters in <see cref="Devices.DeviceRegistry"/>, these totals are
/// never reset by snapshots or new capture sessions.
/// </summary>
public sealed class UsageLedger
{
    private readonly object sync = new();
    private readonly Dictionary<string, DeviceUsage> devices =
        new(StringComparer.OrdinalIgnoreCase);
    private long revision;

    public long Revision
    {
        get
        {
            lock (sync)
            {
                return revision;
            }
        }
    }

    public DeviceUsage Add(string macAddress, long downloadBytes, long uploadBytes)
    {
        var key = TrafficPolicy.NormalizeMac(macAddress);
        downloadBytes = Math.Max(0, downloadBytes);
        uploadBytes = Math.Max(0, uploadBytes);
        lock (sync)
        {
            var current = devices.GetValueOrDefault(key, DeviceUsage.Empty);
            if (downloadBytes == 0 && uploadBytes == 0)
            {
                return current;
            }

            var updated = new DeviceUsage(
                SaturatingAdd(current.DownloadBytes, downloadBytes),
                SaturatingAdd(current.UploadBytes, uploadBytes));
            devices[key] = updated;
            revision++;
            return updated;
        }
    }

    public DeviceUsage Get(string macAddress)
    {
        var key = TrafficPolicy.NormalizeMac(macAddress);
        lock (sync)
        {
            return devices.GetValueOrDefault(key, DeviceUsage.Empty);
        }
    }

    /// <summary>
    /// Adds previously persisted totals on top of whatever has been counted so far.
    /// </summary>
    public void Restore(IReadOnlyDictionary<string, DeviceUsage> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        foreach (var pair in saved)
        {
            string key;
            try
            {
                key = TrafficPolicy.NormalizeMac(pair.Key);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                continue;
            }

            lock (sync)
            {
                var current = devices.GetValueOrDefault(key, DeviceUsage.Empty);
                devices[key] = new DeviceUsage(
                    SaturatingAdd(current.DownloadBytes, Math.Max(0, pair.Value.DownloadBytes)),
                    SaturatingAdd(current.UploadBytes, Math.Max(0, pair.Value.UploadBytes)));
            }
        }
    }

    public UsageLedgerSnapshot Export()
    {
        lock (sync)
        {
            return new UsageLedgerSnapshot(
                revision,
                new Dictionary<string, DeviceUsage>(devices, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        long.MaxValue - left < right ? long.MaxValue : left + right;
}
