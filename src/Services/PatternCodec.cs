using System.Text.Json;
using SVSAP.Content;
using SVSAP.Models;
using StardewValley;

namespace SVSAP.Services;

internal static class PatternCodec
{
    public const string PatternDataKey = ModItemCatalog.UniqueId + "/PatternData";

    public static bool IsPatternItem(Item? item)
    {
        return item?.QualifiedItemId is "(O)" + ModItemCatalog.CraftingPattern or "(O)" + ModItemCatalog.ProcessingPattern;
    }

    public static bool TryRead(Item item, out PatternData data)
    {
        data = new PatternData();
        if (!item.modData.TryGetValue(PatternDataKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<PatternData>(raw);
            if (parsed is null)
                return false;

            data = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static Item CreatePatternItem(PatternData data)
    {
        var itemId = data.Kind == PatternKind.Crafting
            ? "(O)" + ModItemCatalog.CraftingPattern
            : "(O)" + ModItemCatalog.ProcessingPattern;

        var item = ItemRegistry.Create(itemId);
        item.Stack = 1;
        item.modData[PatternDataKey] = JsonSerializer.Serialize(data);
        return item;
    }

    public static PatternSlotData ToSlotData(Item patternItem, int slotIndex)
    {
        var modData = new Dictionary<string, string>();
        foreach (var key in patternItem.modData.Keys)
            modData[key] = patternItem.modData[key];

        return new PatternSlotData
        {
            SlotIndex = slotIndex,
            QualifiedItemId = patternItem.QualifiedItemId,
            ModData = modData
        };
    }

    public static Item CreateItem(PatternSlotData slot)
    {
        var item = ItemRegistry.Create(slot.QualifiedItemId);
        item.Stack = 1;
        foreach (var pair in slot.ModData)
            item.modData[pair.Key] = pair.Value;

        return item;
    }
}

