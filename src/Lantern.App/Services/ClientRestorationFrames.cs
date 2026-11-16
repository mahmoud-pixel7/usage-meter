using System.Net.NetworkInformation;
using Lantern.Core.Networking;

namespace Lantern.App.Services;

public static class ClientRestorationFrames
{
    public static IReadOnlyList<byte[]> Build(
        AdapterProfile adapter,
        PhysicalAddress gatewayMac,
        ClientRestorationTarget target)
    {
        if (!TrafficMetering.CanInterceptClient(adapter, target.Address, target.MacAddress, gatewayMac))
        {
            return [];
        }

        var restore = ArpInterceptionFrames.BuildRestore(
            adapter.LocalMac, adapter.GatewayAddress, gatewayMac, target.Address, target.MacAddress);
        var recovery = ArpInterceptionFrames.BuildRecoveryRequest(
            adapter.LocalMac, gatewayMac, adapter.GatewayAddress, target.MacAddress, target.Address);
        // A retired owner may no longer own this IP. Do not assert it at the gateway.
        return target.RestoreGatewayMapping
            ? [restore.ToClient, restore.ToGateway, recovery]
            : [restore.ToClient, recovery];
    }
}
