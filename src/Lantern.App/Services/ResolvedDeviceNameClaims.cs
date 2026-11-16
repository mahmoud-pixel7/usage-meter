using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;

namespace Lantern.App.Services;

public sealed class ResolvedDeviceNameClaims
{
    private readonly ConcurrentDictionary<string, string> namesByMac =
        new(StringComparer.OrdinalIgnoreCase);

    public ResolvedDeviceNameClaims(string controllerHostName)
    {
        // A shared hostname, including the controller's, is not identity evidence.
    }

    public void Reset(IEnumerable<string>? rejected = null)
    {
        namesByMac.Clear();
    }

    public bool TryClaim(
        PhysicalAddress macAddress,
        string? candidate,
        out string acceptedName)
    {
        return TryClaim(macAddress, candidate, out acceptedName, out _);
    }

    public bool TryClaim(
        PhysicalAddress macAddress,
        string? candidate,
        out string acceptedName,
        out bool isNewClaim)
    {
        ArgumentNullException.ThrowIfNull(macAddress);
        isNewClaim = false;
        acceptedName = Normalize(candidate) ?? string.Empty;
        if (acceptedName.Length == 0)
        {
            return false;
        }

        var macKey = macAddress.ToString();
        while (true)
        {
            if (!namesByMac.TryGetValue(macKey, out var previousName))
            {
                if (namesByMac.TryAdd(macKey, acceptedName))
                {
                    isNewClaim = true;
                    return true;
                }

                continue;
            }

            if (string.Equals(previousName, acceptedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (namesByMac.TryUpdate(macKey, acceptedName, previousName))
            {
                isNewClaim = true;
                return true;
            }
        }
    }

    private static string? Normalize(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrEmpty(name) ||
               name.Length > 253 ||
               name.Any(char.IsControl) ||
               IPAddress.TryParse(name, out _)
            ? null
            : name;
    }
}
