namespace SVSAP.Models;

internal static class MultiplayerMessageTypes
{
    public const string TerminalSnapshotRequest = "TerminalSnapshotRequest";
    public const string TerminalSnapshotResponse = "TerminalSnapshotResponse";
    public const string TerminalActionRequest = "TerminalActionRequest";
    public const string TerminalActionResponse = "TerminalActionResponse";
    public const string CraftingSnapshotRequest = "CraftingSnapshotRequest";
    public const string CraftingSnapshotResponse = "CraftingSnapshotResponse";
    public const string CraftingActionRequest = "CraftingActionRequest";
    public const string CraftingActionResponse = "CraftingActionResponse";
    public const string CraftingMonitorSnapshotRequest = "CraftingMonitorSnapshotRequest";
    public const string CraftingMonitorSnapshotResponse = "CraftingMonitorSnapshotResponse";
    public const string CraftingMonitorActionRequest = "CraftingMonitorActionRequest";
    public const string CraftingMonitorActionResponse = "CraftingMonitorActionResponse";
    public const string StructuralActionRequest = "StructuralActionRequest";
    public const string StructuralActionResponse = "StructuralActionResponse";
}

internal enum TerminalActionKind
{
    Withdraw,
    DepositSlot,
    DepositSame,
    DepositAll,
    ToggleHeldItemLock
}

internal enum CraftingMonitorActionKind
{
    CancelJob,
    UpdatePipeline,
    QueueJob,
    TogglePipeline,
    ToggleCaskPipeline
}

internal enum StructuralActionKind
{
    LinkSelectCore,
    LinkBindEndpoint,
    StorageDriveInteract,
    PatternProviderInteract,
    TransferBusConfigure
}

internal sealed class TerminalSnapshotRequestMessage
{
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public bool Crafting { get; set; }
    public int EntryOffset { get; set; }
    public int EntryLimit { get; set; }
}

