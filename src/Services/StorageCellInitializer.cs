using System.Text.Json;
using SVSAP.Content;
using SVSAP.Models;
using StardewModdingAPI;
using StardewValley;

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
        var seenCellIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            this.EnsureCellData(item);
            this.EnsureUniqueCellData(item, seenCellIds);
        }
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
        if (item is null || !ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out _))
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

        this.monitor.Log($"Duplicate non-empty storage cell identity detected for {item.QualifiedItemId} ({cellId:N}); left unchanged for data safety.", LogLevel.Warn);
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
}
