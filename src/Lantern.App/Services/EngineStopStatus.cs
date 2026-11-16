namespace Lantern.App.Services;

public static class EngineStopStatus
{
    public static string Format(
        bool hadRestorationTargets,
        IEnumerable<string> restorationWarnings,
        string? engineFailure = null)
    {
        ArgumentNullException.ThrowIfNull(restorationWarnings);
        var warnings = restorationWarnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).ToArray();
        var prefix = string.IsNullOrWhiteSpace(engineFailure)
            ? "Stopped."
            : $"Stopped after an engine error: {engineFailure}.";
        if (warnings.Length > 0)
        {
            return $"{prefix} Network restoration is incomplete or unverified: {string.Join("; ", warnings)}";
        }

        return hadRestorationTargets
            ? $"{prefix} Corrective IPv4 ARP frames were sent; peer acceptance cannot be verified."
            : $"{prefix} No client IPv4 ARP mappings were available for restoration.";
    }
}
