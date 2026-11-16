using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Lantern.Core.Control;

namespace Lantern.Core.Networking;

public enum CapturedNetworkProtocol
{
    Malformed,
    Other,
    Ipv4,
    Ipv6,
}

public enum FrameIgnoreReason
{
    None,
    InvalidOrUnsupportedFrame,
    Ipv6ObservationOnly,
    LocalComputerTraffic,
    NotAddressedToController,
    UnknownClient,
    ClientBindingMismatch,
    ProtectedAddress,
}

public readonly record struct LocalTrafficMeasurement(
    TrafficDirection Direction,
    int PayloadByteCount);

public static class TrafficMetering
{
    public static CapturedNetworkProtocol ClassifyProtocol(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14)
        {
            return CapturedNetworkProtocol.Malformed;
        }

        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        if (etherType == 0x8100)
        {
            if (frame.Length < 18)
            {
                return CapturedNetworkProtocol.Malformed;
            }

            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[16..]);
        }

        return etherType switch
        {
            0x0800 => CapturedNetworkProtocol.Ipv4,
            0x86dd => CapturedNetworkProtocol.Ipv6,
            _ => CapturedNetworkProtocol.Other,
        };
    }

    public static bool IsInSubnet(IPAddress address, IPAddress localAddress, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            localAddress.AddressFamily != AddressFamily.InterNetwork ||
            prefixLength is < 0 or > 32)
        {
            return false;
        }

        var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        return (ToUInt32(address) & mask) == (ToUInt32(localAddress) & mask);
    }

    public static bool IsUnicastMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 6 && (bytes[0] & 1) == 0 && bytes.Any(value => value != 0);
    }

    public static bool IsUnicastIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && bytes[0] != 127 &&
               !address.Equals(IPAddress.Broadcast);
    }

    public static bool CanInterceptClient(
        AdapterProfile adapter,
        IPAddress address,
        PhysicalAddress macAddress,
        PhysicalAddress? gatewayMac = null)
    {
        return IsClientAddress(adapter, address) && IsUnicastMac(macAddress) &&
               !macAddress.Equals(adapter.LocalMac) && !macAddress.Equals(gatewayMac);
    }

    public static bool IsClientAddress(AdapterProfile adapter, IPAddress address)
    {
        if (!IsUnicastIpv4(address) ||
            address.Equals(adapter.LocalAddress) || address.Equals(adapter.GatewayAddress) ||
            !IsInSubnet(address, adapter.LocalAddress, adapter.PrefixLength))
        {
            return false;
        }

        if (adapter.PrefixLength <= 30)
        {
            var hostMask = adapter.PrefixLength == 0
                ? uint.MaxValue
                : uint.MaxValue >> adapter.PrefixLength;
            var host = ToUInt32(address) & hostMask;
            if (host == 0 || host == hostMask)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsPublicOffSubnetPeer(IPAddress address, AdapterProfile adapter)
    {
        if (!IsUnicastIpv4(address) ||
            address.Equals(adapter.GatewayAddress) ||
            IsInSubnet(address, adapter.LocalAddress, adapter.PrefixLength))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] != 10 &&
               !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
               !(bytes[0] == 169 && bytes[1] == 254) &&
               !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
               !(bytes[0] == 192 && bytes[1] == 168) &&
               !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) &&
               !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
               !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
               !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }

    private static uint ToUInt32(IPAddress address) =>
        BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
}

public sealed class LocalIpv4TrafficMeter
{
    private readonly AdapterProfile adapter;
    private readonly byte[] localAddressBytes;
    private readonly byte[] localMacBytes;
    private readonly byte[] gatewayMacBytes;

    public LocalIpv4TrafficMeter(AdapterProfile adapter, PhysicalAddress gatewayMac)
    {
        this.adapter = adapter;
        localAddressBytes = adapter.LocalAddress.GetAddressBytes();
        localMacBytes = adapter.LocalMac.GetAddressBytes();
        gatewayMacBytes = gatewayMac.GetAddressBytes();
    }

    public bool TryMeasure(ReadOnlySpan<byte> frame, out LocalTrafficMeasurement measurement)
    {
        measurement = default;
        if (frame.Length < 34 ||
            TrafficMetering.ClassifyProtocol(frame) != CapturedNetworkProtocol.Ipv4)
        {
            return false;
        }

        var ipOffset = frame[12] == 0x81 ? 18 : 14;
        if (frame.Length < ipOffset + 20)
        {
            return false;
        }

        // Reject forwarded copies before the allocating legacy payload parser.
        var upload = frame.Slice(6, 6).SequenceEqual(localMacBytes) &&
                     frame[..6].SequenceEqual(gatewayMacBytes) &&
                     frame.Slice(ipOffset + 12, 4).SequenceEqual(localAddressBytes);
        var download = frame[..6].SequenceEqual(localMacBytes) &&
                       frame.Slice(6, 6).SequenceEqual(gatewayMacBytes) &&
                       frame.Slice(ipOffset + 16, 4).SequenceEqual(localAddressBytes);
        if ((!upload && !download) ||
            !EthernetFrameCodec.TryParseIpv4(frame, out var ipv4) ||
            !TrafficMetering.IsPublicOffSubnetPeer(upload ? ipv4.Destination : ipv4.Source, adapter))
        {
            return false;
        }

        measurement = new LocalTrafficMeasurement(
            upload ? TrafficDirection.Upload : TrafficDirection.Download,
            ipv4.TransportPayloadLength);
        return true;
    }
}
