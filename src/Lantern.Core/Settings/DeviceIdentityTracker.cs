using System.Net;
using System.Net.Sockets;
using Lantern.Core.Control;

namespace Lantern.Core.Settings;

public sealed record DeviceIdentityResult(
    string MacKey,
    string? PreviousMacKey,
    DevicePreferences Preferences);

public static class DeviceIdentityTracker
{
    public static DeviceIdentityResult Learn(
        AppSettings settings,
        string macAddress,
        string hostName,
        string? lastKnownIp)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var macKey = TrafficPolicy.NormalizeMac(macAddress);
        var normalizedHostName = NormalizeHostName(hostName);
        var normalizedIp = NormalizeIpv4(lastKnownIp);
        settings.Devices.TryGetValue(macKey, out var current);

        current ??= settings.Devices[macKey] = new DevicePreferences();
        current.LearnedHostName = normalizedHostName;
        if (normalizedIp is not null)
        {
            current.LastKnownIp = normalizedIp;
        }

        return new DeviceIdentityResult(macKey, null, current);
    }

    private static string NormalizeHostName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    private static string? NormalizeIpv4(string? value) =>
        IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == AddressFamily.InterNetwork
            ? address.ToString()
            : null;
}
