using System.Net.NetworkInformation;
using Lantern.App.Services;
using Lantern.Core.Networking;
using Xunit;

namespace Lantern.App.Tests;

public sealed class DomainObservationBufferTests
{
    private static readonly PhysicalAddress Phone = PhysicalAddress.Parse("021122334455");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-16T10:00:00Z");

    [Fact]
    public void Drain_PreservesCountsSourcesAndOriginalTimesWithoutPerEventCallbacks()
    {
        var buffer = new DomainObservationBuffer();
        buffer.Add(Observation("example.com", DomainObservationSource.Tls, Start.AddSeconds(2), true));
        buffer.Add(Observation("example.com", DomainObservationSource.Dns, Start));
        buffer.Add(Observation("example.com", DomainObservationSource.Http, Start.AddSeconds(1)));

        var row = Assert.Single(buffer.Drain().Observations);

        Assert.Equal(3, row.ObservationCount);
        Assert.Equal(Start, row.FirstSeen);
        Assert.Equal(Start.AddSeconds(2), row.LastSeen);
        Assert.Equal(3, row.Sources.Count);
        Assert.True(row.LastObservedDrop);
        Assert.Empty(buffer.Drain().Observations);
    }

    [Fact]
    public void Clear_DiscardsOldSessionAndChangesGeneration()
    {
        var buffer = new DomainObservationBuffer();
        buffer.Add(Observation("old.example", DomainObservationSource.Dns, Start));
        var previous = buffer.Drain();
        buffer.Add(Observation("queued.example", DomainObservationSource.Dns, Start));

        buffer.Clear();
        buffer.Add(Observation("new.example", DomainObservationSource.Tls, Start.AddSeconds(1)));
        var current = buffer.Drain();

        Assert.NotEqual(previous.Generation, current.Generation);
        Assert.Equal("new.example", Assert.Single(current.Observations).Domain);
        Assert.Equal(0, current.OmittedObservations);
    }

    [Fact]
    public void Clear_WithCutoffRejectsDelayedSignalsFromBeforeTheClear()
    {
        var buffer = new DomainObservationBuffer();
        buffer.Clear(Start.AddSeconds(2));
        buffer.Add(Observation("old.example", DomainObservationSource.Dns, Start));
        buffer.Add(Observation("current.example", DomainObservationSource.Tls, Start.AddSeconds(2)));

        Assert.Equal("current.example", Assert.Single(buffer.Drain().Observations).Domain);
    }

    [Fact]
    public void Overflow_IsBoundedAndReportsOmittedObservationsExplicitly()
    {
        var buffer = new DomainObservationBuffer(2);
        buffer.Add(Observation("first.example", DomainObservationSource.Dns, Start));
        buffer.Add(Observation("first.example", DomainObservationSource.Tls, Start));
        buffer.Add(Observation("second.example", DomainObservationSource.Dns, Start));
        buffer.Add(Observation("third.example", DomainObservationSource.Dns, Start));

        var flush = buffer.Drain();

        Assert.Equal(2, flush.Observations.Count);
        Assert.Equal(2, flush.OmittedObservations);
        Assert.DoesNotContain(flush.Observations, item => item.Domain == "first.example");
    }

    [Fact]
    public void ConcurrentProducers_DoNotLoseRepeatedObservations()
    {
        var buffer = new DomainObservationBuffer();
        Parallel.For(0, 2_000, _ => buffer.Add(Observation("example.com", DomainObservationSource.Dns, Start)));

        Assert.Equal(2_000, Assert.Single(buffer.Drain().Observations).ObservationCount);
    }

    private static DeviceDomainObservedEventArgs Observation(
        string domain,
        DomainObservationSource source,
        DateTimeOffset at,
        bool blocked = false) => new(Phone, new DomainObservation(domain, source, System.Net.IPAddress.Parse("203.0.113.25")), at, blocked);
}
