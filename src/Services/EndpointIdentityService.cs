using SVSAP.Content;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class EndpointIdentityService
{
    internal const string EndpointIdKey = ModItemCatalog.UniqueId + "/EndpointId";
    internal const string NetworkIdKey = ModItemCatalog.UniqueId + "/NetworkId";

    private readonly IMonitor monitor;

    public EndpointIdentityService(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    public Guid EnsureEndpointId(SObject placedObject)
    {
        if (!Guid.TryParse(placedObject.modData.GetValueOrDefault(EndpointIdKey), out var endpointId))
        {
            endpointId = Guid.NewGuid();
            placedObject.modData[EndpointIdKey] = endpointId.ToString("N");
            this.monitor.Log($"Stamped endpoint GUID for {placedObject.QualifiedItemId} at {Game1.currentLocation?.NameOrUniqueName ?? "unknown location"}.", LogLevel.Trace);
        }

        return endpointId;
    }

    public Guid EnsureNetworkId(SObject networkCore)
    {
        if (!Guid.TryParse(networkCore.modData.GetValueOrDefault(NetworkIdKey), out var networkId))
        {
            networkId = Guid.NewGuid();
            networkCore.modData[NetworkIdKey] = networkId.ToString("N");
        }

        return networkId;
    }
}
