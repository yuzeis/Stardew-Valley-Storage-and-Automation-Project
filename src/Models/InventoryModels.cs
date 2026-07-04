using StardewValley;

namespace SVSAP.Models;

internal sealed class NetworkInventorySnapshot
{
    public Guid NetworkId { get; set; }
    public List<NetworkInventoryEntry> Entries { get; set; } = new();
    public int SourceCount { get; set; }
    public NetworkStorageSummary StorageSummary { get; set; } = new();
}

internal sealed class NetworkStorageSummary
{
    public int CellCount { get; set; }
    public long CapacityUsed { get; set; }
    public long CapacityMax { get; set; }
    public int TypeSlotsUsed { get; set; }
    public int TypeSlotsMax { get; set; }
}

internal sealed class NetworkInventoryEntry
{
    public ItemKey Key { get; set; } = new();
    public Item Prototype { get; set; } = null!;
    public long TotalCount { get; set; }
    public long ReservedCount { get; set; }
    public long AvailableCount => Math.Max(0, this.TotalCount - this.ReservedCount);
    public long LastAddedSequence { get; set; }
    public List<ItemStackLocation> Locations { get; set; } = new();
}

internal sealed class ItemStackLocation
{
    public Guid EndpointId { get; set; }
    public Guid CellId { get; set; }
    public InventorySourceKind SourceKind { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public float TileX { get; set; }
    public float TileY { get; set; }
    public int SlotIndex { get; set; }
    public int Count { get; set; }
}

internal enum InventorySourceKind
{
    Chest,
    StorageCell
}

internal sealed class NetworkItemRequest
{
    public string? QualifiedItemId { get; set; }
    public int? Category { get; set; }
    public string? SerializedItemPrototype { get; set; }
    public string? PreservedParentQualifiedItemId { get; set; }
    public int Count { get; set; }

    public string DisplayKey
    {
        get
        {
            var key = this.QualifiedItemId ?? ModText.Format("inventory.request.category", this.Category?.ToString() ?? "?");
            return string.IsNullOrWhiteSpace(this.PreservedParentQualifiedItemId)
                ? key
                : ModText.Format("inventory.request.fromParent", key, this.PreservedParentQualifiedItemId);
        }
    }
}
