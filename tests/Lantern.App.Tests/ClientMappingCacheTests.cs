using System.Net;
using System.Net.NetworkInformation;
using Lantern.App.Services;
using Lantern.Core.Networking;
using Xunit;

namespace Lantern.App.Tests;

public sealed class ClientMappingCacheTests
{
    [Fact]
    public void BeginAdapter_ClearsKnownClientsWhenRestartingSameAdapter()
    {
        var cache = new ClientMappingCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");

        cache.BeginAdapter("wifi-adapter");
        cache.Mappings[address] = mac;
        cache.BeginAdapter("wifi-adapter");

        Assert.Empty(cache.Mappings);
    }

    [Fact]
    public void BeginAdapter_ClearsKnownClientsWhenAdapterChanges()
    {
        var cache = new ClientMappingCache();
        cache.BeginAdapter("wifi-adapter");
        cache.Mappings[IPAddress.Parse("192.168.31.61")] =
            PhysicalAddress.Parse("E261190DBD54");

        cache.BeginAdapter("ethernet-adapter");

        Assert.Empty(cache.Mappings);
    }

    [Fact]
    public void Upsert_MovesOneMacToItsLatestAddress()
    {
        var cache = new ClientMappingCache();
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var oldAddress = IPAddress.Parse("192.168.31.61");
        var newAddress = IPAddress.Parse("192.168.31.213");

        cache.Upsert(oldAddress, mac);
        cache.Upsert(newAddress, mac);

        Assert.False(cache.Mappings.ContainsKey(oldAddress));
        Assert.Equal(mac, cache.Mappings[newAddress]);
    }

    [Fact]
    public void LatestAddressOnlyMode_ReportsAmbiguityInsteadOfClaimingMultiAddressCoverage()
    {
        var cache = new ClientMappingCache();
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var first = cache.UpsertChecked(IPAddress.Parse("192.168.31.61"), mac);
        var moved = cache.UpsertChecked(IPAddress.Parse("192.168.31.213"), mac);

        Assert.False(first.IsAmbiguous);
        Assert.True(moved.Accepted);
        Assert.True(moved.IsAmbiguous);
        Assert.NotEqual(first.BindingVersion, moved.BindingVersion);
        Assert.Single(cache.Mappings);
    }

    [Theory]
    [InlineData("192.168.31.247")]
    [InlineData("192.168.31.1")]
    [InlineData("192.168.31.0")]
    [InlineData("192.168.31.255")]
    [InlineData("192.168.32.61")]
    [InlineData("255.255.255.255")]
    [InlineData("2001:db8::1")]
    public void ScopedCache_RejectsProtectedBroadcastIpv6AndOutOfSubnetAddresses(string address)
    {
        var cache = CreateScopedCache();
        var update = cache.UpsertChecked(IPAddress.Parse(address), PhysicalAddress.Parse("E261190DBD54"));

        Assert.False(update.Accepted);
        Assert.False(update.Changed);
        Assert.Empty(cache.Mappings);
    }

    [Theory]
    [InlineData("345A6063C052")]
    [InlineData("64644A380A15")]
    [InlineData("FFFFFFFFFFFF")]
    [InlineData("000000000000")]
    [InlineData("01005E000001")]
    public void ScopedCache_RejectsControllerGatewayProxyAndNonUnicastMacs(string mac)
    {
        var cache = CreateScopedCache();

        var update = cache.UpsertChecked(IPAddress.Parse("192.168.31.61"), PhysicalAddress.Parse(mac));

        Assert.False(update.Accepted);
        Assert.Empty(cache.Mappings);
    }

    [Fact]
    public void GatewayProxyClaim_QuarantinesAnExistingClientAddress()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var original = cache.UpsertChecked(address, mac);

        var proxy = cache.UpsertChecked(address, PhysicalAddress.Parse("64644A380A15"));

