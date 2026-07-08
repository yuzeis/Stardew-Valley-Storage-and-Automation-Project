using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal static class OreDictionaryMatcher
{
    private static readonly IReadOnlyDictionary<string, string[]> ExplicitGroups = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["(O)378"] = new[] { "ore:metal", "ore:copper" },
        ["(O)380"] = new[] { "ore:metal", "ore:iron" },
        ["(O)384"] = new[] { "ore:metal", "ore:gold" },
        ["(O)386"] = new[] { "ore:metal", "ore:iridium" },
        ["(O)909"] = new[] { "ore:metal", "ore:radioactive" },
        ["(O)334"] = new[] { "ingot:metal", "ingot:copper" },
        ["(O)335"] = new[] { "ingot:metal", "ingot:iron" },
        ["(O)336"] = new[] { "ingot:metal", "ingot:gold" },
        ["(O)337"] = new[] { "ingot:metal", "ingot:iridium" },
        ["(O)910"] = new[] { "ingot:metal", "ingot:radioactive" },
        ["(O)382"] = new[] { "resource:coal", "fuel:coal" },
        ["(O)787"] = new[] { "resource:battery", "component:battery" }
    };

    public static bool IsMatch(Item item, IReadOnlyList<string> filterQualifiedItemIds)
    {
        if (filterQualifiedItemIds.Count == 0)
            return true;

        var itemGroups = GetGroups(item);
        foreach (var filterId in filterQualifiedItemIds)
        {
            if (string.Equals(item.QualifiedItemId, filterId, StringComparison.Ordinal))
                return true;

            if (itemGroups.Count == 0)
                continue;

            if (TryCreateFilterItem(filterId, out var filterItem)
                && itemGroups.Overlaps(GetGroups(filterItem)))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> GetDisplayGroups(Item item)
    {
        return GetGroups(item)
            .OrderBy(group => group, StringComparer.Ordinal)
            .Take(5)
            .ToList();
    }

    private static HashSet<string> GetGroups(Item item)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        if (item is SObject obj)
        {
            if (obj.Category == -4)
                groups.Add("ore:fish");
            if (obj.Category is -2 or -12)
                groups.Add("ore:mineral");
            if (obj.Category is -15 or -16)
                groups.Add("ore:material");
            if (obj.Category is -5 or -6 or -26 or -27)
                groups.Add("ore:processed");
        }

        if (ExplicitGroups.TryGetValue(item.QualifiedItemId, out var explicitGroups))
        {
            foreach (var group in explicitGroups)
                groups.Add(group);
        }

        foreach (var tag in GetContextTags(item))
        {
            if (IsUsefulOreTag(tag))
                groups.Add("tag:" + tag);
        }

        return groups;
    }

    private static IEnumerable<string> GetContextTags(Item item)
    {
        try
        {
            return item.GetContextTags() ?? Enumerable.Empty<string>();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private static bool IsUsefulOreTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        return !tag.StartsWith("color_", StringComparison.OrdinalIgnoreCase)
            && !tag.StartsWith("quality_", StringComparison.OrdinalIgnoreCase)
            && !tag.StartsWith("season_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateFilterItem(string qualifiedItemId, out Item item)
    {
        item = null!;
        try
        {
            item = ItemRegistry.Create(qualifiedItemId);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
