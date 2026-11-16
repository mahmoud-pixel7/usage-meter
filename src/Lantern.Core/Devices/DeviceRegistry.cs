using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Lantern.Core.Control;
using Lantern.Core.Usage;

namespace Lantern.Core.Devices;

public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceRecord> devices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object snapshotSync = new();
    private DateTimeOffset lastSnapshot;

    public DeviceRegistry(DateTimeOffset? startedAt = null, UsageLedger? usage = null)
    {
        lastSnapshot = startedAt ?? DateTimeOffset.UtcNow;
        Usage = usage ?? new UsageLedger();
    }

    /// <summary>
    /// Cumulative per-MAC totals. Survives snapshots and <see cref="BeginSession"/>.
    /// </summary>
    public UsageLedger Usage { get; }

    public void BeginSession(DateTimeOffset? startedAt = null)
    {
        lock (snapshotSync)
        {
            foreach (var (key, record) in devices)
            {
                lock (record.Sync)
                {
                    DrainPendingBytes(key, record);
                    record.RateDownloadBytes = 0;
                    record.RateUploadBytes = 0;
                    record.IsRemembered = true;
                }
            }

            lastSnapshot = startedAt ?? DateTimeOffset.UtcNow;
        }
    }

    public void RestoreInventory(IEnumerable<DeviceSnapshot> remembered)
    {
        ArgumentNullException.ThrowIfNull(remembered);
        foreach (var snapshot in remembered)
        {
            if (!IsDeviceMac(snapshot.MacAddress))
            {
                continue;
            }

            var key = TrafficPolicy.NormalizeMac(snapshot.MacAddress.ToString());
            devices.AddOrUpdate(
                key,
                _ => new DeviceRecord
                {
                    MacAddress = snapshot.MacAddress,
                    IpAddress = snapshot.IpAddress,
                    HostName = snapshot.HostName,
                    FirstSeen = snapshot.FirstSeen,
                    LastSeen = snapshot.LastSeen,
                    IsRemembered = true,
                },
                (_, existing) =>
                {
                    lock (existing.Sync)
                    {
                        if (existing.IsRemembered)
                        {
                            if (existing.IpAddress.Equals(IPAddress.Any) &&
                                !snapshot.IpAddress.Equals(IPAddress.Any))
                            {
                                existing.IpAddress = snapshot.IpAddress;
                            }

                            if (snapshot.LastSeen > existing.LastSeen)
                            {
                                existing.LastSeen = snapshot.LastSeen;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(existing.HostName))
                        {
                            existing.HostName = snapshot.HostName;
                        }
                    }

                    return existing;
                });
        }

        foreach (var key in Usage.Export().Devices.Keys)
        {
            if (PhysicalAddress.TryParse(key, out var mac) && IsDeviceMac(mac))
            {
                RememberUnknown(mac);
            }
        }
    }

    public void RememberUnknown(PhysicalAddress macAddress, string? hostName = null)
    {
        Remember(IPAddress.Any, macAddress, DateTimeOffset.MinValue, hostName);
    }

    /// <summary>
    /// Flushes cumulative bytes without consuming the independent live-rate window.
    /// </summary>
    public void FlushPendingUsage()
    {
        lock (snapshotSync)
        {
            foreach (var (key, record) in devices)
            {
                lock (record.Sync)
                {
                    DrainPendingBytes(key, record);
                }
            }
        }
    }

    private DeviceUsage DrainPendingBytes(string key, DeviceRecord record)
    {
        var download = record.PendingDownloadBytes;
        var upload = record.PendingUploadBytes;
        record.PendingDownloadBytes = 0;
        record.PendingUploadBytes = 0;
        return Usage.Add(key, download, upload);
    }

    public void Observe(
        IPAddress ipAddress,
        PhysicalAddress macAddress,
        DateTimeOffset seenAt,
        string? hostName = null) =>
        AddOrUpdate(ipAddress, macAddress, seenAt, hostName, refreshLastSeen: true);

    public void Remember(
        IPAddress ipAddress,
        PhysicalAddress macAddress,
        DateTimeOffset rememberedAt,
        string? hostName = null) =>
        AddOrUpdate(ipAddress, macAddress, rememberedAt, hostName, refreshLastSeen: false);

    private void AddOrUpdate(
        IPAddress ipAddress,
        PhysicalAddress macAddress,
        DateTimeOffset timestamp,
        string? hostName,
        bool refreshLastSeen)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);
        ArgumentNullException.ThrowIfNull(macAddress);
        var key = TrafficPolicy.NormalizeMac(macAddress.ToString());
        devices.AddOrUpdate(
            key,
            _ => new DeviceRecord
            {
                MacAddress = macAddress,
                IpAddress = ipAddress,
                HostName = hostName,
                FirstSeen = timestamp,
                LastSeen = refreshLastSeen ? timestamp : DateTimeOffset.MinValue,
                IsRemembered = !refreshLastSeen,
            },
            (_, existing) =>
            {
                lock (existing.Sync)
                {
                    if (!ipAddress.Equals(IPAddress.Any) &&
                        (refreshLastSeen || existing.IsRemembered ||
                         existing.IpAddress.Equals(IPAddress.Any)))
                    {
                        existing.IpAddress = ipAddress;
                    }

                    if (refreshLastSeen)
                    {
                        existing.LastSeen = timestamp;
                        existing.IsRemembered = false;
                    }

                    // Observation hints are persisted fallback names, not fresher
                    // identity evidence than a name learned in this process.
                    if (string.IsNullOrWhiteSpace(existing.HostName) &&
                        !string.IsNullOrWhiteSpace(hostName))
                    {
                        existing.HostName = hostName;
                    }
                }

                return existing;
            });
    }

    public void SetHostName(PhysicalAddress macAddress, string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return;
        }

        var key = TrafficPolicy.NormalizeMac(macAddress.ToString());
        if (devices.TryGetValue(key, out var record))
        {
            lock (record.Sync)
            {
                record.HostName = hostName;
            }
        }
    }

    public void RecordTraffic(
        PhysicalAddress macAddress,
        TrafficDirection direction,
        int byteCount,
        DateTimeOffset? seenAt = null)
    {
        if (byteCount < 0)
        {
            return;
        }

        var key = TrafficPolicy.NormalizeMac(macAddress.ToString());
        if (!devices.TryGetValue(key, out var record))
        {
            return;
        }

        lock (record.Sync)
        {
            record.LastSeen = seenAt ?? DateTimeOffset.UtcNow;
            record.IsRemembered = false;
            if (direction == TrafficDirection.Download)
            {
                record.PendingDownloadBytes += byteCount;
                record.RateDownloadBytes += byteCount;
            }
            else
            {
                record.PendingUploadBytes += byteCount;
                record.RateUploadBytes += byteCount;
            }
        }
    }

    public IReadOnlyList<DeviceSnapshot> TakeSnapshot(DateTimeOffset now)
    {
        lock (snapshotSync)
        {
            var seconds = Math.Max(0.001, (now - lastSnapshot).TotalSeconds);
            lastSnapshot = now;
            var snapshots = new List<DeviceSnapshot>(devices.Count);
            foreach (var (key, record) in devices)
            {
                lock (record.Sync)
                {
                    var usage = DrainPendingBytes(key, record);
                    var download = record.RateDownloadBytes;
                    var upload = record.RateUploadBytes;
                    record.RateDownloadBytes = 0;
                    record.RateUploadBytes = 0;
                    snapshots.Add(CreateSnapshot(record, usage, download / seconds, upload / seconds));
                }
            }

            return snapshots
                .OrderByDescending(device => device.TotalBytesPerSecond)
                .ThenBy(device => device.IpAddress.ToString(), StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<DeviceSnapshot> Peek()
    {
        return devices
            .Select(pair =>
            {
                var record = pair.Value;
                lock (record.Sync)
                {
                    return CreateSnapshot(record, Usage.Get(pair.Key), 0, 0);
                }
            })
            .ToArray();
    }

    private static DeviceSnapshot CreateSnapshot(
        DeviceRecord record,
        DeviceUsage usage,
        double downloadRate,
        double uploadRate) =>
        new(
            record.MacAddress,
            record.IpAddress,
            record.HostName,
            record.FirstSeen,
            record.LastSeen,
            downloadRate,
            uploadRate,
            usage.DownloadBytes,
            usage.UploadBytes,
            record.IsRemembered);

    private static bool IsDeviceMac(PhysicalAddress macAddress)
    {
        var bytes = macAddress.GetAddressBytes();
        return bytes.Length == 6 && bytes.Any(value => value != 0) && (bytes[0] & 1) == 0;
    }
}
