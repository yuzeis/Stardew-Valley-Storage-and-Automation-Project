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
            PreserveType = item is SObject preserveObject && preserveObject.preserve.Value.HasValue
                ? (int)preserveObject.preserve.Value.Value
                : null,
            Price = item is SObject priceObject ? priceObject.Price : null,
            Edibility = item is SObject edibleObject ? edibleObject.Edibility : null,
            Category = item is SObject categoryObject ? categoryObject.Category : null,
            Type = item is SObject typeObject ? typeObject.Type : string.Empty,
            Name = item is SObject nameObject ? nameObject.Name : item.Name,
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
            if (!string.IsNullOrWhiteSpace(data.PreservedParentSheetIndex))
                obj.preservedParentSheetIndex.Value = data.PreservedParentSheetIndex;
            if (data.PreserveType.HasValue)
                obj.preserve.Value = (SObject.PreserveType)data.PreserveType.Value;
            if (data.Price.HasValue)
                obj.Price = data.Price.Value;
            if (data.Edibility.HasValue)
                obj.Edibility = data.Edibility.Value;
            if (data.Category.HasValue)
                obj.Category = data.Category.Value;
            if (!string.IsNullOrWhiteSpace(data.Type))
                obj.Type = data.Type;
            if (!string.IsNullOrWhiteSpace(data.Name))
                obj.Name = data.Name;
        }

        if (item is ColoredObject colored && data.Color is not null)
            colored.color.Value = new Microsoft.Xna.Framework.Color(data.Color.Value);

        foreach (var pair in data.ModData)
            item.modData[pair.Key] = pair.Value;

        return item;
    }

    public static bool CanRoundTripPrototype(Item item)
    {
        try
        {
            var prototype = item.getOne();
            prototype.Stack = 1;
            var restored = CreateItem(SerializePrototype(prototype), 1);
            return prototype.canStackWith(restored) && restored.canStackWith(prototype);
        }
        catch
        {
            return false;
        }
    }

    private sealed class SerializedItemData
    {
        public string QualifiedItemId { get; set; } = string.Empty;
        public int Quality { get; set; }
        public string PreservedParentSheetIndex { get; set; } = string.Empty;
        public int? PreserveType { get; set; }
        public int? Price { get; set; }
        public int? Edibility { get; set; }
        public int? Category { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public uint? Color { get; set; }
        public Dictionary<string, string> ModData { get; set; } = new();
    }
}
