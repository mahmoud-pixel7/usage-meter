using System.Net;
using System.Net.NetworkInformation;
using Lantern.App.Services;
using Xunit;

namespace Lantern.App.Tests;

public sealed class NameResolutionRetryGateTests
{
    private static readonly PhysicalAddress Mac = PhysicalAddress.Parse("E261190DBD54");
    private static readonly IPAddress Address = IPAddress.Parse("192.168.31.61");

    [Fact]
    public void Failure_ReleasesInflightAndRetriesWithBackoffInsteadOfRememberingFailureForever()
    {
        var now = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        var gate = new NameResolutionRetryGate(() => now);
        gate.BeginSession(1);
        Assert.True(gate.TryBegin(1, Address, Mac, 10, out var first));
        Assert.False(gate.TryBegin(1, Address, Mac, 10, out _));

        gate.Complete(first, resolved: false);
        Assert.False(gate.IsCurrentAttempt(first));
        Assert.False(gate.TryBegin(1, Address, Mac, 10, out _));
        now += TimeSpan.FromSeconds(5);
        Assert.True(gate.TryBegin(1, Address, Mac, 10, out var second));
        gate.Complete(second, resolved: false);
        now += TimeSpan.FromSeconds(5);
        Assert.False(gate.TryBegin(1, Address, Mac, 10, out _));
        now += TimeSpan.FromSeconds(5);
        Assert.True(gate.TryBegin(1, Address, Mac, 10, out _));
    }

    [Fact]
    public void SuccessfulResolution_UsesALongerCooldownButIsNotPermanentlyInFlight()
    {
        var now = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        var gate = new NameResolutionRetryGate(() => now);
        gate.BeginSession(1);
        Assert.True(gate.TryBegin(1, Address, Mac, 10, out var attempt));

        gate.Complete(attempt, resolved: true);
        now += TimeSpan.FromMinutes(29);
        Assert.False(gate.TryBegin(1, Address, Mac, 10, out _));
        now += TimeSpan.FromMinutes(1);
        Assert.True(gate.TryBegin(1, Address, Mac, 10, out _));
    }

    [Fact]
    public void NewBinding_CanRetryImmediatelyAndOldCompletionCannotReleaseNewInflightWork()
    {
        var now = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        var gate = new NameResolutionRetryGate(() => now);
        gate.BeginSession(1);
        Assert.True(gate.TryBegin(1, Address, Mac, 10, out var oldAttempt));
        var newAddress = IPAddress.Parse("192.168.31.213");
        Assert.True(gate.TryBegin(1, newAddress, Mac, 11, out var current));

        gate.Complete(oldAttempt, resolved: true);

        Assert.False(gate.IsCurrentAttempt(oldAttempt));
        Assert.True(gate.IsCurrentAttempt(current));
        Assert.False(gate.TryBegin(1, newAddress, Mac, 11, out _));
    }

    [Fact]
    public void StopAndRestart_InvalidateOldSessionWorkEvenWhenMacAndIpAreIdentical()
    {
        var gate = new NameResolutionRetryGate();
        gate.BeginSession(1);
        Assert.True(gate.TryBegin(1, Address, Mac, 10, out var oldAttempt));
        gate.EndSession();
        Assert.False(gate.IsCurrentAttempt(oldAttempt));
        Assert.False(gate.TryBegin(1, Address, Mac, 10, out _));
        gate.BeginSession(2);
        Assert.True(gate.TryBegin(2, Address, Mac, 10, out var current));

        gate.Complete(oldAttempt, resolved: true);

        Assert.True(gate.IsCurrentAttempt(current));
        Assert.False(gate.TryBegin(0, Address, Mac, 10, out _));
        Assert.False(gate.TryBegin(1, Address, Mac, 10, out _));
    }

    [Fact]
    public void FreshDhcpIdentity_InvalidatesAnOlderPendingResolverResult()
    {
        var now = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        var gate = new NameResolutionRetryGate(() => now);
        gate.BeginSession(1);
        Assert.True(gate.TryBegin(1, Address, Mac, 10, out var pending));

        gate.MarkResolved(1, Address, Mac, 10);
        gate.Complete(pending, resolved: false);

        Assert.False(gate.IsCurrentAttempt(pending));
        now += TimeSpan.FromMinutes(1);
        Assert.False(gate.TryBegin(1, Address, Mac, 10, out _));
    }

    [Fact]
    public void SessionAndBindingGuardsTogether_RejectAResultAfterTheIpChangesOwners()
    {
        var gate = new NameResolutionRetryGate();
        var cache = new ClientMappingCache();
        gate.BeginSession(1);
        var first = cache.UpsertChecked(Address, Mac);
        Assert.True(gate.TryBegin(1, Address, Mac, first.BindingVersion, out var pending));
        cache.UpsertChecked(Address, PhysicalAddress.Parse("0E4F69CCE4F0"));
        cache.UpsertChecked(Address, Mac);
        var applied = false;

        if (gate.IsCurrentAttempt(pending))
        {
            cache.WithCurrentBinding(pending.Address, Mac, pending.BindingVersion, () => applied = true);
        }

        Assert.False(applied);
    }

    [Fact]
    public void FailureBackoff_IsCappedAtFiveMinutes()
    {
        var now = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        var gate = new NameResolutionRetryGate(() => now);
        gate.BeginSession(1);
        for (var index = 0; index < 10; index++)
        {
            Assert.True(gate.TryBegin(1, Address, Mac, 10, out var attempt));
            gate.Complete(attempt, resolved: false);
            now += TimeSpan.FromMinutes(5);
        }

        Assert.True(gate.TryBegin(1, Address, Mac, 10, out _));
    }
}
