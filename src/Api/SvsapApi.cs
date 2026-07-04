using Koizumi.SVSAP.Api;
using Microsoft.Xna.Framework;
using SVSAP.Models;
using SVSAP.Services;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAP.Api;

public sealed class SvsapApi : ISvsapApi
{
    private readonly NetworkRepository repository;
    private readonly InventoryTransactionService transactionService;
    private readonly Func<ModConfig> getConfig;

    internal SvsapApi(
        NetworkRepository repository,
        InventoryTransactionService transactionService,
        Func<ModConfig> getConfig)
    {
        this.repository = repository;
        this.transactionService = transactionService;
        this.getConfig = getConfig;
    }

    public int ApiVersion => 2;

    public string NetworkIdModDataKey => EndpointIdentityService.NetworkIdKey;

    public string EndpointIdModDataKey => EndpointIdentityService.EndpointIdKey;

    public bool IsHostAuthority => Context.IsWorldReady && Context.IsMainPlayer;

    public ISvsapConfigSnapshot GetConfigSnapshot()
    {
        var config = this.getConfig();
        return new SvsapConfigSnapshot(
            config.EnableSimpleWirelessWithinFarm,
            config.RequireCables,
            config.MaxEndpointsPerNetwork,
            config.MaxOperationsPerTick);
    }

    public bool TryGetLinkedEndpoint(
        GameLocation location,
        Vector2 tile,
        out ISvsapEndpointInfo? endpoint,
        out SvsapApiErrorCode code,
        out string message)
    {
        endpoint = null;

        if (location is null)
            return Fail(SvsapApiErrorCode.InvalidRequest, "Location is null.", out code, out message);

        if (!location.Objects.TryGetValue(tile, out var placedObject))
            return Fail(SvsapApiErrorCode.NotFound, "No placed object exists at the requested tile.", out code, out message);

        return this.TryGetLinkedEndpoint(placedObject, out endpoint, out code, out message);
    }

    public bool TryGetLinkedEndpoint(
        SObject placedObject,
        out ISvsapEndpointInfo? endpoint,
        out SvsapApiErrorCode code,
        out string message)
    {
        endpoint = null;

        if (placedObject is null)
            return Fail(SvsapApiErrorCode.InvalidRequest, "Placed object is null.", out code, out message);

        if (!TryReadGuid(placedObject, EndpointIdentityService.NetworkIdKey, out var networkId)
            || !TryReadGuid(placedObject, EndpointIdentityService.EndpointIdKey, out var endpointId))
        {
            return Fail(SvsapApiErrorCode.EndpointNotLinked, "The placed object is not linked to an SVSAP network endpoint.", out code, out message);
        }

        if (!this.repository.TryGetNetwork(networkId, out var network))
            return Fail(SvsapApiErrorCode.NotFound, "Network does not exist or is not loaded.", out code, out message);

        var linkedEndpoint = network.Endpoints.FirstOrDefault(candidate => candidate.EndpointId == endpointId);
        if (linkedEndpoint is null)
            return Fail(SvsapApiErrorCode.EndpointNotLinked, "Endpoint was not found in the network.", out code, out message);

        endpoint = new SvsapEndpointInfo(network.NetworkId, linkedEndpoint);
        code = SvsapApiErrorCode.None;
        message = string.Empty;
        return true;
    }

    public bool IsEndpointActive(Guid networkId, Guid endpointId, out SvsapApiErrorCode code, out string message)
    {
        if (!this.repository.TryGetNetwork(networkId, out var network))
            return Fail(SvsapApiErrorCode.NotFound, "Network does not exist or is not loaded.", out code, out message);

        var endpoint = network.Endpoints.FirstOrDefault(candidate => candidate.EndpointId == endpointId);
        if (endpoint is null)
            return Fail(SvsapApiErrorCode.EndpointNotLinked, "Endpoint was not found in the network.", out code, out message);

        if (!endpoint.Active)
            return Fail(SvsapApiErrorCode.EndpointInactive, "Endpoint is currently inactive.", out code, out message);

        code = SvsapApiErrorCode.None;
        message = string.Empty;
        return true;
    }

    public bool TryGetNetworkName(Guid networkId, out string name)
    {
        if (this.repository.TryGetNetwork(networkId, out var network))
        {
            name = network.Name;
            return true;
        }

        name = string.Empty;
        return false;
    }

