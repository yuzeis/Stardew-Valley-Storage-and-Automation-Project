using SVSAP.Models;
using StardewValley;

namespace SVSAP.Services;

internal static class ItemDisplayService
{
    public static string FormatIngredientLine(NetworkItemRequest request, int availableCount, int requiredCount)
    {
        return ModText.Format(
            availableCount < requiredCount ? "craftingTerminal.ingredient.missing" : "craftingTerminal.ingredient.available",
            GetRequestDisplayName(request),
            availableCount,
            requiredCount);
    }

    public static string GetRequestDisplayName(NetworkItemRequest request)
    {
        var key = !string.IsNullOrWhiteSpace(request.QualifiedItemId)
            ? GetQualifiedItemDisplayName(request.QualifiedItemId)
            : GetCategoryDisplayName(request.Category);

        if (string.IsNullOrWhiteSpace(request.PreservedParentQualifiedItemId))
            return key;

        return ModText.Format(
            "inventory.request.fromParent",
            key,
            GetQualifiedItemDisplayName(request.PreservedParentQualifiedItemId));
    }

    public static string GetQualityDisplayName(int? quality)
    {
        return TerminalInventoryFilters.GetQualityLabel(quality);
    }

    public static string GetQualifiedItemDisplayName(string? qualifiedItemId)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return ModText.Get("inventory.request.unknown");

        try
        {
            return ItemRegistry.Create(qualifiedItemId).DisplayName;
        }
        catch
        {
            return qualifiedItemId;
        }
    }

    private static string GetCategoryDisplayName(int? category)
    {
        if (category is null)
            return ModText.Get("inventory.request.unknown");

        var terminalCategory = TerminalInventoryFilters.GetCategory(category.Value);
        if (terminalCategory != TerminalInventoryCategory.Other)
            return TerminalInventoryFilters.GetLabel(terminalCategory);

        return ModText.Format("inventory.request.category", category.Value);
    }
}