        Assert.False(proxy.Accepted);
        Assert.Equal(ClientMappingRejection.GatewayOrProxyMac, proxy.Rejection);
        Assert.False(cache.IsCurrentBinding(address, mac, original.BindingVersion));
        Assert.Empty(cache.Mappings);
    }

    [Fact]
    public void CachedNeighbor_CannotReplaceAFreshObservedOwnerOrMoveItsAddress()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var observed = cache.UpsertChecked(address, mac);
        var staleOwner = cache.UpsertChecked(address, PhysicalAddress.Parse("0E4F69CCE4F0"), observed: false);
        var staleAddress = cache.UpsertChecked(IPAddress.Parse("192.168.31.213"), mac, observed: false);

        Assert.False(staleOwner.Accepted);
        Assert.False(staleAddress.Accepted);
        Assert.True(cache.IsCurrentBinding(address, mac, observed.BindingVersion, requireObserved: true));
        Assert.Equal(mac, Assert.Single(cache.Mappings).Value);
    }

    [Fact]
    public void BindingVersion_InvalidatesWorkWhenAddressChangesOwnersAndLaterReturns()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var first = cache.UpsertChecked(address, mac);
        cache.UpsertChecked(address, PhysicalAddress.Parse("0E4F69CCE4F0"));
        var returned = cache.UpsertChecked(address, mac);
        var applied = false;

        Assert.False(cache.WithCurrentBinding(address, mac, first.BindingVersion, () => applied = true));
        Assert.False(applied);
        Assert.NotEqual(first.BindingVersion, returned.BindingVersion);
        Assert.True(cache.IsCurrentBinding(address, mac, returned.BindingVersion));
    }

    [Fact]
    public void CachedOnlyBinding_IsNotObservationEvidenceForIdentity()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var cached = cache.UpsertChecked(address, mac, observed: false);

        Assert.False(cache.TryGetBinding(address, mac, out _, requireObserved: true));
        var observed = cache.UpsertChecked(address, mac);
        Assert.Equal(cached.BindingVersion, observed.BindingVersion);
        Assert.True(cache.TryGetBinding(address, mac, out _, requireObserved: true));
    }

    [Fact]
    public void InterceptionJournal_RetainsRetiredBindingsOutsideLiveMappings()
    {
        var cache = CreateScopedCache();
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var oldAddress = IPAddress.Parse("192.168.31.61");
        var newAddress = IPAddress.Parse("192.168.31.213");
        var first = cache.UpsertChecked(oldAddress, mac);
        Assert.True(cache.RememberInterception(oldAddress, mac, first.BindingVersion));
        var moved = cache.UpsertChecked(newAddress, mac);
        Assert.True(cache.RememberInterception(newAddress, mac, moved.BindingVersion));

        Assert.Single(cache.Mappings);
        Assert.False(cache.Mappings.ContainsKey(oldAddress));
        var targets = cache.GetRestorationTargets();
        Assert.Equal(2, targets.Count);
        Assert.False(targets.Single(target => target.Address.Equals(oldAddress)).RestoreGatewayMapping);
        Assert.True(targets.Single(target => target.Address.Equals(newAddress)).RestoreGatewayMapping);
    }

    [Fact]
    public void ReassignedIp_HistoricalOwnerIsNeverAssertedAtTheGateway()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var oldMac = PhysicalAddress.Parse("E261190DBD54");
        var newMac = PhysicalAddress.Parse("0E4F69CCE4F0");
        var first = cache.UpsertChecked(address, oldMac);
        Assert.True(cache.RememberInterception(address, oldMac, first.BindingVersion));
        var changed = cache.UpsertChecked(address, newMac);
        Assert.True(cache.RememberInterception(address, newMac, changed.BindingVersion));

        var targets = cache.GetRestorationTargets();

        Assert.Equal(newMac, Assert.Single(cache.Mappings).Value);
        Assert.False(targets.Single(target => target.MacAddress.Equals(oldMac)).RestoreGatewayMapping);
        Assert.True(targets.Single(target => target.MacAddress.Equals(newMac)).RestoreGatewayMapping);
    }

    [Fact]
    public void JournalCapacity_DoesNotEvictCleanupTargetsOrAllowUnjournalledInterception()
    {
        var cache = new ClientMappingCache(restorationCapacity: 1);
        var firstAddress = IPAddress.Parse("192.168.31.61");
        var firstMac = PhysicalAddress.Parse("E261190DBD54");
        var first = cache.UpsertChecked(firstAddress, firstMac);
        Assert.True(cache.RememberInterception(firstAddress, firstMac, first.BindingVersion));
        var secondAddress = IPAddress.Parse("192.168.31.62");
        var secondMac = PhysicalAddress.Parse("0E4F69CCE4F0");
        var second = cache.UpsertChecked(secondAddress, secondMac);

        Assert.False(cache.RememberInterception(secondAddress, secondMac, second.BindingVersion));
        Assert.True(cache.RememberInterception(firstAddress, firstMac, first.BindingVersion));
        Assert.Equal(1, cache.RestorationTargetCount);
        Assert.Equal(firstMac, Assert.Single(cache.GetRestorationTargets()).MacAddress);
    }

    [Fact]
    public void FreezeBindings_PreservesRoutingAndRestorationButRejectsLateOwnerChanges()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var original = cache.UpsertChecked(address, mac);
        Assert.True(cache.RememberInterception(address, mac, original.BindingVersion));
        cache.FreezeBindings();

        var late = cache.UpsertChecked(address, PhysicalAddress.Parse("0E4F69CCE4F0"));

        Assert.False(late.Accepted);
        Assert.Equal(ClientMappingRejection.SessionEnded, late.Rejection);
        Assert.True(cache.IsCurrentBinding(address, mac, original.BindingVersion));
        Assert.True(Assert.Single(cache.GetRestorationTargets()).RestoreGatewayMapping);
        Assert.False(cache.RememberInterception(address, mac, original.BindingVersion));
    }

    [Fact]
    public void Journal_IsSessionScopedAndRememberedBindingsAloneNeverCountAsIntercepted()
    {
        var cache = new ClientMappingCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var binding = cache.UpsertChecked(address, mac);
        Assert.Empty(cache.GetRestorationTargets());
        Assert.True(cache.RememberInterception(address, mac, binding.BindingVersion));

        cache.BeginAdapter("new-session");

        Assert.Equal(0, cache.RestorationTargetCount);
        Assert.Empty(cache.GetRestorationTargets());
        Assert.Empty(cache.Mappings);
    }

    [Fact]
    public void RejectedProxyClaim_PreservesTheRetiredCleanupTargetWithoutRoutingIt()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var binding = cache.UpsertChecked(address, mac);
        Assert.True(cache.RememberInterception(address, mac, binding.BindingVersion));

        var proxy = cache.UpsertChecked(address, PhysicalAddress.Parse("64644A380A15"));

        Assert.False(proxy.Accepted);
        Assert.Empty(cache.Mappings);
        var target = Assert.Single(cache.GetRestorationTargets());
        Assert.Equal(mac, target.MacAddress);
        Assert.Equal(address, target.Address);
        Assert.False(target.RestoreGatewayMapping);
    }

    [Fact]
    public void FrozenBindings_CannotBeRemovedByLateGatewayPublicationAndTheNextSessionCanUpdateAgain()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var binding = cache.UpsertChecked(address, mac);
        Assert.True(cache.RememberInterception(address, mac, binding.BindingVersion));
        cache.FreezeBindings();

        cache.SetGatewayMac(mac);

        Assert.True(cache.IsCurrentBinding(address, mac, binding.BindingVersion));
        Assert.True(Assert.Single(cache.GetRestorationTargets()).RestoreGatewayMapping);
        cache.BeginAdapter("next-session");
        Assert.True(cache.UpsertChecked(address, mac).Accepted);
        Assert.Empty(cache.GetRestorationTargets());
    }

    [Fact]
    public void Journal_RejectsStaleBindingVersionsEvenWhenTheOldOwnerReturns()
    {
        var cache = CreateScopedCache();
        var address = IPAddress.Parse("192.168.31.61");
        var mac = PhysicalAddress.Parse("E261190DBD54");
        var original = cache.UpsertChecked(address, mac);
        cache.UpsertChecked(address, PhysicalAddress.Parse("0E4F69CCE4F0"));
        Assert.False(cache.RememberInterception(address, mac, original.BindingVersion));
        var returned = cache.UpsertChecked(address, mac);

        Assert.False(cache.RememberInterception(address, mac, original.BindingVersion));
        Assert.Empty(cache.GetRestorationTargets());
        Assert.True(cache.RememberInterception(address, mac, returned.BindingVersion));
        Assert.Single(cache.GetRestorationTargets());
    }

    private static ClientMappingCache CreateScopedCache()
    {
        var cache = new ClientMappingCache();
        cache.BeginAdapter(new AdapterProfile("synthetic", "Test", "No capture",
            IPAddress.Parse("192.168.31.247"), 24, IPAddress.Parse("192.168.31.1"),
            PhysicalAddress.Parse("345A6063C052")));
        cache.SetGatewayMac(PhysicalAddress.Parse("64644A380A15"));
        return cache;
    }
}
