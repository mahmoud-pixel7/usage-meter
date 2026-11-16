using System.Net.NetworkInformation;
using Lantern.Core.Control;
using Lantern.Core.Networking;

namespace Lantern.App.Services;

public sealed record BufferedDomainObservation(
    PhysicalAddress MacAddress,
    string Domain,
    IReadOnlyList<DomainObservationSource> Sources,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int ObservationCount,
    bool LastObservedDrop);

public sealed record DomainObservationFlush(
    long Generation,
    IReadOnlyList<BufferedDomainObservation> Observations,
    long OmittedObservations);

public sealed class DomainObservationBuffer(int capacity = 2048)
{
    private readonly object sync = new();
    private readonly int capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly Dictionary<string, PendingObservation> pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> arrivalOrder = new();
    private long generation;
    private long omittedObservations;
    private DateTimeOffset? discardBefore;

    public void Add(DeviceDomainObservedEventArgs observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var key = $"{TrafficPolicy.NormalizeMac(observation.MacAddress.ToString())}|{observation.Observation.Domain}";
        lock (sync)
        {
            if (discardBefore is { } cutoff && observation.ObservedAt < cutoff)
            {
                return;
            }

            if (pending.TryGetValue(key, out var existing))
            {
                existing.Observe(observation);
                return;
            }

            if (pending.Count == capacity)
            {
                var oldest = arrivalOrder.Dequeue();
                omittedObservations = SaturatingAdd(omittedObservations, pending[oldest].Count);
                pending.Remove(oldest);
            }

            pending.Add(key, new PendingObservation(observation));
            arrivalOrder.Enqueue(key);
        }
    }

    public DomainObservationFlush Drain()
    {
        lock (sync)
        {
            var observations = arrivalOrder
                .Select(key => pending[key].Snapshot())
                .ToArray();
            pending.Clear();
            arrivalOrder.Clear();
            return new DomainObservationFlush(generation, observations, omittedObservations);
        }
    }

    public void Clear(DateTimeOffset? discardBefore = null)
    {
        lock (sync)
        {
            pending.Clear();
            arrivalOrder.Clear();
            omittedObservations = 0;
            this.discardBefore = discardBefore;
            generation++;
        }
    }

    private static long SaturatingAdd(long value, int amount) =>
        long.MaxValue - value < amount ? long.MaxValue : value + amount;

    private sealed class PendingObservation(DeviceDomainObservedEventArgs first)
    {
        private readonly HashSet<DomainObservationSource> sources = [first.Observation.Source];
        private DateTimeOffset firstSeen = first.ObservedAt;
        private DateTimeOffset lastSeen = first.ObservedAt;
        private bool lastObservedDrop = first.Blocked;

        public int Count { get; private set; } = 1;

        public void Observe(DeviceDomainObservedEventArgs observation)
        {
            sources.Add(observation.Observation.Source);
            if (observation.ObservedAt < firstSeen)
            {
                firstSeen = observation.ObservedAt;
            }

            if (observation.ObservedAt >= lastSeen)
            {
                lastSeen = observation.ObservedAt;
                lastObservedDrop = observation.Blocked;
            }

            Count = Count == int.MaxValue ? int.MaxValue : Count + 1;
        }

        public BufferedDomainObservation Snapshot() => new(
            first.MacAddress,
            first.Observation.Domain,
            sources.OrderBy(source => source).ToArray(),
            firstSeen,
            lastSeen,
            Count,
            lastObservedDrop);
    }
}
