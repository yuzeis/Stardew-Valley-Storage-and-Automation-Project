using System.Security.Cryptography;
using System.Text;
using SVSAP.Models;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal static class ItemKeyFactory
{
    public static ItemKey FromItem(Item item)
    {
        var key = new ItemKey
        {
            QualifiedItemId = item.QualifiedItemId,
            Quality = 0,
            PreservedParentSheetIndex = string.Empty,
            Color = null,
            NormalizedModDataHash = HashModData(item)
        };

        if (item is SObject obj)
        {
            key.Quality = obj.Quality;
            key.PreservedParentSheetIndex = obj.preservedParentSheetIndex.Value;
        }

        if (item is ColoredObject colored)
            key.Color = colored.color.Value.PackedValue;

        return key;
    }

    public static bool SameDisplayBucket(ItemKey left, ItemKey right)
    {
        return SameCoreBucket(left, right)
            && left.NormalizedModDataHash == right.NormalizedModDataHash;
    }

    public static bool SameStackBucket(ItemKey left, Item leftPrototype, ItemKey right, Item rightPrototype)
    {
        return SameDisplayBucket(left, right)
            || (SameCoreBucket(left, right)
                && leftPrototype.canStackWith(rightPrototype)
                && rightPrototype.canStackWith(leftPrototype));
    }

    private static bool SameCoreBucket(ItemKey left, ItemKey right)
    {
        return left.QualifiedItemId == right.QualifiedItemId
            && left.Quality == right.Quality
            && left.PreservedParentSheetIndex == right.PreservedParentSheetIndex
            && left.Color == right.Color;
    }

    public static string NormalizeItemId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return string.Empty;

        var normalized = itemId.Trim();
        return normalized.StartsWith("(O)", StringComparison.Ordinal)
            ? normalized[3..]
            : normalized;
    }

    private static string HashModData(Item item)
    {
        var builder = new StringBuilder();
        foreach (var modKey in item.modData.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            builder.Append(modKey).Append('=').Append(item.modData[modKey]).Append('\n');
        }

        if (builder.Length == 0)
            return string.Empty;

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
