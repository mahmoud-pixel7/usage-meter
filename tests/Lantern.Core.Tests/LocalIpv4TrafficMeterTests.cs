using System.Net;
using System.Net.NetworkInformation;
using Lantern.Core.Control;
using Lantern.Core.Networking;

namespace Lantern.Core.Tests;

public sealed class LocalIpv4TrafficMeterTests
{
    private static readonly PhysicalAddress LocalMac = PhysicalAddress.Parse("345A6063C052");
    private static readonly PhysicalAddress GatewayMac = PhysicalAddress.Parse("64644A380A15");
    private static readonly PhysicalAddress ClientMac = PhysicalAddress.Parse("E261190DBD54");
    private static readonly IPAddress LocalIp = IPAddress.Parse("192.168.31.247");
    private static readonly IPAddress ClientIp = IPAddress.Parse("192.168.31.61");
    private static readonly IPAddress PublicIp = IPAddress.Parse("1.1.1.1");
    private static readonly AdapterProfile Adapter = new(
        "synthetic", "Synthetic adapter", "No capture device", LocalIp, 24,
        IPAddress.Parse("192.168.31.1"), LocalMac);

    [Fact]
    public void OriginalLocalUpload_UsesTheSameLegacyTcpPayloadMetricAsRoutedTraffic()
    {
        var meter = new LocalIpv4TrafficMeter(Adapter, GatewayMac);
        var frame = BuildFrame(GatewayMac, LocalMac, LocalIp, PublicIp, 100);

        Assert.True(meter.TryMeasure(frame, out var measurement));
        Assert.Equal(TrafficDirection.Upload, measurement.Direction);
        Assert.Equal(100, measurement.PayloadByteCount);
        Assert.True(EthernetFrameCodec.TryParseIpv4(frame, out var ipv4));
        Assert.Equal(ipv4.TransportPayloadLength, measurement.PayloadByteCount);
        Assert.NotEqual(frame.Length, measurement.PayloadByteCount);
    }

    [Fact]
    public void OriginalLocalDownload_UsesLegacyUdpPayloadMetric()
    {
        var meter = new LocalIpv4TrafficMeter(Adapter, GatewayMac);
        var frame = BuildFrame(LocalMac, GatewayMac, PublicIp, LocalIp, 73, udp: true);

        Assert.True(meter.TryMeasure(frame, out var measurement));
        Assert.Equal(TrafficDirection.Download, measurement.Direction);
        Assert.Equal(73, measurement.PayloadByteCount);
    }

    [Fact]
    public void ControllerMacAlone_DoesNotCountForwardedClientTrafficAgain()
    {
        var meter = new LocalIpv4TrafficMeter(Adapter, GatewayMac);
        var router = new FrameRouter(LocalMac, LocalIp, GatewayMac,
            new Dictionary<IPAddress, PhysicalAddress> { [ClientIp] = ClientMac }, new TrafficPolicy());
        var originalUpload = BuildFrame(LocalMac, ClientMac, ClientIp, PublicIp, 150);
        var originalDownload = BuildFrame(LocalMac, GatewayMac, PublicIp, ClientIp, 300);
        var forwardedUpload = router.Route(originalUpload);
        var forwardedDownload = router.Route(originalDownload);

        Assert.Equal(FrameAction.Forward, forwardedUpload.Action);
        Assert.Equal(FrameAction.Forward, forwardedDownload.Action);
        Assert.False(meter.TryMeasure(originalUpload, out _));
        Assert.False(meter.TryMeasure(originalDownload, out _));
        Assert.False(meter.TryMeasure(forwardedUpload.Frame!, out _));
        Assert.False(meter.TryMeasure(forwardedDownload.Frame!, out _));
    }

    [Theory]
    [InlineData("192.168.31.1")]
    [InlineData("192.168.31.61")]
    [InlineData("192.168.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.251")]
    [InlineData("239.255.255.250")]
    [InlineData("255.255.255.255")]
    [InlineData("192.168.31.255")]
    public void PrivateLocalRouterOrNonUnicastPeers_AreNotInternetUsage(string peer)
    {
        var meter = new LocalIpv4TrafficMeter(Adapter, GatewayMac);
        var address = IPAddress.Parse(peer);
        var upload = BuildFrame(GatewayMac, LocalMac, LocalIp, address, 100);
        var download = BuildFrame(LocalMac, GatewayMac, address, LocalIp, 100);

        Assert.False(meter.TryMeasure(upload, out _));
        Assert.False(meter.TryMeasure(download, out _));
    }

