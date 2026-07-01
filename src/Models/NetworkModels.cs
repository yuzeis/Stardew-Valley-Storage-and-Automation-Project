namespace SVSAP.Models;

internal sealed class NetworkSaveData
{
    public Dictionary<Guid, NetworkData> Networks { get; set; } = new();
    public List<TxLogRecord> PendingTransactions { get; set; } = new();
}

internal sealed class NetworkData
{
    public Guid NetworkId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<NetworkEndpoint> Endpoints { get; set; } = new();
    public Dictionary<Guid, StorageDriveData> StorageDrives { get; set; } = new();
    public Dictionary<Guid, TransferBusData> TransferBuses { get; set; } = new();
    public Dictionary<Guid, PatternProviderData> PatternProviders { get; set; } = new();
    public Dictionary<Guid, ProductionPipelineData> ProductionPipelines { get; set; } = new();
    public List<CraftingJob> Jobs { get; set; } = new();
    public List<string> LockedQualifiedItemIds { get; set; } = new();
    public long RecentSequence { get; set; }
    public List<NetworkRecentItemData> RecentItems { get; set; } = new();
}

internal sealed class NetworkRecentItemData
{
    public ItemKey Key { get; set; } = new();
    public long LastAddedSequence { get; set; }
}

internal sealed class NetworkEndpoint
{
    public Guid EndpointId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public float TileX { get; set; }
    public float TileY { get; set; }
    public EndpointType Type { get; set; }
    public int Priority { get; set; }
    public bool Active { get; set; } = true;
}

internal sealed class StorageDriveData
{
    public Guid EndpointId { get; set; }
    public List<Guid> InsertedCellIds { get; set; } = new();
    public List<StorageDriveSlotData> Slots { get; set; } = new();
    public int Priority { get; set; }
}

internal sealed class StorageDriveSlotData
{
    public int SlotIndex { get; set; }
    public Guid CellId { get; set; }
    public string QualifiedItemId { get; set; } = string.Empty;
    public Dictionary<string, string> ModData { get; set; } = new();
}

internal sealed class TransferBusData
{
    public Guid EndpointId { get; set; }
    public TransferBusMode Mode { get; set; }
    public string? FilterQualifiedItemId { get; set; }
    public bool FilterBlacklist { get; set; }
    public int MinSourceKeep { get; set; }
    public int TargetKeep { get; set; }
    public int ItemsPerOperation { get; set; } = 64;
    public int TickInterval { get; set; } = 120;
    public MaterialQualityStrategy QualityStrategy { get; set; } = MaterialQualityStrategy.LowQualityFirst;
    public bool Enabled { get; set; } = true;
}

internal sealed class TxLogRecord
{
    public Guid TxId { get; set; }
    public Guid NetworkId { get; set; }
    public TxKind Kind { get; set; }
    public TxState State { get; set; }
    public TxApplyPhase ApplyPhase { get; set; } = TxApplyPhase.Unknown;
    public string SerializedItem { get; set; } = string.Empty;
    public int Count { get; set; }
    public string SourceRef { get; set; } = string.Empty;
    public Guid TargetCellId { get; set; }
    public string CellDataBefore { get; set; } = string.Empty;
    public string CellDataAfter { get; set; } = string.Empty;
}

internal sealed class CraftingJob
{
    public Guid JobId { get; set; }
    public string TargetQualifiedItemId { get; set; } = string.Empty;
    public int RequestedCount { get; set; }
    public int CompletedCount { get; set; }
    public CraftingJobState State { get; set; }
    public PatternData Pattern { get; set; } = new();
    public List<CraftingJobStep> Steps { get; set; } = new();
    public List<CraftingReservation> Reservations { get; set; } = new();
    public int CurrentStepIndex { get; set; }
    public int NodeCount { get; set; }
    public Guid? AssignedCpuEndpointId { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string WaitingMachineLocationName { get; set; } = string.Empty;
    public float WaitingMachineTileX { get; set; }
    public float WaitingMachineTileY { get; set; }
    public long CreatedTick { get; set; }
    public bool IsLongRunning { get; set; }
}

internal sealed class CraftingJobStep
{
    public int StepIndex { get; set; }
    public PatternData Pattern { get; set; } = new();
    public int RequestedBatches { get; set; }
    public int CompletedBatches { get; set; }
    public CraftingJobState State { get; set; } = CraftingJobState.Planning;
}

internal sealed class CraftingReservation
{
    public NetworkItemRequest Request { get; set; } = new();
    public int Count { get; set; }
    public int ConsumedCount { get; set; }
}

internal sealed class ProductionPipelineData
{
    public Guid PipelineId { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public ProductionPipelineMode Mode { get; set; } = ProductionPipelineMode.StandardProcessing;
    public PatternData Pattern { get; set; } = new();
    public string MachineQualifiedItemId { get; set; } = string.Empty;
    public int ItemsPerCycle { get; set; } = 1;
    public int TargetKeep { get; set; }
    public int TickInterval { get; set; } = 240;
    public long LastRunTick { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

internal enum EndpointType
{
    NetworkCore,
    NetworkTerminal,
    StorageInterface,
    StorageDrive,
    Importer,
    Exporter,
    MachineInterface,
    PatternTerminal,
    PatternProvider,
    MolecularAssembler,
    CraftingCpuCore,
    CraftingMatrix1K,
    CraftingMatrix4K,
    CraftingMatrix16K,
    CraftingMatrix64K,
    CoProcessor,
    CraftingMonitor,
    Chest,
    Machine
}

internal enum TransferBusMode
{
    ImportAll,
    ExportFiltered,
    KeepStock
}

internal enum ProductionPipelineMode
{
    StandardProcessing,
    CaskAging
}

internal enum TxKind
{
    CellDeposit,
    CellWithdraw
}

internal enum TxState
{
    Prepared,
    Committed
}

internal enum TxApplyPhase
{
    Unknown,
    Prepared,
    CellApplied,
    CounterpartApplied
}

internal enum CraftingJobState
{
    Planning,
    MissingItems,
    Reserved,
    Running,
    WaitingForMachine,
    WaitingForOutput,
    Completed,
    Failed,
    Cancelled
}

internal sealed class StructuralActionResult
{
    public bool Success;
    public string Message = string.Empty;
    public bool ConsumeHeldOne;
    public string ReturnedSerializedItem = string.Empty;
    public Guid ResultNetworkId;

    public static StructuralActionResult Fail(string message)
    {
        return new StructuralActionResult
        {
            Success = false,
            Message = message
        };
    }
}
