using StardewValley;
using SVSAP.Services;

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
    public List<CraftingIngredientAvailability> Ingredients { get; set; } = new();
    public List<string> IngredientLines { get; set; } = new();
    public List<string> MissingLines { get; set; } = new();
    public List<CraftingMissingIngredient> MissingIngredients { get; set; } = new();
}

internal sealed class CraftingIngredientAvailability
{
    public NetworkItemRequest Request { get; set; } = new();
    public int AvailableCount { get; set; }
    public int RequiredCount { get; set; }
    public bool IsSufficient => this.AvailableCount >= this.RequiredCount;

    public string ToDisplayLine()
    {
        return ItemDisplayService.FormatIngredientLine(this.Request, this.AvailableCount, this.RequiredCount);
    }
}

internal sealed class CraftingMissingIngredient
{
    public NetworkItemRequest Request { get; set; } = new();
    public int AvailableCount { get; set; }
    public int RequiredCount { get; set; }

    public string ToDisplayLine()
    {
        return $"{this.Request.DisplayKey}: {this.AvailableCount:N0}/{this.RequiredCount:N0}";
    }
}

internal enum MaterialQualityStrategy
{
    LowQualityFirst,
    HighQualityFirst,
    PreserveGoldIridium
}
