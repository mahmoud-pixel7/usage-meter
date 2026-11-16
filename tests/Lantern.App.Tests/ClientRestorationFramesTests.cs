using System.Net;
using System.Net.NetworkInformation;
using Lantern.App.Services;
using Lantern.Core.Networking;
using Xunit;

namespace Lantern.App.Tests;

public sealed class ClientRestorationFramesTests
{
    private static readonly PhysicalAddress LocalMac = PhysicalAddress.Parse("345A6063C052");
    private static readonly PhysicalAddress GatewayMac = PhysicalAddress.Parse("64644A380A15");
    private static readonly PhysicalAddress ClientMac = PhysicalAddress.Parse("E261190DBD54");
    private static readonly IPAddress ClientIp = IPAddress.Parse("192.168.31.61");
    private static readonly AdapterProfile Adapter = new("synthetic", "Test", "No capture",
        IPAddress.Parse("192.168.31.247"), 24, IPAddress.Parse("192.168.31.1"), LocalMac);

    [Fact]
    public void RetiredBinding_RepairsOnlyTheClientSideAndRequestsRecoveryWithoutAssertingAnOldOwner()
    {
        var frames = ClientRestorationFrames.Build(Adapter, GatewayMac,
            new ClientRestorationTarget(ClientIp, ClientMac, RestoreGatewayMapping: false));

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame =>
        {
            Assert.True(EthernetFrameCodec.TryParseArp(frame, out var arp));
            Assert.Equal(Adapter.GatewayAddress, arp.SenderIp);
            Assert.Equal(GatewayMac, arp.SenderMac);
            Assert.Equal(ClientMac, arp.TargetMac);
            Assert.Equal(ClientIp, arp.TargetIp);
        });
    }

    [Fact]
    public void CurrentBinding_CanAlsoRestoreItsCurrentGatewayMapping()
    {
        var frames = ClientRestorationFrames.Build(Adapter, GatewayMac,
            new ClientRestorationTarget(ClientIp, ClientMac, RestoreGatewayMapping: true));
        var parsed = frames.Select(frame =>
        {
            Assert.True(EthernetFrameCodec.TryParseArp(frame, out var arp));
            return arp;
        }).ToArray();

        Assert.Equal(3, frames.Count);
        var toGateway = Assert.Single(parsed, arp => arp.SenderIp.Equals(ClientIp));
        Assert.Equal(ClientMac, toGateway.SenderMac);
        Assert.Equal(GatewayMac, toGateway.TargetMac);
        Assert.Equal(Adapter.GatewayAddress, toGateway.TargetIp);
    }

    [Fact]
    public void ReassignedAddress_RestorationJournalNeverOverwritesTheNewOwnerWithTheRetiredOne()
    {
        var cache = new ClientMappingCache();
        cache.BeginAdapter(Adapter);
        cache.SetGatewayMac(GatewayMac);
        var first = cache.UpsertChecked(ClientIp, ClientMac);
        Assert.True(cache.RememberInterception(ClientIp, ClientMac, first.BindingVersion));
        var newMac = PhysicalAddress.Parse("0E4F69CCE4F0");
        var changed = cache.UpsertChecked(ClientIp, newMac);
        Assert.True(cache.RememberInterception(ClientIp, newMac, changed.BindingVersion));
        cache.FreezeBindings();
        var frames = cache.GetRestorationTargets()
            .SelectMany(target => ClientRestorationFrames.Build(Adapter, GatewayMac, target));
        var gatewayAnnouncements = frames.Select(frame =>
        {
            Assert.True(EthernetFrameCodec.TryParseArp(frame, out var arp));
            return arp;
        }).Where(arp => arp.SenderIp.Equals(ClientIp)).ToArray();

        Assert.Equal(newMac, Assert.Single(gatewayAnnouncements).SenderMac);
        Assert.DoesNotContain(gatewayAnnouncements, arp => arp.SenderMac.Equals(ClientMac));
    }

    [Theory]
    [InlineData("192.168.31.247")]
    [InlineData("192.168.31.1")]
    [InlineData("192.168.31.0")]
    [InlineData("192.168.31.255")]
    [InlineData("192.168.32.61")]
    [InlineData("2001:db8::1")]
    public void ProtectedOutOfSubnetAndIpv6Addresses_NeverGenerateRepairOrInterceptionFrames(string address)
    {
        var frames = ClientRestorationFrames.Build(Adapter, GatewayMac,
            new ClientRestorationTarget(IPAddress.Parse(address), ClientMac, true));

        Assert.Empty(frames);
    }
}
