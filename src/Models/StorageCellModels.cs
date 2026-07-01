using System.Text.Json.Serialization;

namespace SVSAP.Models;

internal enum StorageCellTier
{
    OneK = 1,
    FourK = 4,
    SixtyFourK = 64,
    TwoHundredFiftySixK = 256,
    OneThousandTwentyFourK = 1024,
    FourThousandNinetySixK = 4096
}

internal static class StorageCellTierInfo
{
    public const int BytesPerItemType = 8;
    public const int ItemsPerStorageByte = 8;

    public static readonly IReadOnlyList<StorageCellTierDescriptor> All = new List<StorageCellTierDescriptor>
    {
        new(StorageCellTier.OneK, "1K", 1024),
        new(StorageCellTier.FourK, "4K", 4096),
        new(StorageCellTier.SixtyFourK, "64K", 65536),
        new(StorageCellTier.TwoHundredFiftySixK, "256K", 262144),
        new(StorageCellTier.OneThousandTwentyFourK, "1024K", 1048576),
        new(StorageCellTier.FourThousandNinetySixK, "4096K", 4194304)
    };

    public static int GetCapacity(StorageCellTier tier)
    {
        return All.First(entry => entry.Tier == tier).Capacity;
    }

    public static bool TryGetCapacity(StorageCellTier tier, out int capacity)
    {
        var descriptor = All.FirstOrDefault(entry => entry.Tier == tier);
        if (descriptor is null)
        {
            capacity = 0;
            return false;
        }

        capacity = descriptor.Capacity;
        return true;
    }

    public static int CalculateUsedBytes(IEnumerable<StoredItemStack> stacks)
    {
        long used = 0;
        foreach (var stack in stacks.Where(stack => stack.Count > 0))
            used += BytesPerItemType + GetItemCountBytes(stack.Count);

        return ClampToInt(used);
    }

    public static int CountActiveTypes(IEnumerable<StoredItemStack> stacks)
    {
        return stacks.Count(stack => stack.Count > 0);
    }

    public static int GetRemainingItemCapacity(StorageCellData data, StoredItemStack? targetStack, int requestedCount, int maxItemTypes)
    {
        if (requestedCount <= 0)
            return 0;

        maxItemTypes = Math.Clamp(maxItemTypes, 1, 63);
        var currentCount = targetStack?.Count > 0 ? targetStack.Count : 0;
        if (currentCount <= 0 && CountActiveTypes(data.Items) >= maxItemTypes)
            return 0;

        var low = 0;
        var high = requestedCount;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (CanFitAdditionalItems(data, targetStack, mid, maxItemTypes))
                low = mid;
            else
                high = mid - 1;
        }

        return low;
    }

    public static int GetSingleTypeItemLimit(StorageCellTier tier)
    {
        var capacity = GetCapacity(tier);
        return Math.Max(0, capacity - BytesPerItemType) * ItemsPerStorageByte;
    }

    private static bool CanFitAdditionalItems(StorageCellData data, StoredItemStack? targetStack, int additionalCount, int maxItemTypes)
    {
        if (additionalCount <= 0)
            return true;

        var currentCount = targetStack?.Count > 0 ? targetStack.Count : 0;
        var usedWithoutTarget = CalculateUsedBytes(data.Items);
        if (currentCount > 0)
            usedWithoutTarget -= BytesPerItemType + GetItemCountBytes(currentCount);
        else if (CountActiveTypes(data.Items) >= maxItemTypes)
            return false;

        var usedAfter = (long)usedWithoutTarget + BytesPerItemType + GetItemCountBytes(currentCount + additionalCount);
        return usedAfter <= data.CapacityMax;
    }

    private static int GetItemCountBytes(int count)
    {
        return count <= 0 ? 0 : (count + ItemsPerStorageByte - 1) / ItemsPerStorageByte;
    }

    private static int ClampToInt(long value)
    {
        if (value <= 0)
            return 0;

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }
}

internal sealed record StorageCellTierDescriptor(StorageCellTier Tier, string DisplayName, int Capacity);

internal sealed class StorageCellData
{
    public Guid CellId { get; set; }
    public StorageCellTier Tier { get; set; }
    /// <summary>Used storage bytes, using SVSAP-style type bytes plus item-count bytes.</summary>
    public int CapacityUsed { get; set; }
    /// <summary>Maximum storage bytes for the cell tier.</summary>
    public int CapacityMax { get; set; }
    public List<StoredItemStack> Items { get; set; } = new();
}

internal sealed class StoredItemStack
{
    public ItemKey Key { get; set; } = new();
    public int Count { get; set; }
    public string SerializedItemPrototype { get; set; } = string.Empty;
}

internal sealed class ItemKey
{
    public string QualifiedItemId { get; set; } = string.Empty;
    public int Quality { get; set; }
    public string PreservedParentSheetIndex { get; set; } = string.Empty;
    public uint? Color { get; set; }
    public string NormalizedModDataHash { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(StorageCellData))]
internal sealed partial class StorageCellJsonContext : JsonSerializerContext
{
}
