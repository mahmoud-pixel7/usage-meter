using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using Lantern.App.Services;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Xunit;

namespace Lantern.App.Tests;

public sealed class PcapLanEngineIdentityTests
{
    private static readonly PhysicalAddress Mac = PhysicalAddress.Parse("E261190DBD54");
    private static readonly IPAddress Address = IPAddress.Parse("192.168.31.61");
    private static readonly DateTimeOffset SeenAt = DateTimeOffset.Parse("2026-11-15T10:00:00Z");

    [Fact]
    public void LearnedIdentity_IsStampedAndInvalidAfterOwnerChangesEvenIfTheOriginalOwnerReturns()
    {
        var state = CreateSyntheticState();
        DeviceIdentityLearnedEventArgs? learned = null;
        state.Engine.DeviceIdentityLearned += (_, identity) => learned = identity;

        Assert.True(ApplyIdentity(state.Engine, state.Version, "Phone"));
        Assert.NotNull(learned);
        Assert.Equal(1, learned.SessionId);
        Assert.Equal(Address, learned.Address);
        Assert.True(state.Engine.IsIdentityCurrent(learned));
        state.Cache.UpsertChecked(Address, PhysicalAddress.Parse("0E4F69CCE4F0"));
        Assert.False(state.Engine.IsIdentityCurrent(learned));
        var returned = state.Cache.UpsertChecked(Address, Mac);
        Assert.False(state.Engine.IsIdentityCurrent(learned));

        Assert.True(ApplyIdentity(state.Engine, returned.BindingVersion, "Phone"));
        Assert.True(state.Engine.IsIdentityCurrent(learned!));
    }

    [Fact]
    public void NewerIdentityAndSession_InvalidateQueuedOlderEventsBeforePersistence()
    {
        var state = CreateSyntheticState();
        var learned = new List<DeviceIdentityLearnedEventArgs>();
        state.Engine.DeviceIdentityLearned += (_, identity) => learned.Add(identity);
        Assert.True(ApplyIdentity(state.Engine, state.Version, "Old phone name"));
        Assert.True(ApplyIdentity(state.Engine, state.Version, "New phone name"));
        Assert.Equal(2, learned.Count);
        Assert.False(state.Engine.IsIdentityCurrent(learned[0]));
        Assert.True(state.Engine.IsIdentityCurrent(learned[1]));

        SetField(state.Engine, "activeSessionId", 2L);
        state.RetryGate.BeginSession(2);
        Assert.False(state.Engine.IsIdentityCurrent(learned[1]));
        SetField(state.Engine, "activeSessionId", 0L);
        SetField(state.Engine, "controlling", false);
        Assert.False(state.Engine.IsIdentityCurrent(learned[1]));
    }

    [Fact]
    public void PendingResolverResult_CannotApplyAfterBindingOrSessionChanges()
    {
        var state = CreateSyntheticState();
        Assert.True(state.RetryGate.TryBegin(1, Address, Mac, state.Version, out var pending));
        state.Cache.UpsertChecked(Address, PhysicalAddress.Parse("0E4F69CCE4F0"));

        Assert.False(ApplyIdentity(state.Engine, state.Version, "Stale name", pending));
        Assert.Null(Assert.Single(state.Registry.Peek()).HostName);
        state.Cache.UpsertChecked(Address, Mac);
        SetField(state.Engine, "activeSessionId", 2L);
        state.RetryGate.BeginSession(2);
        Assert.False(ApplyIdentity(state.Engine, state.Version, "Old session name", pending));
        Assert.Null(Assert.Single(state.Registry.Peek()).HostName);
    }

    [Fact]
    public void DhcpIdentity_RequiresTheActualObservedMacAndIpAndInvalidatesOlderResolverWork()
    {
        var state = CreateSyntheticState();
        var identities = new List<DeviceIdentityLearnedEventArgs>();
        state.Engine.DeviceIdentityLearned += (_, identity) => identities.Add(identity);
        Assert.True(state.RetryGate.TryBegin(1, Address, Mac, state.Version, out var pending));
        var observeDhcp = GetMethod(state.Engine, "ObserveDhcpIdentity").CreateDelegate<ObserveFrame>(state.Engine);

        observeDhcp(BuildDhcpFrame(IPAddress.Any, Mac, "Unbound name"));
        observeDhcp(BuildDhcpFrame(Address, PhysicalAddress.Parse("64644A380A15"), "Wrong sender"));
        Assert.Empty(identities);
        observeDhcp(BuildDhcpFrame(Address, Mac, "Fresh DHCP name"));

        var identity = Assert.Single(identities);
        Assert.Equal(1, identity.SessionId);
        Assert.Equal(Address, identity.Address);
        Assert.True(state.Engine.IsIdentityCurrent(identity));
        Assert.Equal("Fresh DHCP name", Assert.Single(state.Registry.Peek()).HostName);
        Assert.False(state.RetryGate.IsCurrentAttempt(pending));
        Assert.False(ApplyIdentity(state.Engine, state.Version, "Late resolver name", pending));
        Assert.Equal("Fresh DHCP name", Assert.Single(state.Registry.Peek()).HostName);
    }