    [Fact]
    public void SelectedLocalIpWithoutTheOriginalEthernetPath_IsNotSufficient()
    {
        var meter = new LocalIpv4TrafficMeter(Adapter, GatewayMac);

        Assert.False(meter.TryMeasure(BuildFrame(GatewayMac, ClientMac, LocalIp, PublicIp, 100), out _));
        Assert.False(meter.TryMeasure(BuildFrame(ClientMac, LocalMac, LocalIp, PublicIp, 100), out _));
        Assert.False(meter.TryMeasure(BuildFrame(LocalMac, ClientMac, PublicIp, LocalIp, 100), out _));
        Assert.False(meter.TryMeasure(BuildFrame(ClientMac, GatewayMac, PublicIp, LocalIp, 100), out _));
    }

    [Fact]
    public void Ipv6_IsObservationOnlyAndNeverAddedToTheIpv4PayloadMetric()
    {
        var meter = new LocalIpv4TrafficMeter(Adapter, GatewayMac);
        var frame = new byte[14 + 40 + 100];
        LocalMac.GetAddressBytes().CopyTo(frame, 0);
        GatewayMac.GetAddressBytes().CopyTo(frame, 6);
        frame[12] = 0x86;
        frame[13] = 0xdd;
        frame[14] = 0x60;

        Assert.Equal(CapturedNetworkProtocol.Ipv6, TrafficMetering.ClassifyProtocol(frame));
        Assert.False(meter.TryMeasure(frame, out _));
    }

    [Fact]
    public void SingleVlanTag_PreservesTheExistingPayloadMetric()
    {
        var meter = new LocalIpv4TrafficMeter(Adapter, GatewayMac);
        var original = BuildFrame(GatewayMac, LocalMac, LocalIp, PublicIp, 91);
        var tagged = new byte[original.Length + 4];
        original.AsSpan(0, 12).CopyTo(tagged);
        tagged[12] = 0x81;
        tagged[13] = 0x00;
        original.AsSpan(12).CopyTo(tagged.AsSpan(16));

        Assert.Equal(CapturedNetworkProtocol.Ipv4, TrafficMetering.ClassifyProtocol(tagged));
        Assert.True(meter.TryMeasure(tagged, out var measurement));
        Assert.Equal(91, measurement.PayloadByteCount);
    }

    [Theory]
    [InlineData("192.168.31.247")]
    [InlineData("192.168.31.1")]
    [InlineData("192.168.31.0")]
    [InlineData("192.168.31.255")]
    [InlineData("192.168.30.61")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("2001:db8::1")]
    public void InterceptionScope_RejectsProtectedBroadcastIpv6AndOutOfSubnetAddresses(string address)
    {
        Assert.False(TrafficMetering.CanInterceptClient(Adapter, IPAddress.Parse(address), ClientMac, GatewayMac));
    }

    [Theory]
    [InlineData("345A6063C052")]
    [InlineData("64644A380A15")]
    [InlineData("FFFFFFFFFFFF")]
    [InlineData("000000000000")]
    [InlineData("01005E000001")]
    public void InterceptionScope_RejectsControllerGatewayAndNonUnicastMacs(string mac)
    {
        Assert.False(TrafficMetering.CanInterceptClient(Adapter, ClientIp, PhysicalAddress.Parse(mac), GatewayMac));
    }

    private static byte[] BuildFrame(
        PhysicalAddress destinationMac,
        PhysicalAddress sourceMac,
        IPAddress source,
        IPAddress destination,
        int payloadLength,
        bool udp = false)
    {
        var headerLength = udp ? 8 : 20;
        var frame = new byte[14 + 20 + headerLength + payloadLength];
        destinationMac.GetAddressBytes().CopyTo(frame, 0);
        sourceMac.GetAddressBytes().CopyTo(frame, 6);
        frame[12] = 8;
        frame[14] = 0x45;
        var networkLength = frame.Length - 14;
        frame[16] = (byte)(networkLength >> 8);
        frame[17] = (byte)networkLength;
        frame[22] = 64;
        frame[23] = udp ? (byte)17 : (byte)6;
        source.GetAddressBytes().CopyTo(frame, 26);
        destination.GetAddressBytes().CopyTo(frame, 30);
        if (udp)
        {
            frame[38] = (byte)((headerLength + payloadLength) >> 8);
            frame[39] = (byte)(headerLength + payloadLength);
        }
        else
        {
            frame[46] = 0x50;
        }

        return frame;
    }
}