    public int GetAvailableCount(Guid networkId, string qualifiedItemId, int? quality)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId) || !this.repository.TryGetNetwork(networkId, out var network))
            return 0;

        if (IsLocked(network, qualifiedItemId))
            return 0;

        if (quality.HasValue)
        {
            return this.transactionService.GetUnreservedCountMatching(
                network,
                entry => string.Equals(entry.Key.QualifiedItemId, qualifiedItemId, StringComparison.Ordinal)
                    && entry.Key.Quality == quality.Value);
        }

        return this.transactionService.GetUnreservedCount(
            network,
            new NetworkItemRequest
            {
                QualifiedItemId = qualifiedItemId,
                Count = int.MaxValue
            });
    }

    public bool TryPeekFirstMatchingItem(
        Guid networkId,
        Func<Item, bool> predicate,
        bool highQualityFirst,
        bool preserveGoldIridium,
        out Item? prototype,
        out int availableCount,
        out SvsapApiErrorCode code,
        out string message)
    {
        prototype = null;
        availableCount = 0;

        if (predicate is null)
            return Fail(SvsapApiErrorCode.InvalidRequest, "Match predicate is null.", out code, out message);

        if (!this.repository.TryGetNetwork(networkId, out var network))
            return Fail(SvsapApiErrorCode.NotFound, "Network does not exist or is not loaded.", out code, out message);

        if (!this.transactionService.TryPeekFirstMatchingItem(
                network,
                entry => !IsLocked(network, entry.Key.QualifiedItemId) && MatchesExternalPredicate(predicate, entry.Prototype),
                out prototype,
                out availableCount,
                out message,
                ToMaterialQualityStrategy(highQualityFirst, preserveGoldIridium)))
        {
            return Fail(SvsapApiErrorCode.ItemUnavailable, message, out code, out message);
        }

        code = SvsapApiErrorCode.None;
        return true;
    }

    public int GetInsertCapacity(Guid networkId, Item item, int maxCount)
    {
        if (item is null || item.Stack <= 0 || maxCount <= 0)
            return 0;

        if (!this.repository.TryGetNetwork(networkId, out var network))
            return 0;

        if (IsLocked(network, item.QualifiedItemId))
            return 0;

        var probe = item.getOne();
        probe.Stack = Math.Min(maxCount, Math.Max(1, item.maximumStackSize()));
        return this.transactionService.GetNetworkInsertCapacity(network, probe, maxCount);
    }

    public bool TryInsertItem(
        Guid networkId,
        Item item,
        out Item? remainder,
        out SvsapApiErrorCode code,
        out string message)
    {
        remainder = item;

        if (!this.IsHostAuthority)
            return Fail(SvsapApiErrorCode.NotHost, "SVSAP network writes can only run on the host.", out code, out message);

        if (item is null || item.Stack <= 0)
            return Fail(SvsapApiErrorCode.InvalidRequest, "Item to insert is null or has an invalid count.", out code, out message);

        if (!this.repository.TryGetNetwork(networkId, out var network))
            return Fail(SvsapApiErrorCode.NotFound, "Network does not exist or is not loaded.", out code, out message);

        if (IsLocked(network, item.QualifiedItemId))
            return Fail(SvsapApiErrorCode.ItemLocked, "This item is locked by the network and cannot be inserted automatically.", out code, out message);

        var before = item.Stack;
        if (!this.transactionService.TryDepositItem(network, item, out var moved) || moved <= 0)
            return Fail(SvsapApiErrorCode.StorageFull, "Network cannot accept this item or storage is full.", out code, out message);

        remainder = item.Stack > 0 ? item : null;
        code = SvsapApiErrorCode.None;
        message = remainder is null
            ? $"Inserted {moved:N0} item(s)."
            : $"Inserted {moved:N0}/{before:N0} item(s), remainder {item.Stack:N0}.";
        return true;
    }

    public bool TryExtractItem(
        Guid networkId,
        string qualifiedItemId,
        int? quality,
        int count,
        out Item? extracted,
        out SvsapApiErrorCode code,
        out string message)
    {
        extracted = null;

        if (!this.IsHostAuthority)
            return Fail(SvsapApiErrorCode.NotHost, "SVSAP network extraction can only run on the host.", out code, out message);

        if (string.IsNullOrWhiteSpace(qualifiedItemId) || count <= 0)
            return Fail(SvsapApiErrorCode.InvalidRequest, "Item ID is empty or count is invalid.", out code, out message);

        if (!this.repository.TryGetNetwork(networkId, out var network))
            return Fail(SvsapApiErrorCode.NotFound, "Network does not exist or is not loaded.", out code, out message);

        if (IsLocked(network, qualifiedItemId))
            return Fail(SvsapApiErrorCode.ItemLocked, "This item is locked by the network and cannot be extracted automatically.", out code, out message);

        var success = quality.HasValue
            ? this.transactionService.TryExtractFirstMatchingItem(
                network,
                entry => string.Equals(entry.Key.QualifiedItemId, qualifiedItemId, StringComparison.Ordinal)
                    && entry.Key.Quality == quality.Value,
                _ => count,
                out extracted,
                out message)
            : this.transactionService.TryExtractItem(
                network,
                new NetworkItemRequest
                {
                    QualifiedItemId = qualifiedItemId,
                    Count = count
                },
                count,
                out extracted,
                out message);

        if (!success || extracted is null)
            return Fail(SvsapApiErrorCode.ItemUnavailable, message, out code, out message);

        code = SvsapApiErrorCode.None;
        return true;
    }

    public bool TryExtractFirstMatchingItem(
        Guid networkId,
        Func<Item, bool> predicate,
        Func<Item, int> requestedCountSelector,
        bool highQualityFirst,
        bool preserveGoldIridium,
        out Item? extracted,
        out SvsapApiErrorCode code,
        out string message)
    {
        extracted = null;

        if (!this.IsHostAuthority)
            return Fail(SvsapApiErrorCode.NotHost, "SVSAP network extraction can only run on the host.", out code, out message);

        if (predicate is null || requestedCountSelector is null)
            return Fail(SvsapApiErrorCode.InvalidRequest, "Match predicate or count selector is null.", out code, out message);

        if (!this.repository.TryGetNetwork(networkId, out var network))
            return Fail(SvsapApiErrorCode.NotFound, "Network does not exist or is not loaded.", out code, out message);

        var success = this.transactionService.TryExtractFirstMatchingItem(
            network,
            entry => !IsLocked(network, entry.Key.QualifiedItemId) && MatchesExternalPredicate(predicate, entry.Prototype),
            entry => SelectExternalRequestCount(requestedCountSelector, entry.Prototype),
            out extracted,
            out message,
            ToMaterialQualityStrategy(highQualityFirst, preserveGoldIridium));

        if (!success || extracted is null)
            return Fail(SvsapApiErrorCode.ItemUnavailable, message, out code, out message);

        code = SvsapApiErrorCode.None;
        return true;
    }

    private static bool IsLocked(NetworkData network, string qualifiedItemId)
    {
        return network.LockedQualifiedItemIds.Contains(qualifiedItemId, StringComparer.Ordinal);
    }

    private static MaterialQualityStrategy ToMaterialQualityStrategy(bool highQualityFirst, bool preserveGoldIridium)
    {
        if (preserveGoldIridium)
            return MaterialQualityStrategy.PreserveGoldIridium;

        return highQualityFirst
            ? MaterialQualityStrategy.HighQualityFirst
            : MaterialQualityStrategy.LowQualityFirst;
    }

    private static bool MatchesExternalPredicate(Func<Item, bool> predicate, Item prototype)
    {
        try
        {
            return predicate(prototype);
        }
        catch
        {
            return false;
        }
    }

    private static int SelectExternalRequestCount(Func<Item, int> requestedCountSelector, Item prototype)
    {
        try
        {
            return Math.Max(0, requestedCountSelector(prototype));
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryReadGuid(SObject placedObject, string key, out Guid id)
    {
        return Guid.TryParse(placedObject.modData.GetValueOrDefault(key), out id);
    }

    private static bool Fail(SvsapApiErrorCode errorCode, string failureMessage, out SvsapApiErrorCode code, out string message)
    {
        code = errorCode;
        message = failureMessage;
        return false;
    }

    private sealed record SvsapEndpointInfo(
        Guid NetworkId,
        Guid EndpointId,
        string LocationName,
        int TileX,
        int TileY,
        string EndpointType,
        bool Active) : ISvsapEndpointInfo
    {
        public SvsapEndpointInfo(Guid networkId, NetworkEndpoint endpoint)
            : this(
                networkId,
                endpoint.EndpointId,
                endpoint.LocationName,
                (int)endpoint.TileX,
                (int)endpoint.TileY,
                endpoint.Type.ToString(),
                endpoint.Active)
        {
        }
    }

    private sealed record SvsapConfigSnapshot(
        bool EnableSimpleWirelessWithinFarm,
        bool RequireCables,
        int MaxEndpointsPerNetwork,
        int MaxOperationsPerTick) : ISvsapConfigSnapshot;
}