    [Fact]
    public void CachedOnlyBinding_CannotSupplyLiveDhcpIdentityEvidence()
    {
        var state = CreateSyntheticState(observed: false);
        var identities = new List<DeviceIdentityLearnedEventArgs>();
        state.Engine.DeviceIdentityLearned += (_, identity) => identities.Add(identity);
        var observeDhcp = GetMethod(state.Engine, "ObserveDhcpIdentity").CreateDelegate<ObserveFrame>(state.Engine);

        observeDhcp(BuildDhcpFrame(Address, Mac, "Unconfirmed cached device"));

        Assert.Empty(identities);
        Assert.Null(Assert.Single(state.Registry.Peek()).HostName);
        Assert.True(Assert.Single(state.Registry.Peek()).IsRemembered);
    }

    private static SyntheticState CreateSyntheticState(bool observed = true)
    {
        var registry = new DeviceRegistry(SeenAt);
        registry.Remember(Address, Mac, SeenAt);
        var engine = new PcapLanEngine(registry, new TrafficPolicy());
        var cache = GetField<ClientMappingCache>(engine, "clientMappings");
        cache.BeginAdapter("synthetic-no-capture");
        var binding = cache.UpsertChecked(Address, Mac, observed);
        var gate = GetField<NameResolutionRetryGate>(engine, "nameRetryGate");
        gate.BeginSession(1);
        SetField(engine, "activeSessionId", 1L);
        SetField(engine, "controlling", true);
        return new SyntheticState(engine, registry, cache, gate, binding.BindingVersion);
    }

    private static bool ApplyIdentity(
        PcapLanEngine engine,
        long version,
        string name,
        NameResolutionAttempt? attempt = null) =>
        (bool)GetMethod(engine, "TryApplyIdentity").Invoke(engine, [1L, Address, Mac, version, name, attempt])!;

    private static MethodInfo GetMethod(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField<T>(object target, string name, T value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static byte[] BuildDhcpFrame(IPAddress source, PhysicalAddress ethernetSource, string hostName)
    {
        var name = Encoding.UTF8.GetBytes(hostName);
        const int dhcpOffset = 14 + 20 + 8;
        var frame = new byte[dhcpOffset + 240 + 2 + name.Length + 1];
        Array.Fill(frame, (byte)0xff, 0, 6);
        ethernetSource.GetAddressBytes().CopyTo(frame, 6);
        frame[12] = 8;
        frame[14] = 0x45;
        var networkLength = frame.Length - 14;
        frame[16] = (byte)(networkLength >> 8);
        frame[17] = (byte)networkLength;
        frame[23] = 17;
        source.GetAddressBytes().CopyTo(frame, 26);
        IPAddress.Broadcast.GetAddressBytes().CopyTo(frame, 30);
        frame[35] = 68;
        frame[37] = 67;
        var udpLength = frame.Length - 34;
        frame[38] = (byte)(udpLength >> 8);
        frame[39] = (byte)udpLength;
        frame[dhcpOffset] = 1;
        frame[dhcpOffset + 1] = 1;
        frame[dhcpOffset + 2] = 6;
        Mac.GetAddressBytes().CopyTo(frame, dhcpOffset + 28);
        frame[dhcpOffset + 236] = 99;
        frame[dhcpOffset + 237] = 130;
        frame[dhcpOffset + 238] = 83;
        frame[dhcpOffset + 239] = 99;
        frame[dhcpOffset + 240] = 12;
        frame[dhcpOffset + 241] = (byte)name.Length;
        name.CopyTo(frame, dhcpOffset + 242);
        frame[^1] = 255;
        return frame;
    }

    private delegate void ObserveFrame(ReadOnlySpan<byte> frame);

    private sealed record SyntheticState(
        PcapLanEngine Engine,
        DeviceRegistry Registry,
        ClientMappingCache Cache,
        NameResolutionRetryGate RetryGate,
        long Version);
}
