using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using Lantern.App.Services;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Networking;
using Xunit;

namespace Lantern.App.Tests;

public sealed class PcapLanEngineMeteringTests
{
    [Fact]
    public void SyntheticCapture_SeparatesOriginalLocalIpv4FromForwardedCopiesAndIpv6WithoutOpeningCapture()
    {
        var start = DateTimeOffset.Parse("2026-11-15T10:00:00Z");
        var localMac = PhysicalAddress.Parse("345A6063C052");
        var gatewayMac = PhysicalAddress.Parse("64644A380A15");
        var clientMac = PhysicalAddress.Parse("E261190DBD54");
        var localIp = IPAddress.Parse("192.168.31.247");
        var clientIp = IPAddress.Parse("192.168.31.61");
        var publicIp = IPAddress.Parse("1.1.1.1");
        var adapter = new AdapterProfile("synthetic", "No capture", "Test", localIp, 24,
            IPAddress.Parse("192.168.31.1"), localMac);
        var registry = new DeviceRegistry(start);
        registry.Observe(localIp, localMac, start, "This PC");
        registry.Observe(clientIp, clientMac, start, "Phone");
        var policy = new TrafficPolicy();
        policy.SetRule(localMac.ToString(), new TrafficRule(true, 1, 1));
        var engine = new PcapLanEngine(registry, policy);
        SetField(engine, "profile", adapter);
        SetField(engine, "frameRouter", new FrameRouter(localMac, localIp, gatewayMac,
            new Dictionary<IPAddress, PhysicalAddress> { [clientIp] = clientMac }, policy, adapter: adapter));
        SetField(engine, "localMeter", new LocalIpv4TrafficMeter(adapter, gatewayMac));
        SetField(engine, "controlling", true);
        var process = typeof(PcapLanEngine).GetMethod("ProcessForwardingFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<ProcessFrame>(engine);

        process(TcpFrame(gatewayMac, localMac, localIp, publicIp, 100));
        process(TcpFrame(localMac, gatewayMac, publicIp, localIp, 200));
        process(TcpFrame(gatewayMac, localMac, clientIp, publicIp, 300));
        process(TcpFrame(clientMac, localMac, publicIp, clientIp, 400));
        process(TcpFrame(clientMac, localMac, localIp, clientIp, 500));
        process(TcpFrame(gatewayMac, localMac, localIp, IPAddress.Parse("192.168.0.1"), 600));
        var ipv6 = new byte[14 + 40 + 100];
        ipv6[12] = 0x86;
        ipv6[13] = 0xdd;
        ipv6[14] = 0x60;
        process(ipv6);

        var snapshots = registry.TakeSnapshot(start.AddSeconds(1));
        var local = snapshots.Single(snapshot => snapshot.MacAddress.Equals(localMac));
        var client = snapshots.Single(snapshot => snapshot.MacAddress.Equals(clientMac));
        var diagnostics = engine.GetDiagnosticsSnapshot();
        Assert.Equal(100, local.UsageUploadBytes);
        Assert.Equal(200, local.UsageDownloadBytes);
        Assert.Equal(0, client.UsageTotalBytes);
        Assert.Equal(100, diagnostics.LocalUploadPayloadBytes);
        Assert.Equal(200, diagnostics.LocalDownloadPayloadBytes);
        Assert.Equal(0, diagnostics.RoutedUploadPayloadBytes + diagnostics.RoutedDownloadPayloadBytes);
        Assert.Equal(6, diagnostics.CapturedIpv4Frames);
        Assert.Equal(1, diagnostics.CapturedIpv6Frames);
        Assert.Equal(1, diagnostics.IgnoredFrames[FrameIgnoreReason.Ipv6ObservationOnly]);
        Assert.Equal(0, diagnostics.ForwardingInjectionFailures);
        Assert.Equal(0, diagnostics.ArpInjectionFailures);
        Assert.Equal(0, engine.ForwardedPacketCount);
        Assert.Equal(0, engine.DroppedPacketCount);
        Assert.False(engine.IsRunning);
    }

    private static void SetField<T>(object target, string name, T value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static byte[] TcpFrame(
        PhysicalAddress destinationMac,
        PhysicalAddress sourceMac,
        IPAddress source,
        IPAddress destination,
        int payloadLength)
    {
        var frame = new byte[14 + 20 + 20 + payloadLength];
        destinationMac.GetAddressBytes().CopyTo(frame, 0);
        sourceMac.GetAddressBytes().CopyTo(frame, 6);
        frame[12] = 8;
        frame[14] = 0x45;
        var networkLength = frame.Length - 14;
        frame[16] = (byte)(networkLength >> 8);
        frame[17] = (byte)networkLength;
        frame[23] = 6;
        source.GetAddressBytes().CopyTo(frame, 26);
        destination.GetAddressBytes().CopyTo(frame, 30);
        frame[46] = 0x50;
        return frame;
    }

    private delegate void ProcessFrame(ReadOnlySpan<byte> frame);
}
