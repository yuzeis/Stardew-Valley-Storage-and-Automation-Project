namespace SVSAP;

internal sealed class ModConfig
{
    public string Language { get; set; } = ModText.Chinese;
    public bool EnableSimpleWirelessWithinFarm { get; set; } = true;
    public bool RequireCables { get; set; }
    public bool EnableAutocrafting { get; set; } = true;
    public bool EnableProcessingPatterns { get; set; } = true;
    public bool RequireConfirmForLongCpuJobs { get; set; } = true;
    public int ReserveFastSlots { get; set; } = 1;
    public int LongJobThresholdMinutes { get; set; } = 1600;
    public int CaskTargetQuality { get; set; } = 4;
    public bool AllowToolsInNetwork { get; set; }
    public bool AllowWeaponsInNetwork { get; set; }
    public int MaxEndpointsPerNetwork { get; set; } = 200;
    public int MaxStorageCellsPerNetwork { get; set; } = 64;
    public int MaxItemTypesPerStorageCell { get; set; } = 63;
    public bool PreferStorageCellsForDeposits { get; set; } = true;
    public int MaxOperationsPerTick { get; set; } = 20;
    public bool DetailedGameplayLogs { get; set; } = true;
    public bool DebugTransactionLogs { get; set; }
    public bool CasualRecipeCosts { get; set; }
}
