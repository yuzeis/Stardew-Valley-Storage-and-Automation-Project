namespace SVSAP;

internal static class RecipeCostModes
{
    public const string Normal = "Normal";
    public const string Casual = "Casual";
    public const string Debug = "Debug";

    public static readonly string[] All = { Normal, Casual, Debug };

    public static string Normalize(string? value, bool casualFallback = false)
    {
        if (string.Equals(value, Debug, StringComparison.OrdinalIgnoreCase))
            return Debug;

        if (string.Equals(value, Casual, StringComparison.OrdinalIgnoreCase))
            return Casual;

        return casualFallback ? Casual : Normal;
    }
}

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
    public bool DetailedGameplayLogs { get; set; }
    public bool DebugTransactionLogs { get; set; }
    public bool CasualRecipeCosts { get; set; }
    public string? RecipeCostMode { get; set; }

    public string GetRecipeCostMode()
    {
        return RecipeCostModes.Normalize(this.RecipeCostMode, this.CasualRecipeCosts);
    }

    public bool IsDebugRecipeCostMode()
    {
        return this.GetRecipeCostMode() == RecipeCostModes.Debug;
    }

    public void NormalizeRecipeCostMode()
    {
        this.RecipeCostMode = this.GetRecipeCostMode();
        this.CasualRecipeCosts = this.RecipeCostMode == RecipeCostModes.Casual;
    }
}