internal sealed class TerminalSnapshotResponseMessage
{
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public bool Crafting { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public int SourceCount { get; set; }
    public int TotalEntryCount { get; set; }
    public int EntryOffset { get; set; }
    public int EntryLimit { get; set; }
    public bool Truncated { get; set; }
    public NetworkStorageSummary StorageSummary { get; set; } = new();
    public List<string> LockedQualifiedItemIds { get; set; } = new();
    public List<RemoteInventoryEntryMessage> Entries { get; set; } = new();
}

internal sealed class RemoteInventoryEntryMessage
{
    public ItemKey Key { get; set; } = new();
    public string QualifiedItemId { get; set; } = string.Empty;
    public string SerializedItemPrototype { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public TerminalInventoryCategory Category { get; set; }
    public int Quality { get; set; }
    public int SalePrice { get; set; }
    public long TotalCount { get; set; }
    public long ReservedCount { get; set; }
    public long AvailableCount { get; set; }
    public long LastAddedSequence { get; set; }
    public int StackCount { get; set; }
    public List<RemoteItemStackLocationMessage> Locations { get; set; } = new();
}

internal sealed class RemoteItemStackLocationMessage
{
    public InventorySourceKind SourceKind { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public float TileX { get; set; }
    public float TileY { get; set; }
    public int SlotIndex { get; set; }
    public int Count { get; set; }
}

internal sealed class TerminalActionRequestMessage
{
    public Guid TransactionId { get; set; }
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public TerminalActionKind Action { get; set; }
    public ItemKey? ItemKey { get; set; }
    public int Amount { get; set; }
    public int InventorySlotIndex { get; set; } = -1;
    public bool DepositSingle { get; set; }
    public string HeldQualifiedItemId { get; set; } = string.Empty;
    public string HeldDisplayName { get; set; } = string.Empty;
    public List<TerminalItemPayloadMessage> DepositItems { get; set; } = new();
}

internal sealed class TerminalActionResponseMessage
{
    public Guid TransactionId { get; set; }
    public Guid NetworkId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ReturnedSerializedItem { get; set; } = string.Empty;
    public int ReturnedCount { get; set; }
    public List<TerminalItemPayloadMessage> ReturnedDepositItems { get; set; } = new();
    public TerminalSnapshotResponseMessage? Snapshot { get; set; }
}

internal sealed class TerminalItemPayloadMessage
{
    public string SerializedItem { get; set; } = string.Empty;
    public int Count { get; set; }
}

internal sealed class CraftingSnapshotRequestMessage
{
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public int Batches { get; set; } = 1;
    public MaterialQualityStrategy QualityStrategy { get; set; } = MaterialQualityStrategy.LowQualityFirst;
}

internal sealed class CraftingSnapshotResponseMessage
{
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public int NetworkItemTypes { get; set; }
    public int Batches { get; set; } = 1;
    public MaterialQualityStrategy QualityStrategy { get; set; } = MaterialQualityStrategy.LowQualityFirst;
    public List<RemoteCraftingRecipeMessage> Recipes { get; set; } = new();
}

internal sealed class RemoteCraftingRecipeMessage
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string OutputQualifiedItemId { get; set; } = string.Empty;
    public string OutputSerializedItemPrototype { get; set; } = string.Empty;
    public int OutputCount { get; set; }
    public bool CanCraft { get; set; }
    public List<string> MissingLines { get; set; } = new();
    public List<CraftingMissingIngredient> MissingIngredients { get; set; } = new();
}

internal sealed class CraftingActionRequestMessage
{
    public Guid TransactionId { get; set; }
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public string RecipeName { get; set; } = string.Empty;
    public int Batches { get; set; } = 1;
    public MaterialQualityStrategy QualityStrategy { get; set; } = MaterialQualityStrategy.LowQualityFirst;
}

internal sealed class CraftingActionResponseMessage
{
    public Guid TransactionId { get; set; }
    public Guid NetworkId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public CraftingSnapshotResponseMessage? Snapshot { get; set; }
}

internal sealed class CraftingMonitorSnapshotRequestMessage
{
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public PatternData? HeldPattern { get; set; }
    public string HeldCaskItemPrototype { get; set; } = string.Empty;
}

internal sealed class CraftingMonitorSnapshotResponseMessage
{
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string NetworkName { get; set; } = string.Empty;
    public PatternData? QueuePattern { get; set; }
    public string CaskPipelineItemPrototype { get; set; } = string.Empty;
    public string CaskPipelineItemDisplayName { get; set; } = string.Empty;
    public List<RemoteCraftingJobMessage> Jobs { get; set; } = new();
    public List<RemoteProductionPipelineMessage> Pipelines { get; set; } = new();
}

internal sealed class RemoteCraftingJobMessage
{
    public Guid JobId { get; set; }
    public PatternData? Pattern { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public CraftingJobState State { get; set; }
    public int RequestedCount { get; set; }
    public int CompletedCount { get; set; }
    public int NodeCount { get; set; }
    public string CpuSlotLabel { get; set; } = string.Empty;
    public int ReservedCount { get; set; }
    public int RemainingReservedCount { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public bool CanCancel { get; set; }
}

internal sealed class RemoteProductionPipelineMessage
{
    public Guid PipelineId { get; set; }
    public bool Enabled { get; set; }
    public int Priority { get; set; }
    public ProductionPipelineMode Mode { get; set; }
    public PatternData? Pattern { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int TargetKeep { get; set; }
    public int ItemsPerCycle { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

internal sealed class CraftingMonitorActionRequestMessage
{
    public Guid TransactionId { get; set; }
    public Guid NetworkId { get; set; }
    public Guid EndpointId { get; set; }
    public CraftingMonitorActionKind Action { get; set; }
    public Guid? JobId { get; set; }
    public Guid? PipelineId { get; set; }
    public string PipelineAction { get; set; } = string.Empty;
    public PatternData? QueuePattern { get; set; }
    public string CaskPipelineItemPrototype { get; set; } = string.Empty;
    public int Batches { get; set; } = 1;
    public bool ConfirmLongJob { get; set; }
}

internal sealed class CraftingMonitorActionResponseMessage
{
    public Guid TransactionId { get; set; }
    public Guid NetworkId { get; set; }
    public bool Success { get; set; }
    public bool RequiresConfirmation { get; set; }
    public string Message { get; set; } = string.Empty;
    public CraftingMonitorSnapshotResponseMessage? Snapshot { get; set; }
}

internal sealed class StructuralActionRequestMessage
{
    public Guid TransactionId { get; set; }
    public StructuralActionKind Kind { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int TileX { get; set; }
    public int TileY { get; set; }
    public Guid SelectedNetworkId { get; set; }
    public string HeldQualifiedItemId { get; set; } = string.Empty;
    public string HeldDisplayName { get; set; } = string.Empty;
    public int HeldStack { get; set; }
    public string HeldSerializedItem { get; set; } = string.Empty;
}

internal sealed class StructuralActionResponseMessage
{
    public Guid TransactionId { get; set; }
    public StructuralActionKind Kind { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool ConsumeHeldOne { get; set; }
    public string ReturnedSerializedItem { get; set; } = string.Empty;
    public Guid ResultNetworkId { get; set; }
}
