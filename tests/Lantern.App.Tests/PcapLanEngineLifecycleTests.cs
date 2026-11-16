using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using Lantern.App.Services;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Networking;
using Xunit;

namespace Lantern.App.Tests;

public sealed class PcapLanEngineLifecycleTests
{
    private static readonly PhysicalAddress GatewayMac = PhysicalAddress.Parse("64644A380A15");
    private static readonly PhysicalAddress ClientMac = PhysicalAddress.Parse("E261190DBD54");
    private static readonly IPAddress ClientIp = IPAddress.Parse("192.168.31.61");
    private static readonly AdapterProfile Adapter = new("synthetic", "Test", "No capture",
        IPAddress.Parse("192.168.31.247"), 24, IPAddress.Parse("192.168.31.1"),
        PhysicalAddress.Parse("345A6063C052"));

    [Fact]
    public async Task StopDuringPendingGatewayResolution_CancelsTheWaitAndRejectsLateResolution()
    {
        var engine = CreateSyntheticEngine();
        var pending = new TaskCompletionSource<PhysicalAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(engine, "gatewayResolution", pending);

        await engine.StopAsync();

        Assert.True(pending.Task.IsCanceled);
        Assert.False(pending.TrySetResult(GatewayMac));
        Assert.False(engine.IsGatewayMac(GatewayMac));
        Assert.False(engine.IsRunning);
        Assert.Equal(0, engine.SessionId);
        Assert.Null(GetField<TaskCompletionSource<PhysicalAddress>?>(engine, "gatewayResolution"));
    }

    [Fact]
    public async Task StaleQueuedShutdown_DoesNotStopAReplacementSession()
    {
        var engine = CreateSyntheticEngine(session: 2);
        SetField(engine, "gatewayMac", GatewayMac);
        var stoppedEvents = 0;
        engine.StateChanged += (_, _) => stoppedEvents++;
        var stopSession = typeof(PcapLanEngine)
            .GetMethod("StopSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<long?, Task>>(engine);

        await stopSession(1);

        Assert.Equal(2, engine.SessionId);
        Assert.True(engine.IsGatewayMac(GatewayMac));
        Assert.Equal(0, stoppedEvents);
        await engine.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterceptionJournalWithoutACaptureHandle_RetainsAnHonestFinalWarning(bool retireBinding)
    {
        var engine = CreateSyntheticEngine();
        SetField(engine, "gatewayMac", GatewayMac);
        var cache = GetField<ClientMappingCache>(engine, "clientMappings");
        cache.BeginAdapter(Adapter);
        cache.SetGatewayMac(GatewayMac);
        var binding = cache.UpsertChecked(ClientIp, ClientMac);
        Assert.True(cache.RememberInterception(ClientIp, ClientMac, binding.BindingVersion));
        if (retireBinding)
        {
            cache.UpsertChecked(IPAddress.Parse("192.168.31.213"), ClientMac);
        }

        PcapEngineStateChangedEventArgs? stopped = null;
        engine.StateChanged += (_, state) => stopped = state;

        await engine.StopAsync();

        Assert.Contains("incomplete or unverified", engine.LastStopStatus);
        Assert.Contains("capture handle unavailable", engine.LastStopStatus);
        Assert.DoesNotContain("frames were sent", engine.LastStopStatus);
        Assert.Equal(retireBinding, engine.LastStopStatus.Contains("Retired IPv4 bindings", StringComparison.Ordinal));
        Assert.NotNull(stopped);
        Assert.Equal(engine.LastStopStatus, stopped.StatusMessage);
        Assert.NotNull(stopped.FailureMessage);
        Assert.Equal(1, engine.GetDiagnosticsSnapshot().RestorationTargets);
        Assert.False(cache.RememberInterception(ClientIp, ClientMac, binding.BindingVersion));
        var finalStatus = engine.LastStopStatus;
        await engine.StopAsync();
        Assert.Equal(finalStatus, engine.LastStopStatus);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task RetiredJournalEntry_IsNeverASourceForRePoisoningOrInterceptionReplies()
    {
        var engine = CreateSyntheticEngine();
        SetField(engine, "gatewayMac", GatewayMac);
        SetField(engine, "controlling", true);
        var cache = GetField<ClientMappingCache>(engine, "clientMappings");
        cache.BeginAdapter(Adapter);
        cache.SetGatewayMac(GatewayMac);
        var original = cache.UpsertChecked(ClientIp, ClientMac);
        Assert.True(cache.RememberInterception(ClientIp, ClientMac, original.BindingVersion));
        var currentIp = IPAddress.Parse("192.168.31.213");
        cache.UpsertChecked(currentIp, ClientMac);
        var poison = typeof(PcapLanEngine).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "PoisonClient" && method.GetParameters().Length == 1)
            .CreateDelegate<Action<KeyValuePair<IPAddress, PhysicalAddress>>>(engine);
        var reply = typeof(PcapLanEngine)
            .GetMethod("SendInterceptionReply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<IPAddress, PhysicalAddress, InterceptionTargets, AdapterProfile, PhysicalAddress>>(engine);

        poison(new KeyValuePair<IPAddress, PhysicalAddress>(ClientIp, ClientMac));
        reply(ClientIp, ClientMac, InterceptionTargets.Client | InterceptionTargets.Gateway, Adapter, GatewayMac);

        Assert.Equal(currentIp, Assert.Single(cache.Mappings).Key);
        Assert.Equal(ClientIp, Assert.Single(cache.GetRestorationTargets()).Address);
        Assert.False(cache.TryGetBinding(ClientIp, ClientMac, out _));
        Assert.Equal(0, engine.GetDiagnosticsSnapshot().ArpInjectionFailures);
        Assert.False(engine.IsRunning);
        await engine.StopAsync();
    }

    [Fact]
    public async Task GatewayPolicy_IsRetainedButCannotCreateAnInterceptableGatewayBinding()
    {
        var policy = new TrafficPolicy();
        var engine = CreateSyntheticEngine(policy: policy);
        SetField(engine, "gatewayMac", GatewayMac);
        SetField(engine, "controlling", true);
        var cache = GetField<ClientMappingCache>(engine, "clientMappings");
        cache.BeginAdapter(Adapter);
        cache.SetGatewayMac(GatewayMac);
        Assert.False(cache.UpsertChecked(ClientIp, GatewayMac).Accepted);
        var rule = new TrafficRule(true, 1, 1);

        await engine.ApplyRuleAsync(GatewayMac.ToString(), rule);

        Assert.Equal(rule.Normalize(), policy.GetRule(GatewayMac.ToString()));
        Assert.True(engine.IsGatewayMac(GatewayMac));
        Assert.Empty(cache.Mappings);
        Assert.Empty(cache.GetRestorationTargets());
        Assert.Equal(0, engine.GetDiagnosticsSnapshot().ArpInjectionFailures);
        await engine.StopAsync();
    }

    private static PcapLanEngine CreateSyntheticEngine(long session = 1, TrafficPolicy? policy = null)
    {
        var engine = new PcapLanEngine(new DeviceRegistry(), policy ?? new TrafficPolicy());
        SetField(engine, "profile", Adapter);
        SetField(engine, "activeSessionId", session);
        return engine;
    }

    private static T GetField<T>(PcapLanEngine engine, string name) =>
        (T)typeof(PcapLanEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine)!;

    private static void SetField<T>(PcapLanEngine engine, string name, T value) =>
        typeof(PcapLanEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(engine, value);
}
