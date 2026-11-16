using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Networking;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Lantern.App.Services;

public sealed class PcapLanEngine : IAsyncDisposable
{
    public static TimeSpan ProbeInterval { get; } = TimeSpan.FromMilliseconds(10);

    public static TimeSpan ProbeReplyWindow { get; } = TimeSpan.FromMilliseconds(800);

    public const string ForwardingFilter = "ip or ip6";

    private readonly object arpSendSync = new();
    private readonly object identitySync = new();
    private readonly SemaphoreSlim refreshSync = new(1, 1);
    private readonly SemaphoreSlim stopSync = new(1, 1);
    private readonly TrafficPolicy policy;
    private readonly DeviceRegistry registry;
    private readonly PassiveDiscoveryProfile discoveryProfile =
        PassiveDiscoveryProfile.Default;
    private readonly ClientMappingCache clientMappings = new();
    private readonly ResolvedDeviceNameClaims resolvedNameClaims = new(Dns.GetHostName());
    private readonly NameResolutionRetryGate nameRetryGate = new();
    private readonly ConditionalWeakTable<DeviceIdentityLearnedEventArgs, IdentityBinding> issuedIdentities = new();
    private readonly Dictionary<string, DeviceIdentityLearnedEventArgs> latestIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private CaptureDiagnostics diagnostics = new();
    private LocalIpv4TrafficMeter? localMeter;
    private long sessionSequence;
    private long activeSessionId;
    private CancellationTokenSource? engineCancellation;
    private CancellationTokenRegistration cancellationRegistration;
    private Task? backgroundTask;
    private LibPcapLiveDevice? arpDevice;
    private LibPcapLiveDevice? forwardingDevice;
    private AdapterProfile? profile;
    private PhysicalAddress? gatewayMac;
    private FrameRouter? frameRouter;
    private TaskCompletionSource<PhysicalAddress>? gatewayResolution;
    private ConcurrentDictionary<IPAddress, PhysicalAddress>? activeProbeReplies;
    private ConcurrentDictionary<string, IPAddress>? activeKnownDeviceReplies;
    private KnownDeviceHint[] knownDeviceHints = [];
    private string[] rejectedResolvedNames = [];
    private string? backgroundFailure;
    private volatile bool controlling;
    private volatile bool restoring;
    private long forwardedPacketCount;
    private long droppedPacketCount;

    public PcapLanEngine(DeviceRegistry registry, TrafficPolicy policy)
    {
        this.registry = registry;
        this.policy = policy;
    }

    public bool IsRunning => arpDevice is not null || forwardingDevice is not null;

    public bool IsControlling => controlling;

    public bool IsGatewayMac(PhysicalAddress macAddress) =>
        gatewayMac is { } current && current.Equals(macAddress);

    public long ForwardedPacketCount => Interlocked.Read(ref forwardedPacketCount);

    public long DroppedPacketCount => Interlocked.Read(ref droppedPacketCount);

    public string DriverName { get; private set; } = "Not started";

    public string StopStatus { get; private set; } = "Not started; no network restoration was attempted.";

    public string LastStopStatus => StopStatus;

    public long SessionId => Interlocked.Read(ref activeSessionId);

    public CaptureDiagnosticsSnapshot GetDiagnosticsSnapshot() => diagnostics.Snapshot() with
    {
        RestorationTargets = clientMappings.RestorationTargetCount,
    };

    public string GetDiagnosticsSummary() => CaptureDiagnosticsFormatter.Format(GetDiagnosticsSnapshot());

    public DeviceCaptureCoverage GetCoverage(PhysicalAddress macAddress) => diagnostics.GetCoverage(macAddress);

    public string GetCoverageStatus(string macAddress) =>
        PhysicalAddress.TryParse(macAddress, out var mac)
            ? diagnostics.GetCoverage(mac).Status
            : "No valid MAC address is available for capture coverage.";

    public bool IsIdentityCurrent(DeviceIdentityLearnedEventArgs identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (identitySync)
        {
            return controlling && !restoring && identity.SessionId != 0 &&
                   identity.SessionId == SessionId && identity.Address is { } address &&
                   latestIdentities.TryGetValue(identity.MacAddress.ToString(), out var latest) &&
                   ReferenceEquals(latest, identity) &&
                   issuedIdentities.TryGetValue(identity, out var binding) &&
                   clientMappings.IsCurrentBinding(
                       address, identity.MacAddress, binding.Version, requireObserved: true);
        }
    }

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<PcapEngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<DeviceIdentityLearnedEventArgs>? DeviceIdentityLearned;

    public event EventHandler<DeviceDomainObservedEventArgs>? DeviceDomainObserved;

    public void ReplaceKnownDeviceHints(IEnumerable<KnownDeviceHint> hints)
    {
        ArgumentNullException.ThrowIfNull(hints);
        knownDeviceHints = hints
            .Where(hint => !hint.MacAddress.Equals(PhysicalAddress.None))
            .DistinctBy(hint => TrafficPolicy.NormalizeMac(hint.MacAddress.ToString()))
            .ToArray();
    }

    public void ReplaceRejectedResolvedNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        rejectedResolvedNames = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        resolvedNameClaims.Reset(rejectedResolvedNames);
    }

    public async Task StartAsync(AdapterProfile adapter, CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        await NpfDriverService.EnsureAvailableAsync(cancellationToken);
        var startedSession = 0L;
        var clients = clientMappings.Mappings;
        var pendingGateway = new TaskCompletionSource<PhysicalAddress>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await stopSync.WaitAsync(cancellationToken);
            try
            {
                if (IsRunning || profile is not null)
                {
                    return;
                }

                lock (identitySync)
                {
                    profile = adapter;
                    gatewayMac = null;
                    clientMappings.BeginAdapter(adapter);
                    resolvedNameClaims.Reset(rejectedResolvedNames);
                    activeSessionId = ++sessionSequence;
                    startedSession = activeSessionId;
                    latestIdentities.Clear();
                    nameRetryGate.BeginSession(activeSessionId);
                    restoring = false;
                    controlling = false;
                    gatewayResolution = pendingGateway;
                }

                registry.BeginSession();
                diagnostics = new CaptureDiagnostics();
                StopStatus = "Running; peer restoration has not been attempted.";
                backgroundFailure = null;
                Interlocked.Exchange(ref forwardedPacketCount, 0);
                Interlocked.Exchange(ref droppedPacketCount, 0);
                var captureDevice = LibPcapLiveDeviceList.Instance
                    .FirstOrDefault(candidate => candidate.MacAddress?.Equals(adapter.LocalMac) == true)
                    ?? throw new InvalidOperationException(
                        $"WinPcap could not match the Windows adapter “{adapter.Name}”.");
                arpDevice = captureDevice;
                arpDevice.OnPacketArrival += OnArpPacketArrival;
                arpDevice.Open(PcapCaptureConfiguration.CreateForArpDiscovery());
                arpDevice.Filter = "arp";
                arpDevice.StartCapture();

                forwardingDevice = new LibPcapLiveDevice(
                    captureDevice.Interface ??
                    throw new InvalidOperationException("The capture adapter has no interface."));
                forwardingDevice.Open(PcapCaptureConfiguration.CreateForForwarding());
                forwardingDevice.Filter = ForwardingFilter;
                DriverName = captureDevice.Description ?? captureDevice.Name;
                RaiseStatus($"Resolving gateway {adapter.GatewayAddress}…");

                SendArpPacket(
                    EthernetFrameCodec.BuildArpRequest(
                        adapter.LocalMac,
                        adapter.LocalAddress,
                        adapter.GatewayAddress));
            }
            finally
            {
                stopSync.Release();
            }

            var resolvedGatewayMac = await pendingGateway.Task.WaitAsync(
                TimeSpan.FromSeconds(4),
                cancellationToken);
            CancellationToken engineToken;
            Task forwardingTask;
            var forwarderStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (identitySync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (restoring || SessionId != startedSession || !ReferenceEquals(profile, adapter))
                {
                    throw new OperationCanceledException("Monitoring startup was stopped.");
                }

                gatewayMac = resolvedGatewayMac;
                clientMappings.SetGatewayMac(resolvedGatewayMac);
                registry.Observe(
                    adapter.GatewayAddress,
                    resolvedGatewayMac,
                    DateTimeOffset.UtcNow,
                    "Gateway");
                registry.Observe(
                    adapter.LocalAddress,
                    adapter.LocalMac,
                    DateTimeOffset.UtcNow,
                    Dns.GetHostName());
                localMeter = new LocalIpv4TrafficMeter(adapter, resolvedGatewayMac);

                frameRouter = new FrameRouter(
                    adapter.LocalMac,
                    adapter.LocalAddress,
                    resolvedGatewayMac,
                    clients,
                    policy,
                    adapter: adapter);

                RaiseStatus("Loading devices already observed by Windows…");
                engineCancellation = new CancellationTokenSource();
                engineToken = engineCancellation.Token;
                controlling = true;
                forwardingTask = Task.Run(
                    () => RunForwardingLoop(engineToken, forwarderStarted));
                backgroundTask = forwardingTask;
                // Cancellation requests the restore-first shutdown rather than
                // cancelling forwarding while peers still point at this computer.
                cancellationRegistration = cancellationToken.Register(() =>
                {
                    _ = Task.Run(() => StopSessionAsync(startedSession));
                });
            }
            await forwarderStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                cancellationToken);

            await RefreshNeighborsAsync(engineToken);
            cancellationToken.ThrowIfCancellationRequested();
            engineToken.ThrowIfCancellationRequested();
            if (restoring || SessionId != startedSession)
            {
                throw new OperationCanceledException("Monitoring startup was stopped.");
            }

            await Task.Run(PoisonClients, engineToken);
            lock (identitySync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                engineToken.ThrowIfCancellationRequested();
                if (restoring || SessionId != startedSession)
                {
                    throw new OperationCanceledException("Monitoring startup was stopped.");
                }

                backgroundTask = Task.WhenAll(
                    forwardingTask,
                    Task.Run(() => RunMaintenanceAsync(engineToken)));
                const string activeMessage =
                    "IPv4 payload monitoring active — observed traffic only, not complete router usage; IPv6 is diagnostics-only.";
                RaiseStatus(activeMessage);
                RaiseStateChanged(true, activeMessage);
            }
        }
        catch (Exception exception)
        {
            string? forwardingFailure = null;
            lock (identitySync)
            {
                if (startedSession != 0 && SessionId == startedSession)
                {
                    forwardingFailure = backgroundFailure;
                    if (exception is not OperationCanceledException ||
                        (!cancellationToken.IsCancellationRequested && !restoring))
                    {
                        Interlocked.CompareExchange(
                            ref backgroundFailure,
                            $"Startup failed: {exception.Message}",
                            null);
                    }
                }
            }

            if (startedSession != 0)
            {
                await StopSessionAsync(startedSession);
            }

            if (forwardingFailure is not null)
            {
                throw new InvalidOperationException(forwardingFailure, exception);
            }

            throw;
        }
    }

    public async Task<DiscoveryRefreshResult> RefreshNeighborsAsync(
        CancellationToken cancellationToken = default)
    {
        await refreshSync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => RefreshNeighborsCoreAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshSync.Release();
        }
    }

    private async Task<DiscoveryRefreshResult> RefreshNeighborsCoreAsync(
        CancellationToken cancellationToken)
    {
        var clients = clientMappings.Mappings;
        var expectedSession = SessionId;
        var activeProfile = profile ??
            throw new InvalidOperationException("Select and start an adapter before refreshing.");
        var probeReplies = new ConcurrentDictionary<IPAddress, PhysicalAddress>();
        var knownReplies = new ConcurrentDictionary<string, IPAddress>(
            StringComparer.OrdinalIgnoreCase);
        activeProbeReplies = probeReplies;
        activeKnownDeviceReplies = knownReplies;
        int probesSent;
        try
        {
            probesSent = await ProbeSubnetAsync(activeProfile, cancellationToken);
        }
        finally
        {
            activeProbeReplies = null;
            activeKnownDeviceReplies = null;
        }

        if (expectedSession == 0 || expectedSession != SessionId || restoring)
        {
            return new DiscoveryRefreshResult(probesSent, probeReplies.Count, clients.Count);
        }

        await RefreshWindowsNeighborCacheAsync(
            activeProfile,
            clients,
            cancellationToken);

        return new DiscoveryRefreshResult(
            probesSent,
            probeReplies.Count,
            clients.Count);
    }

    private async Task RefreshWindowsNeighborCacheAsync(
        AdapterProfile activeProfile,
        ConcurrentDictionary<IPAddress, PhysicalAddress> clients,
        CancellationToken cancellationToken)
    {
        var expectedSession = SessionId;
        var neighbors = await WindowsNeighborCache.ReadAsync(
            activeProfile,
            cancellationToken).ConfigureAwait(false);
        if (expectedSession == 0 || expectedSession != SessionId || restoring ||
            !ReferenceEquals(profile, activeProfile))
        {
            return;
        }

        var requestedRealMappings = false;
        foreach (var neighbor in neighbors)
        {
            if (restoring || expectedSession != SessionId)
            {
                return;
            }

            if (neighbor.Address.Equals(activeProfile.GatewayAddress))
            {
                registry.Remember(
                    neighbor.Address,
                    gatewayMac ?? neighbor.MacAddress,
                    DateTimeOffset.UtcNow,
                    "Gateway");
                continue;
            }

            if (!TrafficMetering.IsClientAddress(activeProfile, neighbor.Address))
            {
                diagnostics.RecordMappingUpdate(new ClientMappingUpdate(
                    false, false, false, 0, ClientMappingRejection.ProtectedOrOutOfSubnet));
                continue;
            }

            if (neighbor.MacAddress.Equals(activeProfile.LocalMac))
            {
                _ = await WindowsNeighborCache.DeleteAsync(
                    activeProfile,
                    neighbor.Address,
                    cancellationToken);
                if (restoring || expectedSession != SessionId)
                {
                    return;
                }

                SendArpPacket(
                    EthernetFrameCodec.BuildArpRequest(
                        activeProfile.LocalMac,
                        activeProfile.LocalAddress,
                        neighbor.Address));
                requestedRealMappings = true;
                continue;
            }

            var update = clientMappings.UpsertChecked(neighbor.Address, neighbor.MacAddress, observed: false);
            diagnostics.RecordMappingUpdate(update);
            if (!update.Accepted)
            {
                if (!clients.ContainsKey(neighbor.Address))
                {
                    frameRouter?.RemoveClient(neighbor.Address);
                }

                continue;
            }

            if (!clientMappings.WithCurrentBinding(neighbor.Address, neighbor.MacAddress, update.BindingVersion, () =>
                {
                    frameRouter?.UpdateClient(neighbor.Address, neighbor.MacAddress);
                    registry.Remember(
                        neighbor.Address,
                        neighbor.MacAddress,
                        DateTimeOffset.UtcNow,
                        GetKnownHostName(neighbor.MacAddress));
                }))
            {
                continue;
            }

            _ = ResolveNameAsync(neighbor.Address, neighbor.MacAddress);
            if (controlling && update.Changed)
            {
                PoisonClient(
                    new KeyValuePair<IPAddress, PhysicalAddress>(
                        neighbor.Address,
                        neighbor.MacAddress));
            }
        }

        if (requestedRealMappings)
        {
            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task<int> ProbeSubnetAsync(
        AdapterProfile activeProfile,
        CancellationToken cancellationToken)
    {
        var probesSent = 0;
        var expectedSession = SessionId;
        var hints = knownDeviceHints
            .Where(hint => TrafficMetering.IsUnicastMac(hint.MacAddress) &&
                           !hint.MacAddress.Equals(activeProfile.LocalMac) &&
                           !hint.MacAddress.Equals(gatewayMac))
            .ToArray();
        var addressedHints = hints.Where(hint => hint.LastKnownIp is { } address &&
            TrafficMetering.CanInterceptClient(activeProfile, address, hint.MacAddress, gatewayMac)).ToArray();

        // Try remembered addresses first. This usually rediscovers a client in
        // milliseconds without scanning the rest of the subnet.
        foreach (var hint in addressedHints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (restoring || expectedSession == 0 || expectedSession != SessionId)
            {
                return probesSent;
            }

            SendArpPacket(
                EthernetFrameCodec.BuildUnicastArpRequest(
                    activeProfile.LocalMac,
                    activeProfile.LocalAddress,
                    hint.MacAddress,
                    hint.LastKnownIp!));
            probesSent++;
        }

        if (addressedHints.Length > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        }

        foreach (var address in IPv4DiscoveryRange.EnumerateHosts(
                     activeProfile.LocalAddress,
                     activeProfile.PrefixLength))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (restoring || expectedSession == 0 || expectedSession != SessionId)
            {
                return probesSent;
            }

            if (TrafficMetering.IsClientAddress(activeProfile, address))
            {
                // Keep ordinary broadcast discovery for wired clients.
                SendArpPacket(
                    EthernetFrameCodec.BuildArpRequest(
                        activeProfile.LocalMac,
                        activeProfile.LocalAddress,
                        address));
                probesSent++;

                // Some inexpensive access points suppress broadcast ARP from
                // Ethernet to Wi-Fi. Addressing the request directly to a
                // remembered client MAC crosses those bridges without changing
                // the router or client ARP tables.
                foreach (var hint in hints)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (restoring || expectedSession != SessionId)
                    {
                        return probesSent;
                    }

                    var key = TrafficPolicy.NormalizeMac(hint.MacAddress.ToString());
                    if (activeKnownDeviceReplies?.ContainsKey(key) == true ||
                        Equals(hint.LastKnownIp, address))
                    {
                        continue;
                    }

                    SendArpPacket(
                        EthernetFrameCodec.BuildUnicastArpRequest(
                            activeProfile.LocalMac,
                            activeProfile.LocalAddress,
                            hint.MacAddress,
                            address));
                    probesSent++;
                }
            }

            // Pace broadcasts so discovery remains invisible to games and calls.
            await Task.Delay(ProbeInterval, cancellationToken);
        }

        // Give replies time to reach the ARP capture callback before the snapshot.
        await Task.Delay(ProbeReplyWindow, cancellationToken);
        return probesSent;
    }

    public Task StopAsync() => StopSessionAsync();

    private async Task StopSessionAsync(long? expectedSession = null)
    {
        await stopSync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (expectedSession is null || SessionId == expectedSession)
            {
                await Task.Run(StopCoreAsync).ConfigureAwait(false);
            }
        }
        finally
        {
            stopSync.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (!IsRunning && profile is null && SessionId == 0)
        {
            return;
        }

        restoring = true;
        lock (identitySync)
        {
            activeSessionId = 0;
            latestIdentities.Clear();
            nameRetryGate.EndSession();
            clientMappings.FreezeBindings();
            gatewayResolution?.TrySetCanceled();
            gatewayResolution = null;
        }

        var restorationTargets = clientMappings.GetRestorationTargets();
        var hadRestorationTargets = restorationTargets.Count > 0;
        var warnings = new List<string>();
        if (hadRestorationTargets && (profile is null || gatewayMac is null || arpDevice is null))
        {
            warnings.Add("Corrective IPv4 ARP could not be sent: adapter, gateway, or capture handle unavailable");
        }

        if (restorationTargets.Any(target => !target.RestoreGatewayMapping))
        {
            warnings.Add("Retired IPv4 bindings receive client-side repair/recovery only; old gateway ownership is unverified");
        }
        try
        {
            await ForwardingShutdown.RunAsync(
                () => arpDevice is null ? Task.CompletedTask : RestoreArpAsync(),
                () =>
                {
                    controlling = false;
                    engineCancellation?.Cancel();
                },
                AwaitBackgroundTaskAsync).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            warnings.Add($"ARP restoration could not be confirmed: {exception.Message}");
            RaiseStatus($"Network restoration reported an error; closing capture handles: {exception.Message}");
        }

        UpdateCaptureStatistics();
        if (arpDevice is { } activeArpDevice)
        {
            try
            {
                activeArpDevice.StopCapture();
            }
            catch (Exception exception)
            {
                warnings.Add($"ARP capture stop reported: {exception.Message}");
            }
            finally
            {
                activeArpDevice.OnPacketArrival -= OnArpPacketArrival;
                try
                {
                    activeArpDevice.Close();
                }
                catch (Exception exception)
                {
                    warnings.Add($"ARP handle close reported: {exception.Message}");
                }

                arpDevice = null;
            }
        }

        if (forwardingDevice is { } activeForwardingDevice)
        {
            try
            {
                activeForwardingDevice.Close();
            }
            catch (Exception exception)
            {
                warnings.Add($"Data handle close reported: {exception.Message}");
            }

            forwardingDevice = null;
        }

        cancellationRegistration.Dispose();
        engineCancellation?.Dispose();
        engineCancellation = null;
        backgroundTask = null;
        frameRouter = null;
        localMeter = null;
        gatewayMac = null;
        profile = null;
        restoring = false;
        var failure = backgroundFailure;
        StopStatus = EngineStopStatus.Format(hadRestorationTargets, warnings, failure);
        RaiseStatus(StopStatus);
        RaiseStateChanged(false, StopStatus, failure ?? warnings.FirstOrDefault());
    }

    public async Task ApplyRuleAsync(string macAddress, TrafficRule rule)
    {
        var clients = clientMappings.Mappings;
        var previousTargets = policy.GetInterceptionTargets(macAddress);
        policy.SetRule(macAddress, rule.Normalize());
        var currentTargets = policy.GetInterceptionTargets(macAddress);
        if (controlling)
        {
            var normalizedMac = TrafficPolicy.NormalizeMac(macAddress);
            foreach (var client in clients)
            {
                if (string.Equals(
                        TrafficPolicy.NormalizeMac(client.Value.ToString()),
                        normalizedMac,
                        StringComparison.Ordinal))
                {
                    await Task.Run(() => ApplyInterceptionTransitionAsync(
                        client,
                        InterceptionTransition.Between(previousTargets, currentTargets))).ConfigureAwait(false);
                    break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }

    private void OnArpPacketArrival(object sender, PacketCapture capture)
    {
        diagnostics.RecordArpFrame();
        try
        {
            if (EthernetFrameCodec.TryParseArp(capture.Data, out var arp))
            {
                ObserveArp(arp);
            }
        }
        catch (Exception exception)
        {
            HandleBackgroundFailure("ARP processing failed", exception);
        }
    }

    private void RunForwardingLoop(
        CancellationToken cancellationToken,
        TaskCompletionSource forwarderStarted)
    {
        try
        {
            var activeDevice = forwardingDevice ??
                throw new InvalidOperationException("The forwarding adapter is not open.");
            var startupReported = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = activeDevice.GetNextPacket(out var capture);
                if (status == GetPacketStatus.Error)
                {
                    diagnostics.RecordCaptureReadFailure();
                }

                if (!startupReported)
                {
                    ForwarderStartupPolicy.ObserveFirstRead(
                        forwarderStarted,
                        status,
                        activeDevice.LastError);
                    startupReported = true;
                }

                if (status == GetPacketStatus.ReadTimeout)
                {
                    continue;
                }

                if (status == GetPacketStatus.Error)
                {
                    throw new InvalidOperationException(
                        $"Packet forwarding stopped: {activeDevice.LastError}");
                }

                if (status != GetPacketStatus.PacketRead)
                {
                    continue;
                }

                ProcessForwardingFrame(capture.Data);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            forwarderStarted.TrySetException(exception);
            HandleBackgroundFailure("Forwarding failed", exception);
        }
    }

    private void ProcessForwardingFrame(ReadOnlySpan<byte> bytes)
    {
        diagnostics.RecordCapturedFrame(bytes);
        var router = frameRouter;
        var activeProfile = profile;
        if (!controlling || router is null || activeProfile is null)
        {
            return;
        }

        ObserveDhcpIdentity(bytes);
        if (localMeter is { } meter && meter.TryMeasure(bytes, out var localTraffic))
        {
            registry.RecordTraffic(activeProfile.LocalMac, localTraffic.Direction, localTraffic.PayloadByteCount);
            diagnostics.RecordLocalTraffic(activeProfile.LocalMac, localTraffic);
        }

        var result = router.Route(bytes);
        diagnostics.RecordRoute(result);
        if (result.Direction is not null && result.ClientMac is not null)
        {
            registry.RecordTraffic(
                result.ClientMac,
                result.Direction.Value,
                result.MeteredByteCount);
        }

        if (result.Direction == TrafficDirection.Upload &&
            result.ClientMac is not null &&
            result.Observation is { } observation)
        {
            DeviceDomainObserved?.Invoke(
                this,
                new DeviceDomainObservedEventArgs(
                    result.ClientMac,
                    observation,
                    DateTimeOffset.UtcNow,
                    result.BlockedByDomain));
        }

        if (result.Action == FrameAction.Forward && result.Frame is not null)
        {
            SendForwardingPacket(result.Frame);
            Interlocked.Increment(ref forwardedPacketCount);
            diagnostics.RecordForwardedPacket();
        }
        else if (result.Action == FrameAction.Drop)
        {
            Interlocked.Increment(ref droppedPacketCount);
        }
    }

    private void ObserveArp(ArpFrameInfo arp)
    {
        var clients = clientMappings.Mappings;
        var activeProfile = profile;
        if (activeProfile is null || restoring || SessionId == 0 ||
            !arp.HasConsistentSender ||
            arp.SenderIp.Equals(IPAddress.Any) ||
            arp.SenderMac.Equals(activeProfile.LocalMac) ||
            !TrafficMetering.IsUnicastMac(arp.SenderMac))
        {
            return;
        }

        if (arp.SenderIp.Equals(activeProfile.GatewayAddress))
        {
            if (gatewayMac is not null && !gatewayMac.Equals(arp.SenderMac))
            {
                diagnostics.RecordMappingUpdate(new ClientMappingUpdate(
                    false, false, false, 0, ClientMappingRejection.GatewayOrProxyMac));
                return;
            }

            gatewayResolution?.TrySetResult(arp.SenderMac);
            registry.Observe(arp.SenderIp, arp.SenderMac, DateTimeOffset.UtcNow, "Gateway");
            RespondToArpRequest(arp);
            return;
        }

        var update = clientMappings.UpsertChecked(arp.SenderIp, arp.SenderMac);
        diagnostics.RecordMappingUpdate(update);
        if (!update.Accepted)
        {
            if (!clients.ContainsKey(arp.SenderIp))
            {
                frameRouter?.RemoveClient(arp.SenderIp);
            }

            return;
        }

        if (!clientMappings.WithCurrentBinding(arp.SenderIp, arp.SenderMac, update.BindingVersion, () =>
            {
                activeProbeReplies?.TryAdd(arp.SenderIp, arp.SenderMac);
                activeKnownDeviceReplies?.TryAdd(
                    TrafficPolicy.NormalizeMac(arp.SenderMac.ToString()),
                    arp.SenderIp);
                frameRouter?.UpdateClient(arp.SenderIp, arp.SenderMac);
                registry.Observe(
                    arp.SenderIp,
                    arp.SenderMac,
                    DateTimeOffset.UtcNow,
                    GetKnownHostName(arp.SenderMac));
            }))
        {
            return;
        }

        _ = ResolveNameAsync(arp.SenderIp, arp.SenderMac);
        RespondToArpRequest(arp);
        if (controlling && update.Changed)
        {
            PoisonClient(
                new KeyValuePair<IPAddress, PhysicalAddress>(
                    arp.SenderIp,
                    arp.SenderMac));
        }
    }

    private async Task ResolveNameAsync(IPAddress address, PhysicalAddress mac)
    {
        var session = SessionId;
        if (!controlling || restoring ||
            !clientMappings.TryGetBinding(address, mac, out var version, requireObserved: true) ||
            !nameRetryGate.TryBegin(session, address, mac, version, out var attempt))
        {
            return;
        }

        var resolved = false;
        try
        {
            var netBiosTask = NetBiosNameResolver.ResolveAsync(address);
            var mdnsTask = MdnsNameResolver.ResolveAsync(address);
            var dnsTask = ResolveDnsNameAsync(address);
            await Task.WhenAll(netBiosTask, mdnsTask, dnsTask).ConfigureAwait(false);
            foreach (var name in new[] { netBiosTask.Result, mdnsTask.Result, dnsTask.Result })
            {
                if (TryApplyIdentity(session, address, mac, version, name, attempt))
                {
                    resolved = true;
                    return;
                }
            }
        }
        catch (Exception)
        {
            // A resolver failure must release the in-flight gate and remain retryable.
        }
        finally
        {
            nameRetryGate.Complete(attempt, resolved);
        }
    }

    private void ObserveDhcpIdentity(ReadOnlySpan<byte> bytes)
    {
        if (DhcpHostNameParser.TryParse(bytes, out var dhcp) &&
            EthernetFrameCodec.TryParseIpv4(bytes, out var ipv4) &&
            ipv4.SourceMac.Equals(dhcp.MacAddress) &&
            clientMappings.TryGetBinding(
                ipv4.Source, dhcp.MacAddress, out var version, requireObserved: true))
        {
            TryApplyIdentity(SessionId, ipv4.Source, dhcp.MacAddress, version, dhcp.HostName);
        }
    }

    private bool TryApplyIdentity(
        long session,
        IPAddress address,
        PhysicalAddress mac,
        long version,
        string? name,
        NameResolutionAttempt? attempt = null)
    {
        DeviceIdentityLearnedEventArgs? identity = null;
        var accepted = false;
        lock (identitySync)
        {
            if (!controlling || restoring || session == 0 || session != SessionId ||
                (attempt is { } activeAttempt && !nameRetryGate.IsCurrentAttempt(activeAttempt)))
            {
                return false;
            }

            clientMappings.WithCurrentBinding(address, mac, version, () =>
            {
                if (!resolvedNameClaims.TryClaim(mac, name, out var acceptedName, out var isNewClaim))
                {
                    return;
                }

                accepted = true;
                registry.SetHostName(mac, acceptedName);
                if (attempt is null)
                {
                    nameRetryGate.MarkResolved(session, address, mac, version);
                }

                var macKey = mac.ToString();
                if (isNewClaim || !latestIdentities.TryGetValue(macKey, out var previousIdentity) ||
                    previousIdentity.SessionId != session || !Equals(previousIdentity.Address, address) ||
                    !issuedIdentities.TryGetValue(previousIdentity, out var previousBinding) ||
                    previousBinding.Version != version)
                {
                    identity = new DeviceIdentityLearnedEventArgs(mac, acceptedName, session, address);
                    issuedIdentities.Add(identity, new IdentityBinding(version));
                    latestIdentities[macKey] = identity;
                }
            }, requireObserved: true);
        }

        if (identity is not null)
        {
            DeviceIdentityLearned?.Invoke(this, identity);
        }

        return accepted;
    }

    private static async Task<string?> ResolveDnsNameAsync(IPAddress address)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address)
                .WaitAsync(TimeSpan.FromSeconds(2));
            var name = entry.HostName.Split('.')[0].Trim();
            return name.Length == 0 || IPAddress.TryParse(name, out _) ? null : name;
        }
        catch (Exception exception) when (
            exception is System.Net.Sockets.SocketException or TimeoutException)
        {
            return null;
        }
    }

    private string? GetKnownHostName(PhysicalAddress macAddress)
    {
        var key = TrafficPolicy.NormalizeMac(macAddress.ToString());
        return knownDeviceHints.FirstOrDefault(
            hint => string.Equals(
                TrafficPolicy.NormalizeMac(hint.MacAddress.ToString()),
                key,
                StringComparison.OrdinalIgnoreCase))?.HostName;
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            RunMaintenanceWorkerAsync(() => RunPoisonLoopAsync(cancellationToken), cancellationToken),
            RunMaintenanceWorkerAsync(() => RunNeighborRefreshLoopAsync(cancellationToken), cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task RunMaintenanceWorkerAsync(Func<Task> worker, CancellationToken cancellationToken)
    {
        try
        {
            await worker().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HandleBackgroundFailure("Maintenance failed", exception);
        }
    }

    private async Task RunPoisonLoopAsync(CancellationToken cancellationToken)
    {
        var poisonTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await poisonTimer.WaitForNextTickAsync(cancellationToken))
            {
                PoisonClients();
            }
        }
        finally
        {
            poisonTimer.Dispose();
        }
    }

    private async Task RunNeighborRefreshLoopAsync(CancellationToken cancellationToken)
    {
        var refreshTimer = new PeriodicTimer(discoveryProfile.RefreshInterval);
        try
        {
            while (await refreshTimer.WaitForNextTickAsync(cancellationToken))
            {
                UpdateCaptureStatistics();
                if (discoveryProfile.ProbeSubnetOnRefresh)
                {
                    await RefreshNeighborsAsync(cancellationToken);
                }
                else
                {
                    await RefreshCachedNeighborsAsync(cancellationToken);
                }
            }
        }
        finally
        {
            refreshTimer.Dispose();
        }
    }

    private async Task RefreshCachedNeighborsAsync(CancellationToken cancellationToken)
    {
        await refreshSync.WaitAsync(cancellationToken);
        try
        {
            var activeProfile = profile;
            if (activeProfile is null)
            {
                return;
            }

            await RefreshWindowsNeighborCacheAsync(
                activeProfile,
                clientMappings.Mappings,
                cancellationToken);
            ProbeKnownClients(activeProfile);
        }
        finally
        {
            refreshSync.Release();
        }
    }

    private void ProbeKnownClients(AdapterProfile activeProfile)
    {
        foreach (var client in clientMappings.Mappings)
        {
            if (!controlling || restoring ||
                !TrafficMetering.CanInterceptClient(activeProfile, client.Key, client.Value, gatewayMac))
            {
                continue;
            }

            SendArpPacket(
                EthernetFrameCodec.BuildUnicastArpRequest(
                    activeProfile.LocalMac,
                    activeProfile.LocalAddress,
                    client.Value,
                    client.Key));
        }
    }

    private void PoisonClients()
    {
        var clients = clientMappings.Mappings;
        var activeProfile = profile;
        var activeGatewayMac = gatewayMac;
        if (!controlling || restoring || activeProfile is null || activeGatewayMac is null)
        {
            return;
        }

        foreach (var pair in clients)
        {
            PoisonClient(pair);
        }
    }

    private async Task RestoreArpAsync()
    {
        var clients = clientMappings.GetRestorationTargets();
        var activeProfile = profile;
        var activeGatewayMac = gatewayMac;
        if (activeProfile is null || activeGatewayMac is null || clients.Count == 0)
        {
            return;
        }

        var failures = 0;
        string? firstFailure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var pair in clients)
            {
                try
                {
                    SendRestoreFrames(pair, activeProfile, activeGatewayMac);
                }
                catch (Exception exception)
                {
                    failures++;
                    firstFailure ??= exception.Message;
                }
            }

            await Task.Delay(120).ConfigureAwait(false);
        }

        if (failures > 0)
        {
            throw new InvalidOperationException($"{failures} corrective ARP attempts failed: {firstFailure}");
        }
    }

    private bool RememberInterception(IPAddress address, PhysicalAddress mac, long version)
    {
        if (clientMappings.RememberInterception(address, mac, version))
        {
            return true;
        }

        if (controlling && !restoring && clientMappings.IsCurrentBinding(address, mac, version))
        {
            diagnostics.RecordMappingUpdate(new ClientMappingUpdate(
                false, false, false, 0, ClientMappingRejection.RestorationCapacityReached));
        }

        return false;
    }

    private void PoisonClient(KeyValuePair<IPAddress, PhysicalAddress> client)
    {
        PoisonClient(client, policy.GetInterceptionTargets(client.Value.ToString()));
    }

    private void PoisonClient(
        KeyValuePair<IPAddress, PhysicalAddress> client,
        InterceptionTargets targets)
    {
        var activeProfile = profile;
        var activeGatewayMac = gatewayMac;
        if (!controlling || restoring || targets == InterceptionTargets.None ||
            activeProfile is null || activeGatewayMac is null ||
            !TrafficMetering.CanInterceptClient(activeProfile, client.Key, client.Value, activeGatewayMac) ||
            !clientMappings.TryGetBinding(client.Key, client.Value, out var version))
        {
            return;
        }

        clientMappings.WithCurrentBinding(client.Key, client.Value, version, () =>
        {
            if (!controlling || restoring || !RememberInterception(client.Key, client.Value, version))
            {
                return;
            }

            var frames = ArpInterceptionFrames.BuildPoison(
                activeProfile.LocalMac,
                activeProfile.GatewayAddress,
                activeGatewayMac,
                client.Key,
                client.Value);
            foreach (var frame in frames.Select(targets))
            {
                SendArpPacket(frame);
            }

            var controllerFrames = ArpInterceptionFrames.BuildControllerProtection(
                activeProfile.LocalMac,
                activeProfile.LocalAddress,
                activeGatewayMac,
                activeProfile.GatewayAddress,
                client.Value,
                client.Key);
            SendArpPacket(controllerFrames.ClientToController);
            SendArpPacket(controllerFrames.GatewayToController);
        });
    }

    private async Task ApplyInterceptionTransitionAsync(
        KeyValuePair<IPAddress, PhysicalAddress> client,
        InterceptionTransition transition)
    {
        var activeProfile = profile;
        var activeGatewayMac = gatewayMac;
        if (!controlling || restoring || activeProfile is null || activeGatewayMac is null)
        {
            return;
        }

        if (transition.Restore == InterceptionTargets.None)
        {
            PoisonClient(client);
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!controlling || restoring || arpDevice is null)
            {
                return;
            }

            SendRestoreFrames(client, activeProfile, activeGatewayMac);
            PoisonClient(client);
            if (attempt < 2)
            {
                await Task.Delay(40);
            }
        }
    }

    private void SendRestoreFrames(
        KeyValuePair<IPAddress, PhysicalAddress> client,
        AdapterProfile activeProfile,
        PhysicalAddress activeGatewayMac)
    {
        if (clientMappings.TryGetBinding(client.Key, client.Value, out var version))
        {
            clientMappings.WithCurrentBinding(client.Key, client.Value, version, () =>
                SendRestoreFrames(new ClientRestorationTarget(client.Key, client.Value, true),
                    activeProfile, activeGatewayMac));
        }
    }

    private void SendRestoreFrames(
        ClientRestorationTarget target,
        AdapterProfile activeProfile,
        PhysicalAddress activeGatewayMac)
    {
        var failures = 0;
        Exception? firstFailure = null;
        foreach (var frame in ClientRestorationFrames.Build(activeProfile, activeGatewayMac, target))
        {
            try
            {
                SendArpPacket(frame);
            }
            catch (Exception exception)
            {
                failures++;
                firstFailure ??= exception;
            }
        }

        if (failures > 0)
        {
            throw new InvalidOperationException(
                $"{failures} corrective ARP frame sends failed: {firstFailure!.Message}", firstFailure);
        }
    }

    private void RespondToArpRequest(ArpFrameInfo request)
    {
        var clients = clientMappings.Mappings;
        var activeProfile = profile;
        var activeGatewayMac = gatewayMac;
        if (!controlling || restoring ||
            request.Operation != ArpOperation.Request ||
            activeProfile is null ||
            activeGatewayMac is null)
        {
            return;
        }

        if (request.TargetIp.Equals(activeProfile.GatewayAddress) &&
            clients.TryGetValue(request.SenderIp, out var requestingClient) &&
            request.SenderMac.Equals(requestingClient))
        {
            SendInterceptionReply(request.SenderIp, requestingClient, InterceptionTargets.Client,
                activeProfile, activeGatewayMac);
            return;
        }

        if (request.SenderIp.Equals(activeProfile.GatewayAddress) &&
            request.SenderMac.Equals(activeGatewayMac) &&
            clients.TryGetValue(request.TargetIp, out var requestedClient))
        {
            SendInterceptionReply(request.TargetIp, requestedClient, InterceptionTargets.Gateway,
                activeProfile, activeGatewayMac);
        }
    }

    private void SendInterceptionReply(
        IPAddress address,
        PhysicalAddress mac,
        InterceptionTargets target,
        AdapterProfile activeProfile,
        PhysicalAddress activeGatewayMac)
    {
        if (!TrafficMetering.CanInterceptClient(activeProfile, address, mac, activeGatewayMac) ||
            !clientMappings.TryGetBinding(address, mac, out var version))
        {
            return;
        }

        clientMappings.WithCurrentBinding(address, mac, version, () =>
        {
            if (!controlling || restoring || !policy.GetInterceptionTargets(mac.ToString()).HasFlag(target) ||
                !RememberInterception(address, mac, version))
            {
                return;
            }

            var frames = ArpInterceptionFrames.BuildPoison(
                activeProfile.LocalMac, activeProfile.GatewayAddress, activeGatewayMac, address, mac);
            foreach (var frame in frames.Select(target))
            {
                SendArpPacket(frame);
            }
        });
    }

    private void SendArpPacket(byte[] bytes)
    {
        try
        {
            var activeDevice = arpDevice ??
                throw new InvalidOperationException("The ARP capture adapter is not open.");
            lock (arpSendSync)
            {
                activeDevice.SendPacket(bytes);
            }
        }
        catch (Exception)
        {
            diagnostics.RecordInjectionFailure(arp: true);
            throw;
        }
    }

    private void SendForwardingPacket(byte[] bytes)
    {
        // Some Wi-Fi drivers allow a second Npcap handle to capture but reject
        // packet injection through it with ERROR_BAD_UNIT (20). The ARP handle
        // has already proven that it can inject on this adapter, and capture
        // filters do not restrict packets sent through a handle.
        LibPcapLiveDevice activeDevice;
        IReadOnlyList<byte[]> frames;
        try
        {
            activeDevice = PacketInjectionPolicy.SelectHandle(arpDevice, forwardingDevice);
            frames = PacketInjectionPolicy.PrepareFrames(bytes);
        }
        catch (Exception)
        {
            diagnostics.RecordInjectionFailure(arp: false);
            throw;
        }

        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            try
            {
                lock (arpSendSync)
                {
                    activeDevice.SendPacket(frame);
                }
            }
            catch (Exception exception)
            {
                diagnostics.RecordInjectionFailure(arp: false);
                var adapterName = profile?.Name ?? "unknown adapter";
                throw new InvalidOperationException(
                    $"Npcap rejected a forwarded frame on '{adapterName}' " +
                    $"(captured {bytes.Length} bytes, sending {frame.Length} bytes, " +
                    $"segment {index + 1}/{frames.Count}): {exception.Message}",
                    exception);
            }
        }
    }

    private async Task AwaitBackgroundTaskAsync()
    {
        if (backgroundTask is null)
        {
            return;
        }

        try
        {
            await backgroundTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(
                ref backgroundFailure, $"Worker shutdown reported: {exception.Message}", null);
            RaiseStatus(
                $"Forwarding stopped unexpectedly; restoring network: {exception.Message}");
        }
    }

    private void UpdateCaptureStatistics()
    {
        ReadStatistics(arpDevice, true);
        ReadStatistics(forwardingDevice, false);
    }

    private void ReadStatistics(LibPcapLiveDevice? device, bool arp)
    {
        if (device is null)
        {
            return;
        }

        try
        {
            var statistics = device.Statistics;
            diagnostics.SetCaptureStatistics(arp, new CaptureHandleStatistics(
                statistics.ReceivedPackets,
                statistics.DroppedPackets,
                statistics.InterfaceDroppedPackets));
        }
        catch (Exception exception)
        {
            diagnostics.SetCaptureStatistics(arp, null, exception.Message);
        }
    }

    private sealed record IdentityBinding(long Version);

    private void HandleBackgroundFailure(string source, Exception exception)
    {
        var failedSession = SessionId;
        var failure = $"{source}: {exception.Message}";
        if (Interlocked.CompareExchange(ref backgroundFailure, failure, null) is not null)
        {
            return;
        }

        RaiseStatus($"{failure}; restoring the network.");
        _ = Task.Run(() => StopSessionAsync(failedSession));
    }

    private void RaiseStatus(string message) => StatusChanged?.Invoke(this, message);

    private void RaiseStateChanged(
        bool isRunning,
        string statusMessage,
        string? failureMessage = null) =>
        StateChanged?.Invoke(
            this,
            new PcapEngineStateChangedEventArgs(
                isRunning,
                statusMessage,
                failureMessage));
}
