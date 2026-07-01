using System.Text.Json;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal static class SerializedItemCodec
{
    public static string SerializePrototype(Item item)
    {
        var data = new SerializedItemData
        {
            QualifiedItemId = item.QualifiedItemId,
            Quality = item is SObject obj ? obj.Quality : 0,
            PreservedParentSheetIndex = item is SObject objWithParent ? objWithParent.preservedParentSheetIndex.Value : string.Empty,
            Color = item is ColoredObject colored ? colored.color.Value.PackedValue : null
        };

        foreach (var key in item.modData.Keys)
            data.ModData[key] = item.modData[key];

        return JsonSerializer.Serialize(data);
    }

    public static Item CreateItem(string raw, int stack)
    {
        var data = JsonSerializer.Deserialize<SerializedItemData>(raw) ?? new SerializedItemData();
        var item = ItemRegistry.Create(data.QualifiedItemId);
        item.Stack = stack;

        if (item is SObject obj)
        {
            obj.Quality = data.Quality;
            obj.preservedParentSheetIndex.Value = data.PreservedParentSheetIndex;
        }

        if (item is ColoredObject colored && data.Color is not null)
            colored.color.Value = new Microsoft.Xna.Framework.Color(data.Color.Value);

        foreach (var pair in data.ModData)
            item.modData[pair.Key] = pair.Value;

        return item;
    }

    private sealed class SerializedItemData
    {
        public string QualifiedItemId { get; set; } = string.Empty;
        public int Quality { get; set; }
        public string PreservedParentSheetIndex { get; set; } = string.Empty;
        public uint? Color { get; set; }
        public Dictionary<string, string> ModData { get; set; } = new();
    }
}

