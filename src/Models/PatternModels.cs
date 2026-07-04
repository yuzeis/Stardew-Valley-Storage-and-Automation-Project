namespace SVSAP.Models;

internal sealed class PatternData
{
    public Guid PatternId { get; set; } = Guid.NewGuid();
    public PatternKind Kind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayNameKey { get; set; } = string.Empty;
    public List<string> DisplayNameArguments { get; set; } = new();
    public List<NetworkItemRequest> Inputs { get; set; } = new();
    public List<NetworkItemRequest> Outputs { get; set; } = new();
    public string? MachineQualifiedItemId { get; set; }
    public int ProcessingMinutes { get; set; }
    public ProcessingSpeedClass SpeedClass { get; set; }
}

internal sealed class PatternProviderData
{
    public Guid EndpointId { get; set; }
    public List<PatternSlotData> Slots { get; set; } = new();
}

internal sealed class PatternSlotData
{
    public int SlotIndex { get; set; }
    public string QualifiedItemId { get; set; } = string.Empty;
    public Dictionary<string, string> ModData { get; set; } = new();
}

internal enum PatternKind
{
    Crafting,
    Processing
}

internal enum ProcessingSpeedClass
{
    Fast,
    Medium,
    Slow
}
