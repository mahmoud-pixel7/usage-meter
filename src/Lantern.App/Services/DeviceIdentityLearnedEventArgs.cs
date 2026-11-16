using System.Net;
using System.Net.NetworkInformation;

namespace Lantern.App.Services;

public sealed class DeviceIdentityLearnedEventArgs(
    PhysicalAddress macAddress,
    string hostName,
    long sessionId = 0,
    IPAddress? address = null) : EventArgs
{
    public PhysicalAddress MacAddress { get; } = macAddress;

    public string HostName { get; } = hostName;

    public long SessionId { get; } = sessionId;

    public IPAddress? Address { get; } = address;
}
