using StardewValley;

namespace SVSAP.Models;

internal sealed class NetworkCraftingRecipe
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Item OutputPrototype { get; set; } = null!;
    public int OutputCount { get; set; } = 1;
    public bool IsBigCraftable { get; set; }
    public List<NetworkItemRequest> Ingredients { get; set; } = new();
}

internal sealed class CraftingAvailability
{
    public bool CanCraft { get; set; }
    public List<string> MissingLines { get; set; } = new();
}

internal enum MaterialQualityStrategy
{
    LowQualityFirst,
    HighQualityFirst,
    PreserveGoldIridium
}
