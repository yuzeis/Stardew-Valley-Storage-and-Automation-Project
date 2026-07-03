using Microsoft.Xna.Framework;
using StardewValley;

namespace Koizumi.SVSAP.Api;

public enum SvsapApiErrorCode
{
    None = 0,
    NotFound = 1,
    InvalidRequest = 2,
    NotHost = 3,
    NetworkInactive = 4,
    EndpointNotLinked = 5,
    EndpointInactive = 6,
    ItemUnavailable = 7,
    ItemLocked = 8,
    StorageFull = 9,
    Unsupported = 10,
    InternalError = 11
}

public interface ISvsapApi
{
    int ApiVersion { get; }
    string NetworkIdModDataKey { get; }
    string EndpointIdModDataKey { get; }
    bool IsHostAuthority { get; }

    ISvsapConfigSnapshot GetConfigSnapshot();

    bool TryGetLinkedEndpoint(GameLocation location, Vector2 tile,
        out ISvsapEndpointInfo? endpoint, out SvsapApiErrorCode code, out string message);

    bool TryGetLinkedEndpoint(StardewValley.Object placedObject,
        out ISvsapEndpointInfo? endpoint, out SvsapApiErrorCode code, out string message);

    bool IsEndpointActive(Guid networkId, Guid endpointId,
        out SvsapApiErrorCode code, out string message);

    bool TryGetNetworkName(Guid networkId, out string name);

    int GetAvailableCount(Guid networkId, string qualifiedItemId, int? quality);

    bool TryPeekFirstMatchingItem(Guid networkId,
        Func<Item, bool> predicate,
        bool highQualityFirst,
        bool preserveGoldIridium,
        out Item? prototype,
        out int availableCount,
        out SvsapApiErrorCode code,
        out string message);

    int GetInsertCapacity(Guid networkId, Item item, int maxCount);

    bool TryInsertItem(Guid networkId, Item item,
        out Item? remainder, out SvsapApiErrorCode code, out string message);

    bool TryExtractItem(Guid networkId, string qualifiedItemId, int? quality, int count,
        out Item? extracted, out SvsapApiErrorCode code, out string message);

    bool TryExtractFirstMatchingItem(Guid networkId,
        Func<Item, bool> predicate,
        Func<Item, int> requestedCountSelector,
        bool highQualityFirst,
        bool preserveGoldIridium,
        out Item? extracted,
        out SvsapApiErrorCode code,
        out string message);
}

public interface ISvsapEndpointInfo
{
    Guid NetworkId { get; }
    Guid EndpointId { get; }
    string LocationName { get; }
    int TileX { get; }
    int TileY { get; }
    string EndpointType { get; }
    bool Active { get; }
}

public interface ISvsapConfigSnapshot
{
    bool EnableSimpleWirelessWithinFarm { get; }
    bool RequireCables { get; }
    int MaxEndpointsPerNetwork { get; }
    int MaxOperationsPerTick { get; }
}
