using System.Text.Json;
using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace SVSAP.Services;

internal sealed class StorageCellInitializer
{
    internal const string CellIdKey = ModItemCatalog.UniqueId + "/CellId";
    internal const string CellDataKey = ModItemCatalog.UniqueId + "/StorageCellData";

    private readonly IMonitor monitor;

    public StorageCellInitializer(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    public void InitializeInventory(IEnumerable<Item?> items)
    {
        if (items is IList<Item?> itemList)
        {
            this.InitializeItemList(itemList, ReturnItemWithoutLocation, "inventory");
            return;
        }

        var seenCellIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            this.EnsureCellData(item);
            this.EnsureUniqueCellData(item, seenCellIds);
        }
    }

    public void InitializeInventory(Farmer? player)
    {
        if (player is null)
            return;

        this.InitializeItemList(
            player.Items,
            item => ReturnItemToFarmer(player, item, this.monitor),
            $"{player.Name}'s inventory");
    }

    public void InitializeChest(Chest? chest, GameLocation? location)
    {
        if (chest is null)
            return;

        this.InitializeItemList(
            chest.Items,
            item => ReturnItemToChest(chest, location, item, this.monitor),
            "chest");
    }

    public void EnsureCellData(Item? item)
    {
        if (item is null || !ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out var tier))
            return;

        var hasCellId = Guid.TryParse(item.modData.GetValueOrDefault(CellIdKey), out var cellId);
        if (StorageCellCodec.TryReadCellData(item, out var existingData))
        {
            if (!hasCellId)
                cellId = existingData.CellId != Guid.Empty ? existingData.CellId : Guid.NewGuid();

            existingData.CellId = cellId;
            existingData.Tier = tier;
            existingData.CapacityMax = StorageCellTierInfo.GetCapacity(tier);
            item.modData[CellIdKey] = cellId.ToString("N");
            item.modData[CellDataKey] = JsonSerializer.Serialize(existingData, StorageCellJsonContext.Default.StorageCellData);
            ApplyNonEmptyQuestFlag(item, existingData);
            return;
        }

        if (!hasCellId)
            cellId = Guid.NewGuid();

        this.AssignNewEmptyCellData(item, tier, cellId);
    }

    public void ResetEmptyCellData(Item? item)
    {
        if (item is null || !ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out var tier))
            return;

        if (StorageCellCodec.TryReadCellData(item, out var data) && !IsCellEmpty(data))
        {
            this.monitor.Log($"Refused to reset non-empty storage cell {item.QualifiedItemId} ({data.CellId:N}).", LogLevel.Warn);
            return;
        }

        this.AssignNewEmptyCellData(item, tier, Guid.NewGuid());
    }

    private void EnsureUniqueCellData(Item? item, HashSet<Guid> seenCellIds)
    {
        if (item is null || !ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out var tier))
            return;

        if (!Guid.TryParse(item.modData.GetValueOrDefault(CellIdKey), out var cellId))
            return;

        if (seenCellIds.Add(cellId))
            return;

        if (StorageCellCodec.TryReadCellData(item, out var data) && IsCellEmpty(data))
        {
            this.ResetEmptyCellData(item);
            if (Guid.TryParse(item.modData.GetValueOrDefault(CellIdKey), out var replacementId))
                seenCellIds.Add(replacementId);
            return;
        }

        this.AssignNewEmptyCellData(item, tier, Guid.NewGuid());
        if (Guid.TryParse(item.modData.GetValueOrDefault(CellIdKey), out var replacementNonEmptyId))
            seenCellIds.Add(replacementNonEmptyId);

        this.monitor.Log($"Duplicate non-empty storage cell identity detected for {item.QualifiedItemId} ({cellId:N}); converted the duplicate to an empty cell to prevent storage duplication.", LogLevel.Warn);
    }

    private void InitializeItemList(IList<Item?> items, Action<Item> overflowItem, string sourceName)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            this.EnsureCellData(item);
            this.SplitStackedStorageCell(items, i, overflowItem, sourceName);
        }

        var seenCellIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            this.EnsureCellData(item);
            this.EnsureUniqueCellData(item, seenCellIds);
        }
    }

    private void SplitStackedStorageCell(IList<Item?> items, int index, Action<Item> overflowItem, string sourceName)
    {
        var item = items[index];
        if (item is null
            || item.Stack <= 1
            || !ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out var tier))
        {
            return;
        }

        var extraCount = item.Stack - 1;
        item.Stack = 1;
        for (var i = 0; i < extraCount; i++)
        {
            var extra = ItemRegistry.Create(item.QualifiedItemId);
            extra.Stack = 1;
            this.AssignNewEmptyCellData(extra, tier, Guid.NewGuid());
            if (!TryPlaceInEmptySlot(items, extra))
                overflowItem(extra);
        }

        this.monitor.Log($"Split stacked SVSAP storage cell {item.QualifiedItemId} x{extraCount + 1:N0} in {sourceName}; only the first item kept the original cell data.", LogLevel.Warn);
    }

    private void AssignNewEmptyCellData(Item item, StorageCellTier tier, Guid cellId)
    {
        item.modData[CellIdKey] = cellId.ToString("N");
        var data = new StorageCellData
        {
            CellId = cellId,
            Tier = tier,
            CapacityUsed = 0,
            CapacityMax = StorageCellTierInfo.GetCapacity(tier)
        };

        item.modData[CellDataKey] = JsonSerializer.Serialize(data, StorageCellJsonContext.Default.StorageCellData);
        ApplyNonEmptyQuestFlag(item, data);
        this.monitor.Log($"Initialized storage cell modData for {item.QualifiedItemId} with capacity {data.CapacityMax:N0}.", LogLevel.Trace);
    }

    private static bool IsCellEmpty(StorageCellData data)
    {
        return data.CapacityUsed <= 0 && data.Items.All(stack => stack.Count <= 0);
    }

    private static void ApplyNonEmptyQuestFlag(Item item, StorageCellData data)
    {
        if (item is StardewValley.Object obj)
            obj.questItem.Value = !IsCellEmpty(data);
    }

    private static bool TryPlaceInEmptySlot(IList<Item?> items, Item item)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not null)
                continue;

            items[i] = item;
            return true;
        }

        return false;
    }

    private static void ReturnItemWithoutLocation(Item item)
    {
        // Only used for synthetic lists where there is no safe world position.
    }

    private static void ReturnItemToFarmer(Farmer player, Item item, IMonitor monitor)
    {
        if (TryPlaceInEmptySlot(player.Items, item))
            return;

        var location = player.currentLocation ?? Game1.currentLocation;
        if (Context.IsWorldReady && location is not null)
            Game1.createItemDebris(item, player.Position, player.FacingDirection, location);
        else
            monitor.Log($"Could not return split SVSAP storage cell {item.QualifiedItemId}; no current location was available.", LogLevel.Error);
    }

    private static void ReturnItemToChest(Chest chest, GameLocation? location, Item item, IMonitor monitor)
    {
        if (TryPlaceInEmptySlot(chest.Items, item))
            return;

        if (Context.IsWorldReady && location is not null)
            Game1.createItemDebris(item, (chest.TileLocation + new Vector2(0.5f, 0.5f)) * Game1.tileSize, -1, location);
        else
            monitor.Log($"Could not return split SVSAP storage cell {item.QualifiedItemId}; no chest location was available.", LogLevel.Error);
    }
}
