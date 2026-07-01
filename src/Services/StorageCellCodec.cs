using System.Text.Json;
using SVSAP.Models;
using StardewValley;

namespace SVSAP.Services;

internal static class StorageCellCodec
{
    public static bool TryReadCellData(Item item, out StorageCellData data)
    {
        return TryReadCellData(item.modData.TryGetValue(StorageCellInitializer.CellDataKey, out var raw) ? raw : null, out data);
    }

    public static bool TryReadCellData(StorageDriveSlotData slot, out StorageCellData data)
    {
        return TryReadCellData(slot.ModData.TryGetValue(StorageCellInitializer.CellDataKey, out var raw) ? raw : null, out data);
    }

    public static StorageDriveSlotData ToSlotData(Item cellItem, int slotIndex)
    {
        var modData = new Dictionary<string, string>();
        foreach (var key in cellItem.modData.Keys)
            modData[key] = cellItem.modData[key];

        var cellId = Guid.TryParse(cellItem.modData.GetValueOrDefault(StorageCellInitializer.CellIdKey), out var parsed)
            ? parsed
            : Guid.NewGuid();

        return new StorageDriveSlotData
        {
            SlotIndex = slotIndex,
            CellId = cellId,
            QualifiedItemId = cellItem.QualifiedItemId,
            ModData = modData
        };
    }

    public static Item CreateItem(StorageDriveSlotData slot)
    {
        var item = ItemRegistry.Create(slot.QualifiedItemId);
        item.Stack = 1;
        foreach (var pair in slot.ModData)
            item.modData[pair.Key] = pair.Value;

        if (item is StardewValley.Object obj && TryReadCellData(slot, out var data))
            obj.questItem.Value = data.CapacityUsed > 0 || data.Items.Any(stack => stack.Count > 0);

        return item;
    }

    public static void WriteCellData(StorageDriveSlotData slot, StorageCellData data)
    {
        slot.CellId = data.CellId;
        slot.ModData[StorageCellInitializer.CellIdKey] = data.CellId.ToString("N");
        slot.ModData[StorageCellInitializer.CellDataKey] = JsonSerializer.Serialize(data, StorageCellJsonContext.Default.StorageCellData);
    }

    private static bool TryReadCellData(string? raw, out StorageCellData data)
    {
        data = new StorageCellData();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize(raw, StorageCellJsonContext.Default.StorageCellData);
            if (parsed is null)
                return false;

            Normalize(parsed);
            data = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Normalize(StorageCellData data)
    {
        if (StorageCellTierInfo.TryGetCapacity(data.Tier, out var capacity))
            data.CapacityMax = capacity;

        data.CapacityUsed = StorageCellTierInfo.CalculateUsedBytes(data.Items);
    }
}
