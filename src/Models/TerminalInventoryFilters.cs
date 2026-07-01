using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAP.Models;

internal enum TerminalInventoryCategory
{
    All,
    Crops,
    Minerals,
    Fish,
    Cooking,
    Seeds,
    Materials,
    MachineProducts,
    Other
}

internal enum TerminalInventorySortMode
{
    Count,
    Name,
    Price,
    Category,
    Recent
}

internal static class TerminalInventoryFilters
{
    public static readonly TerminalInventoryCategory[] CategoryOrder =
    {
        TerminalInventoryCategory.All,
        TerminalInventoryCategory.Crops,
        TerminalInventoryCategory.Minerals,
        TerminalInventoryCategory.Fish,
        TerminalInventoryCategory.Cooking,
        TerminalInventoryCategory.Seeds,
        TerminalInventoryCategory.Materials,
        TerminalInventoryCategory.MachineProducts,
        TerminalInventoryCategory.Other
    };

    public static readonly TerminalInventorySortMode[] SortOrder =
    {
        TerminalInventorySortMode.Count,
        TerminalInventorySortMode.Name,
        TerminalInventorySortMode.Price,
        TerminalInventorySortMode.Category,
        TerminalInventorySortMode.Recent
    };

    public static TerminalInventoryCategory GetCategory(Item item)
    {
        if (item is not SObject obj)
            return TerminalInventoryCategory.Other;

        return GetCategory(obj.Category);
    }

    public static TerminalInventoryCategory GetCategory(int rawCategory)
    {
        return rawCategory switch
        {
            -75 or -79 or -80 or -81 => TerminalInventoryCategory.Crops,
            -2 or -12 => TerminalInventoryCategory.Minerals,
            -4 => TerminalInventoryCategory.Fish,
            -7 => TerminalInventoryCategory.Cooking,
            -74 => TerminalInventoryCategory.Seeds,
            -15 or -16 => TerminalInventoryCategory.Materials,
            -5 or -6 or -26 or -27 => TerminalInventoryCategory.MachineProducts,
            _ => TerminalInventoryCategory.Other
        };
    }

    public static bool MatchesCategory(TerminalInventoryCategory itemCategory, TerminalInventoryCategory selectedCategory)
    {
        return selectedCategory == TerminalInventoryCategory.All || itemCategory == selectedCategory;
    }

    public static int GetSalePrice(Item item)
    {
        return Math.Max(0, item.salePrice(false));
    }

    public static string GetLabel(TerminalInventoryCategory category)
    {
        return category switch
        {
            TerminalInventoryCategory.Crops => ModText.Get("terminal.category.crops"),
            TerminalInventoryCategory.Minerals => ModText.Get("terminal.category.minerals"),
            TerminalInventoryCategory.Fish => ModText.Get("terminal.category.fish"),
            TerminalInventoryCategory.Cooking => ModText.Get("terminal.category.cooking"),
            TerminalInventoryCategory.Seeds => ModText.Get("terminal.category.seeds"),
            TerminalInventoryCategory.Materials => ModText.Get("terminal.category.materials"),
            TerminalInventoryCategory.MachineProducts => ModText.Get("terminal.category.processed"),
            TerminalInventoryCategory.Other => ModText.Get("terminal.category.other"),
            _ => ModText.Get("terminal.category.all")
        };
    }

    public static string GetLabel(TerminalInventorySortMode sortMode)
    {
        return sortMode switch
        {
            TerminalInventorySortMode.Name => ModText.Get("terminal.sort.name"),
            TerminalInventorySortMode.Price => ModText.Get("terminal.sort.price"),
            TerminalInventorySortMode.Category => ModText.Get("terminal.sort.category"),
            TerminalInventorySortMode.Recent => ModText.Get("terminal.sort.recent"),
            _ => ModText.Get("terminal.sort.count")
        };
    }

    public static string GetQualityLabel(int? quality)
    {
        return quality switch
        {
            0 => ModText.Get("terminal.quality.normal"),
            1 => ModText.Get("terminal.quality.silver"),
            2 => ModText.Get("terminal.quality.gold"),
            4 => ModText.Get("terminal.quality.iridium"),
            _ => ModText.Get("terminal.quality.any")
        };
    }

    public static string FormatStorageSummary(NetworkStorageSummary summary)
    {
        if (summary.CellCount <= 0 || summary.CapacityMax <= 0)
            return ModText.Get("terminal.storage.none");

        return ModText.Format("terminal.storage.summary", summary.CellCount, summary.CapacityUsed, summary.CapacityMax, summary.TypeSlotsUsed, summary.TypeSlotsMax);
    }

    public static string FormatLockedList(IReadOnlyList<string> lockedQualifiedItemIds)
    {
        if (lockedQualifiedItemIds.Count == 0)
            return ModText.Get("terminal.locked.none");

        var shown = lockedQualifiedItemIds.Take(4).ToList();
        var suffix = lockedQualifiedItemIds.Count > shown.Count ? $" +{lockedQualifiedItemIds.Count - shown.Count:N0}" : string.Empty;
        return ModText.Format("terminal.locked.list", string.Join(", ", shown), suffix);
    }
}
