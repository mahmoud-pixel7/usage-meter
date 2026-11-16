using System.Net;
using System.Net.NetworkInformation;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Settings;
using Lantern.Core.Usage;

namespace Lantern.App.Services;

public static class SavedDeviceInventory
{
    public static IReadOnlyList<DeviceSnapshot> Build(AppSettings settings, UsageLedger usage)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(usage);
        var preferences = new Dictionary<string, DevicePreferences>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.Devices)
        {
            if (TryNormalizeMac(pair.Key, out var key))
            {
                preferences[key] = pair.Value;
            }
        }

        var totals = usage.Export().Devices;
        var keys = preferences.Keys.Concat(totals.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var devices = new List<DeviceSnapshot>();
        foreach (var key in keys)
        {
            if (!TryNormalizeMac(key, out var normalized))
            {
                continue;
            }

            preferences.TryGetValue(normalized, out var saved);
            var address = IPAddress.TryParse(saved?.LastKnownIp, out var parsed)
                ? parsed
                : IPAddress.Any;
            var lastSeen = saved?.LastSeenUtc ?? DateTimeOffset.MinValue;
            var total = totals.GetValueOrDefault(normalized, DeviceUsage.Empty);
            devices.Add(new DeviceSnapshot(
                PhysicalAddress.Parse(normalized),
                address,
                saved?.LearnedHostName,
                lastSeen,
                lastSeen,
                0,
                0,
                total.DownloadBytes,
                total.UploadBytes,
                IsRemembered: true));
        }

        return devices;
    }

    private static bool TryNormalizeMac(string candidate, out string normalized)
    {
        try
        {
            normalized = TrafficPolicy.NormalizeMac(candidate);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}
