using Microsoft.Xna.Framework;
using SVSAP.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class InventoryScanner
{
    private static readonly Vector2[] AdjacentOffsets =
    {
        new Vector2(0, -1),
        new Vector2(1, 0),
        new Vector2(0, 1),
        new Vector2(-1, 0)
    };

    private readonly Func<ModConfig> getConfig;
    private readonly IMonitor monitor;

    public InventoryScanner(Func<ModConfig> getConfig, IMonitor monitor)
    {
        this.getConfig = getConfig;
        this.monitor = monitor;
    }

    public NetworkInventorySnapshot Scan(NetworkData network)
    {
        var snapshot = new NetworkInventorySnapshot { NetworkId = network.NetworkId };
        var scannedChests = new HashSet<string>(StringComparer.Ordinal);
        var entryBuckets = new Dictionary<StackBucketIndexKey, List<NetworkInventoryEntry>>();

        foreach (var endpoint in network.Endpoints.Where(endpoint => endpoint.Active))
        {
            if (endpoint.Type == EndpointType.StorageInterface)
            {
                var location = this.GetLocation(endpoint.LocationName);
                if (location is null)
                    continue;

                var interfaceTile = new Vector2(endpoint.TileX, endpoint.TileY);
                foreach (var chestInfo in GetAdjacentUnlockedChests(location, interfaceTile))
                {
                    if (!scannedChests.Add(GetChestKey(location, chestInfo.Tile)))
                        continue;

                    snapshot.SourceCount++;
                    this.ScanChest(endpoint, chestInfo.Chest, chestInfo.Tile, snapshot, entryBuckets);
                }
            }
            else if (endpoint.Type == EndpointType.StorageDrive && network.StorageDrives.TryGetValue(endpoint.EndpointId, out var driveData))
            {
                snapshot.SourceCount++;
                this.ScanStorageDrive(endpoint, driveData, snapshot, entryBuckets);
            }
        }

        foreach (var entry in snapshot.Entries)
            entry.LastAddedSequence = this.GetLastAddedSequence(network, entry.Key);

        snapshot.Entries = snapshot.Entries
            .OrderByDescending(entry => entry.TotalCount)
            .ThenBy(entry => entry.Prototype.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return snapshot;
    }

    private long GetLastAddedSequence(NetworkData network, ItemKey key)
    {
        return network.RecentItems
            .Where(entry => ItemKeyFactory.SameDisplayBucket(entry.Key, key))
            .Select(entry => entry.LastAddedSequence)
            .DefaultIfEmpty(0)
            .Max();
    }

    private void ScanChest(
        NetworkEndpoint endpoint,
        Chest chest,
        Vector2 chestTile,
        NetworkInventorySnapshot snapshot,
        Dictionary<StackBucketIndexKey, List<NetworkInventoryEntry>> entryBuckets)
    {
        for (var i = 0; i < chest.Items.Count; i++)
        {
            var item = chest.Items[i];
            if (item is null || item.Stack <= 0)
                continue;

            var key = ItemKeyFactory.FromItem(item);
            var entry = GetOrCreateEntry(snapshot, entryBuckets, key, item);

            entry.TotalCount += item.Stack;
            entry.Locations.Add(new ItemStackLocation
            {
                EndpointId = endpoint.EndpointId,
                CellId = Guid.Empty,
                SourceKind = InventorySourceKind.Chest,
                LocationName = endpoint.LocationName,
                TileX = chestTile.X,
                TileY = chestTile.Y,
                SlotIndex = i,
                Count = item.Stack
            });
        }
    }

    private void ScanStorageDrive(
        NetworkEndpoint endpoint,
        StorageDriveData driveData,
        NetworkInventorySnapshot snapshot,
        Dictionary<StackBucketIndexKey, List<NetworkInventoryEntry>> entryBuckets)
    {
        foreach (var slot in driveData.Slots)
        {
            if (!StorageCellCodec.TryReadCellData(slot, out var cellData))
                continue;

            snapshot.StorageSummary.CellCount++;
            snapshot.StorageSummary.CapacityUsed += Math.Max(0, cellData.CapacityUsed);
            snapshot.StorageSummary.CapacityMax += Math.Max(0, cellData.CapacityMax);
            snapshot.StorageSummary.TypeSlotsUsed += StorageCellTierInfo.CountActiveTypes(cellData.Items);
            snapshot.StorageSummary.TypeSlotsMax += Math.Clamp(this.getConfig().MaxItemTypesPerStorageCell, 1, 63);

            foreach (var stack in cellData.Items.Where(stack => stack.Count > 0))
            {
                Item prototype;
                try
                {
                    prototype = SerializedItemCodec.CreateItem(stack.SerializedItemPrototype, 1);
                }
                catch (Exception ex)
                {
                    this.monitor.Log($"Failed to deserialize storage cell item prototype in cell {slot.CellId}: {ex.Message}", LogLevel.Warn);
                    continue;
                }

                var entry = GetOrCreateEntry(snapshot, entryBuckets, stack.Key, prototype);

                entry.TotalCount += stack.Count;
                entry.Locations.Add(new ItemStackLocation
                {
                    EndpointId = endpoint.EndpointId,
                    CellId = slot.CellId,
                    SourceKind = InventorySourceKind.StorageCell,
                    LocationName = endpoint.LocationName,
                    TileX = endpoint.TileX,
                    TileY = endpoint.TileY,
                    SlotIndex = slot.SlotIndex,
                    Count = stack.Count
                });
            }
        }
    }

    private static NetworkInventoryEntry GetOrCreateEntry(
        NetworkInventorySnapshot snapshot,
        Dictionary<StackBucketIndexKey, List<NetworkInventoryEntry>> entryBuckets,
        ItemKey key,
        Item prototype)
    {
        var bucketKey = new StackBucketIndexKey(
            key.QualifiedItemId,
            key.Quality,
            key.PreservedParentSheetIndex,
            key.Color);
        if (!entryBuckets.TryGetValue(bucketKey, out var bucket))
        {
            bucket = new List<NetworkInventoryEntry>();
            entryBuckets[bucketKey] = bucket;
        }

        var entry = bucket.FirstOrDefault(candidate =>
            ItemKeyFactory.SameStackBucket(candidate.Key, candidate.Prototype, key, prototype));
        if (entry is not null)
            return entry;

        entry = new NetworkInventoryEntry
        {
            Key = key,
            Prototype = prototype.getOne(),
            TotalCount = 0
        };
        snapshot.Entries.Add(entry);
        bucket.Add(entry);
        return entry;
    }

    public GameLocation? GetLocation(string name)
    {
        var direct = Game1.getLocationFromName(name);
        if (direct is not null)
            return direct;

        var fallback = Game1.locations.FirstOrDefault(location => location.NameOrUniqueName == name);
        if (fallback is null)
            this.monitor.Log($"Network endpoint references missing location '{name}'.", LogLevel.Trace);

        return fallback;
    }

    private static bool IsChestLocked(Chest chest)
    {
        return ChestMutexHelper.IsLockedByAnotherActor(chest);
    }

    private static bool IsSupportedNetworkChest(Chest chest)
    {
        return chest.SpecialChestType == Chest.SpecialChestTypes.None
            || chest.SpecialChestType.ToString().Contains("Big", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(Vector2 Tile, Chest Chest)> GetAdjacentUnlockedChests(GameLocation location, Vector2 origin)
    {
        foreach (var offset in AdjacentOffsets)
        {
            var tile = origin + offset;
            if (!location.objects.TryGetValue(tile, out SObject? placedObject) || placedObject is not Chest chest)
                continue;

            if (!IsSupportedNetworkChest(chest) || IsChestLocked(chest))
                continue;

            yield return (tile, chest);
        }
    }

    private static string GetChestKey(GameLocation location, Vector2 tile)
    {
        return $"{location.NameOrUniqueName}:{tile.X:0}:{tile.Y:0}";
    }

    private readonly record struct StackBucketIndexKey(
        string QualifiedItemId,
        int Quality,
        string PreservedParentSheetIndex,
        uint? Color);
}
