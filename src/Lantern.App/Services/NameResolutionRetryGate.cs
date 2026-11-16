using System.Net;
using System.Net.NetworkInformation;

namespace Lantern.App.Services;

public readonly record struct NameResolutionAttempt(
    long SessionId,
    string MacKey,
    IPAddress Address,
    long BindingVersion,
    long AttemptId);

public sealed class NameResolutionRetryGate
{
    private readonly object sync = new();
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private long sessionId;
    private long nextAttemptId;

    public NameResolutionRetryGate(Func<DateTimeOffset>? utcNow = null)
    {
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void BeginSession(long id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        lock (sync)
        {
            sessionId = id;
            entries.Clear();
        }
    }

    public void EndSession()
    {
        lock (sync)
        {
            sessionId = 0;
            entries.Clear();
        }
    }

    public bool TryBegin(
        long id,
        IPAddress address,
        PhysicalAddress macAddress,
        long bindingVersion,
        out NameResolutionAttempt attempt)
    {
        lock (sync)
        {
            attempt = default;
            if (id == 0 || id != sessionId || bindingVersion <= 0)
            {
                return false;
            }

            var key = macAddress.ToString();
            if (!entries.TryGetValue(key, out var entry) ||
                entry.BindingVersion != bindingVersion || !entry.Address.Equals(address))
            {
                entry = new Entry(address, bindingVersion);
                entries[key] = entry;
            }

            if (entry.InFlightId != 0 || utcNow() < entry.NextAttemptAt)
            {
                return false;
            }

            entry.InFlightId = ++nextAttemptId;
            attempt = new NameResolutionAttempt(id, key, address, bindingVersion, entry.InFlightId);
            return true;
        }
    }

    public bool IsCurrentAttempt(NameResolutionAttempt attempt)
    {
        lock (sync)
        {
            return FindCurrent(attempt) is not null;
        }
    }

    public void Complete(NameResolutionAttempt attempt, bool resolved)
    {
        lock (sync)
        {
            if (FindCurrent(attempt) is not { } entry)
            {
                return;
            }

            entry.InFlightId = 0;
            entry.Failures = resolved ? 0 : Math.Min(7, entry.Failures + 1);
            var delay = resolved
                ? TimeSpan.FromMinutes(30)
                : TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, entry.Failures - 1)));
            entry.NextAttemptAt = utcNow() + delay;
        }
    }

    public void MarkResolved(
        long id,
        IPAddress address,
        PhysicalAddress macAddress,
        long bindingVersion)
    {
        lock (sync)
        {
            if (id != sessionId || id == 0)
            {
                return;
            }

            entries[macAddress.ToString()] = new Entry(address, bindingVersion)
            {
                NextAttemptAt = utcNow() + TimeSpan.FromMinutes(30),
            };
        }
    }

    private Entry? FindCurrent(NameResolutionAttempt attempt) =>
        attempt.SessionId != 0 && attempt.SessionId == sessionId &&
        entries.TryGetValue(attempt.MacKey, out var entry) &&
        entry.Address.Equals(attempt.Address) && entry.BindingVersion == attempt.BindingVersion &&
        entry.InFlightId != 0 && entry.InFlightId == attempt.AttemptId
            ? entry
            : null;

    private sealed class Entry(IPAddress address, long bindingVersion)
    {
        public IPAddress Address { get; } = address;

        public long BindingVersion { get; } = bindingVersion;

        public long InFlightId { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset NextAttemptAt { get; set; }
    }
}
