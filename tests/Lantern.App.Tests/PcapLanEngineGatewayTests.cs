using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using Lantern.App.Services;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Networking;
using Xunit;

namespace Lantern.App.Tests;

public sealed class PcapLanEngineGatewayTests
{
    private static readonly PhysicalAddress GatewayMac = PhysicalAddress.Parse("64644A380A15");
    private static readonly PhysicalAddress ClientMac = PhysicalAddress.Parse("E261190DBD54");
    private static readonly AdapterProfile Adapter = new("synthetic", "Test", "No capture",
        IPAddress.Parse("192.168.31.247"), 24, IPAddress.Parse("192.168.31.1"),
        PhysicalAddress.Parse("345A6063C052"));

    [Fact]
    public void GatewayMac_IsUnknownUntilResolvedAndDoesNotInferIdentityFromRememberedIp()
    {
        var registry = new DeviceRegistry();
        registry.Remember(Adapter.GatewayAddress, ClientMac, DateTimeOffset.UtcNow);
        var engine = new PcapLanEngine(registry, new TrafficPolicy());
        SetField(engine, "profile", Adapter);

        Assert.False(engine.IsGatewayMac(GatewayMac));
        Assert.False(engine.IsGatewayMac(ClientMac));
        SetField(engine, "gatewayMac", GatewayMac);

        Assert.True(engine.IsGatewayMac(PhysicalAddress.Parse("64644A380A15")));
        Assert.False(engine.IsGatewayMac(Assert.Single(registry.Peek()).MacAddress));
        Assert.False(engine.IsGatewayMac(Adapter.LocalMac));
    }

    [Fact]
    public async Task SyntheticStop_ClearsGatewayIdentityAndUsesTheSameFinalStatusWithoutOpeningCapture()
    {
        var engine = new PcapLanEngine(new DeviceRegistry(), new TrafficPolicy());
        SetField(engine, "profile", Adapter);
        SetField(engine, "gatewayMac", GatewayMac);
        PcapEngineStateChangedEventArgs? stopped = null;
        engine.StateChanged += (_, state) => stopped = state;

        await engine.StopAsync();

        Assert.False(engine.IsGatewayMac(GatewayMac));
        Assert.False(engine.IsRunning);
        Assert.NotNull(stopped);
        Assert.False(stopped.IsRunning);
        Assert.Equal(engine.LastStopStatus, stopped.StatusMessage);
        Assert.Equal(engine.StopStatus, engine.LastStopStatus);
        Assert.DoesNotContain("frames were sent", engine.LastStopStatus);
    }

    private static void SetField<T>(PcapLanEngine engine, string name, T value) =>
        typeof(PcapLanEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(engine, value);
}
