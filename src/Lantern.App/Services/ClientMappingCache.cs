using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using Lantern.Core.Networking;

namespace Lantern.App.Services;

public enum ClientMappingRejection
{
    None,
    InvalidMacOrAddress,
    ProtectedOrOutOfSubnet,
    GatewayOrProxyMac,
    UnconfirmedBindingChange,
    RestorationCapacityReached,
    SessionEnded,
}

public readonly record struct ClientMappingUpdate(
    bool Accepted,
    bool Changed,
    bool IsAmbiguous,
    long BindingVersion,
    ClientMappingRejection Rejection = ClientMappingRejection.None);

public readonly record struct ClientRestorationTarget(
    IPAddress Address,
    PhysicalAddress MacAddress,
    bool RestoreGatewayMapping);

public sealed class ClientMappingCache
{
    private readonly object sync = new();
    private readonly Dictionary<IPAddress, Binding> bindings = new();
    private readonly HashSet<(IPAddress Address, PhysicalAddress MacAddress)> restorationMappings = new();
    private readonly int restorationCapacity;
    private bool acceptingUpdates = true;
    private AdapterProfile? adapter;
    private PhysicalAddress? gatewayMac;
    private long nextBindingVersion;

    public ClientMappingCache(int restorationCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(restorationCapacity);
        this.restorationCapacity = restorationCapacity;
    }

    public ConcurrentDictionary<IPAddress, PhysicalAddress> Mappings { get; } = new();

    public int RestorationTargetCount
    {
        get
        {
            lock (sync)
            {
                return restorationMappings.Count;
            }
        }
    }

    public void BeginAdapter(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (sync)
        {
            Mappings.Clear();
            bindings.Clear();
            restorationMappings.Clear();
            acceptingUpdates = true;
            adapter = null;
            gatewayMac = null;
        }
    }

    public void BeginAdapter(AdapterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        BeginAdapter(profile.Id);
        lock (sync)
        {
            adapter = profile;
        }
    }

    public void SetGatewayMac(PhysicalAddress macAddress)
    {
        ArgumentNullException.ThrowIfNull(macAddress);
        lock (sync)
        {
            if (!acceptingUpdates)
            {
                return;
            }

            gatewayMac = macAddress;
            foreach (var address in Mappings
                         .Where(pair => pair.Value.Equals(macAddress))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                Mappings.TryRemove(address, out _);
                bindings.Remove(address);
            }
        }
    }

    public void FreezeBindings()
    {
        lock (sync)
        {
            acceptingUpdates = false;
        }
    }

    public bool Upsert(IPAddress address, PhysicalAddress macAddress) =>
        UpsertChecked(address, macAddress).Changed;

    public ClientMappingUpdate UpsertChecked(
        IPAddress address,
        PhysicalAddress macAddress,
        bool observed = true)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(macAddress);
        lock (sync)
        {
            if (!acceptingUpdates)
            {
                return new ClientMappingUpdate(false, false, false, 0, ClientMappingRejection.SessionEnded);
            }

            if (!TrafficMetering.IsUnicastIpv4(address) || !TrafficMetering.IsUnicastMac(macAddress))
            {
                return Reject(address, ClientMappingRejection.InvalidMacOrAddress);
            }

            if (macAddress.Equals(gatewayMac))
            {
                return Reject(address, ClientMappingRejection.GatewayOrProxyMac);
            }

            if (adapter is not null &&
                !TrafficMetering.CanInterceptClient(adapter, address, macAddress, gatewayMac))
            {
                return Reject(address, ClientMappingRejection.ProtectedOrOutOfSubnet);
            }

            if (!observed && bindings.Any(pair => pair.Value.Observed &&
                    ((pair.Key.Equals(address) && !pair.Value.MacAddress.Equals(macAddress)) ||
                     (pair.Value.MacAddress.Equals(macAddress) && !pair.Key.Equals(address)))))
            {
                return new ClientMappingUpdate(
                    false, false, false, 0, ClientMappingRejection.UnconfirmedBindingChange);
            }

            var hadPrevious = Mappings.TryGetValue(address, out var previous);
            var changedOwner = hadPrevious && !macAddress.Equals(previous);
            var staleAddresses = Mappings
                .Where(pair => pair.Value.Equals(macAddress) && !pair.Key.Equals(address))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var staleAddress in staleAddresses)
            {
                Mappings.TryRemove(staleAddress, out _);
                bindings.Remove(staleAddress);
            }

            var changed = !hadPrevious || changedOwner || staleAddresses.Length > 0;
            if (changed || !bindings.TryGetValue(address, out var binding) ||
                !binding.MacAddress.Equals(macAddress))
            {
                binding = new Binding(macAddress, ++nextBindingVersion, observed);
            }
            else if (observed && !binding.Observed)
            {
                binding = binding with { Observed = true };
            }

            Mappings[address] = macAddress;
            bindings[address] = binding;
            return new ClientMappingUpdate(
                true,
                changed,
                changedOwner || staleAddresses.Length > 0,
                binding.Version);
        }
    }

    public bool RememberInterception(IPAddress address, PhysicalAddress macAddress, long version)
    {
        lock (sync)
        {
            if (!acceptingUpdates || !IsCurrentBinding(address, macAddress, version))
            {
                return false;
            }

            var key = (address, macAddress);
            if (restorationMappings.Contains(key))
            {
                return true;
            }

            return restorationMappings.Count < restorationCapacity && restorationMappings.Add(key);
        }
    }

    public IReadOnlyList<ClientRestorationTarget> GetRestorationTargets()
    {
        lock (sync)
        {
            return restorationMappings.Select(pair => new ClientRestorationTarget(
                pair.Address,
                pair.MacAddress,
                TryGetBinding(pair.Address, pair.MacAddress, out _)))
                .ToArray();
        }
    }

    public bool TryGetBinding(
        IPAddress address,
        PhysicalAddress macAddress,
        out long version,
        bool requireObserved = false)
    {
        lock (sync)
        {
            if (Mappings.TryGetValue(address, out var currentMac) && currentMac.Equals(macAddress) &&
                bindings.TryGetValue(address, out var binding) &&
                binding.MacAddress.Equals(macAddress) && (!requireObserved || binding.Observed))
            {
                version = binding.Version;
                return true;
            }

            version = 0;
            return false;
        }
    }

    public bool IsCurrentBinding(
        IPAddress address,
        PhysicalAddress macAddress,
        long version,
        bool requireObserved = false) =>
        TryGetBinding(address, macAddress, out var currentVersion, requireObserved) &&
        currentVersion == version;

    public bool WithCurrentBinding(
        IPAddress address,
        PhysicalAddress macAddress,
        long version,
        Action action,
        bool requireObserved = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (sync)
        {
            if (!IsCurrentBinding(address, macAddress, version, requireObserved))
            {
                return false;
            }

            action();
            return true;
        }
    }

    private ClientMappingUpdate Reject(IPAddress address, ClientMappingRejection rejection)
    {
        Mappings.TryRemove(address, out _);
        bindings.Remove(address);
        return new ClientMappingUpdate(false, false, false, 0, rejection);
    }

    private sealed record Binding(PhysicalAddress MacAddress, long Version, bool Observed);
}
