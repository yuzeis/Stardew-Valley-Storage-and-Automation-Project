using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Objects;
using System.Text.Json;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class TransferBusService
{
    private const string FilterKey = ModItemCatalog.UniqueId + "/TransferFilter";
    private const string FilterListKey = ModItemCatalog.UniqueId + "/TransferFilters";
    private const string FilterBlacklistKey = ModItemCatalog.UniqueId + "/TransferFilterBlacklist";
    private const string OreDictionaryModeKey = ModItemCatalog.UniqueId + "/TransferOreDictionary";
    private const string FacingDirectionKey = ModItemCatalog.UniqueId + "/TransferFacingDirection";
    private const string TickIntervalKey = ModItemCatalog.UniqueId + "/TransferTickInterval";
    private const string ItemsPerOperationKey = ModItemCatalog.UniqueId + "/TransferItemsPerOperation";
    private const string QualityStrategyKey = ModItemCatalog.UniqueId + "/TransferQualityStrategy";
    private const string MinSourceKeepKey = ModItemCatalog.UniqueId + "/TransferMinSourceKeep";
    private const string TargetKeepKey = ModItemCatalog.UniqueId + "/TransferTargetKeep";
    private const int FilterSlotCount = 9;
    private const int DefaultItemsPerOperation = 64;
    private const int MaxItemsPerOperation = 999;
    private const int DefaultTickInterval = 120;
    private const int MinTickInterval = 30;
    private const int AllDirections = -1;

    private static readonly Vector2[] AdjacentOffsets =
    {
        new Vector2(0, -1),
        new Vector2(1, 0),
        new Vector2(0, 1),
        new Vector2(-1, 0)
    };

    private readonly NetworkRepository repository;
    private readonly InventoryTransactionService transactionService;
    private readonly Func<ModConfig> getConfig;
    private readonly TickOperationBudget operationBudget;
    private readonly IMonitor monitor;

    public TransferBusService(
        NetworkRepository repository,
        InventoryTransactionService transactionService,
        Func<ModConfig> getConfig,
        TickOperationBudget operationBudget,
        IMonitor monitor)
    {
        this.repository = repository;
        this.transactionService = transactionService;
        this.getConfig = getConfig;
        this.operationBudget = operationBudget;
        this.monitor = monitor;
    }

    public bool TryHandleTransferBusAction(SObject bus)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
            return false;

        var held = Game1.player.CurrentItem;
        if (held is null)
        {
            var filter = this.FormatFilterSummary(bus);
            var quality = Enum.TryParse(bus.modData.GetValueOrDefault(QualityStrategyKey), out MaterialQualityStrategy parsedQuality)
                ? FormatQualityStrategy(parsedQuality)
                : FormatQualityStrategy(MaterialQualityStrategy.LowQualityFirst);
            var minSourceKeep = this.GetIntModData(bus, MinSourceKeepKey, 0);
            var targetKeep = this.GetIntModData(bus, TargetKeepKey, 0);
            Game1.addHUDMessage(new HUDMessage(ModText.Format("ui.transferBus.summary", filter, quality, minSourceKeep, targetKeep), HUDMessage.newQuest_type));
            return true;
        }

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.LinkTool)
            return false;

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.FilterCard)
        {
            var hasFilter = this.ReadFilterIds(bus).Count > 0;
            if (!hasFilter)
            {
                bus.modData.Remove(FilterBlacklistKey);
                this.SyncBusData(bus);
                Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.transferBus.filterAlreadyEmpty"), HUDMessage.newQuest_type));
                return true;
            }

            if (!this.GetBoolModData(bus, FilterBlacklistKey, false))
            {
                bus.modData[FilterBlacklistKey] = true.ToString();
                this.SyncBusData(bus);
                this.ConsumeHeldOne(held);
                Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.transferBus.filterModeBlacklist"), HUDMessage.newQuest_type));
                return true;
            }

            bus.modData.Remove(FilterKey);
            bus.modData.Remove(FilterListKey);
            bus.modData.Remove(FilterBlacklistKey);
            bus.modData.Remove(MinSourceKeepKey);
            bus.modData.Remove(TargetKeepKey);
            this.SyncBusData(bus);
            this.ConsumeHeldOne(held);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.transferBus.filterCleared"), HUDMessage.newQuest_type));
            return true;
        }

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.OreDictionaryCard)
        {
            var enabled = this.ToggleOreDictionaryFlag(bus);
            this.SyncBusData(bus);
            this.ConsumeHeldOne(held);
            Game1.addHUDMessage(new HUDMessage(enabled ? ModText.Get("ui.transferBus.oreDictionaryOn") : ModText.Get("ui.transferBus.oreDictionaryOff"), HUDMessage.newQuest_type));
            return true;
        }

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.SpeedCard)
        {
            var interval = this.GetIntModData(bus, TickIntervalKey, DefaultTickInterval);
            bus.modData[TickIntervalKey] = Math.Max(MinTickInterval, interval / 2).ToString();
            this.SyncBusData(bus);
            this.ConsumeHeldOne(held);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.transferBus.speedUpgraded"), HUDMessage.newQuest_type));
            return true;
        }

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.CapacityCard)
        {
            var amount = this.GetIntModData(bus, ItemsPerOperationKey, DefaultItemsPerOperation);
            bus.modData[ItemsPerOperationKey] = Math.Min(MaxItemsPerOperation, Math.Max(1, amount) * 2).ToString();
            this.SyncBusData(bus);
            this.ConsumeHeldOne(held);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.transferBus.capacityUpgraded"), HUDMessage.newQuest_type));
            return true;
        }

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.QualityCard)
        {
            var strategy = this.GetQualityStrategy(bus, MaterialQualityStrategy.LowQualityFirst) == MaterialQualityStrategy.LowQualityFirst
                ? MaterialQualityStrategy.HighQualityFirst
                : MaterialQualityStrategy.LowQualityFirst;
            bus.modData[QualityStrategyKey] = strategy.ToString();
            this.SyncBusData(bus);
            this.ConsumeHeldOne(held);
            Game1.addHUDMessage(new HUDMessage(ModText.Format("ui.transferBus.qualityChanged", FormatQualityStrategy(strategy)), HUDMessage.newQuest_type));
            return true;
        }

        if (this.ReadFilterIds(bus).Contains(held.QualifiedItemId, StringComparer.Ordinal))
        {
            var keep = Math.Max(0, held.Stack);
            if (bus.QualifiedItemId == "(BC)" + ModItemCatalog.Importer)
            {
                bus.modData[MinSourceKeepKey] = keep.ToString();
                this.SyncBusData(bus);
                Game1.addHUDMessage(new HUDMessage(ModText.Format("ui.transferBus.importerKeep", keep), HUDMessage.newQuest_type));
                return true;
            }

            bus.modData[TargetKeepKey] = keep.ToString();
            this.SyncBusData(bus);
            Game1.addHUDMessage(new HUDMessage(ModText.Format("ui.transferBus.exporterKeep", keep), HUDMessage.newQuest_type));
            return true;
        }

        this.WriteFilterIds(bus, new[] { held.QualifiedItemId });
        bus.modData.Remove(FilterBlacklistKey);
        this.SyncBusData(bus);
        Game1.addHUDMessage(new HUDMessage(ModText.Format("ui.transferBus.filterSetWhitelist", held.DisplayName), HUDMessage.newQuest_type));
        return true;
    }

    public string DescribeConfiguration(SObject bus)
    {
        var filter = this.FormatFilterSummary(bus);
        var quality = Enum.TryParse(bus.modData.GetValueOrDefault(QualityStrategyKey), out MaterialQualityStrategy parsedQuality)
            ? FormatQualityStrategy(parsedQuality)
            : FormatQualityStrategy(MaterialQualityStrategy.LowQualityFirst);
        var minSourceKeep = this.GetIntModData(bus, MinSourceKeepKey, 0);
        var targetKeep = this.GetIntModData(bus, TargetKeepKey, 0);
        return ModText.Format("ui.transferBus.summary", filter, quality, minSourceKeep, targetKeep);
    }

    public IReadOnlyList<string> DescribeConfigurationLines(SObject bus)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
            return new[] { ModText.Get("ui.transferBus.notBus") };

        var filter = this.FormatFilterSummary(bus);
        var quality = FormatQualityStrategy(this.GetQualityStrategy(bus, MaterialQualityStrategy.LowQualityFirst));
        var minSourceKeep = this.GetIntModData(bus, MinSourceKeepKey, 0);
        var targetKeep = this.GetIntModData(bus, TargetKeepKey, 0);
        var interval = this.GetIntModData(bus, TickIntervalKey, DefaultTickInterval);
        var amount = this.GetIntModData(bus, ItemsPerOperationKey, DefaultItemsPerOperation);
        var direction = FormatFacingDirection(this.GetIntModData(bus, FacingDirectionKey, AllDirections));
        var oreMode = this.GetBoolModData(bus, OreDictionaryModeKey, false) ? ModText.Get("ui.transferBus.enabled") : ModText.Get("ui.transferBus.disabled");
        return new[]
        {
            ModText.Format("ui.transferBus.type", bus.QualifiedItemId == "(BC)" + ModItemCatalog.Importer ? ModText.Get("ui.transferBus.importer") : ModText.Get("ui.transferBus.exporter")),
            ModText.Format("ui.transferBus.filter", filter),
            ModText.Format("ui.transferBus.oreDictionary", oreMode),
            ModText.Format("ui.transferBus.direction", direction),
            ModText.Format("ui.transferBus.quality", quality),
            ModText.Format("ui.transferBus.sourceKeep", minSourceKeep),
            ModText.Format("ui.transferBus.targetKeep", targetKeep),
            ModText.Format("ui.transferBus.amount", amount),
            ModText.Format("ui.transferBus.tickInterval", interval),
            ModText.Get("ui.transferBus.help")
        };
    }

    public bool TryClearFilter(SObject bus, out string message)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
        {
            message = ModText.Get("ui.transferBus.notBus");
            return false;
        }

        bus.modData.Remove(FilterKey);
        bus.modData.Remove(FilterListKey);
        bus.modData.Remove(FilterBlacklistKey);
        bus.modData.Remove(MinSourceKeepKey);
        bus.modData.Remove(TargetKeepKey);
        this.SyncBusData(bus);
        message = ModText.Get("ui.transferBus.filterCleared");
        return true;
    }

    public bool TryToggleFilterMode(SObject bus, out string message)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
        {
            message = ModText.Get("ui.transferBus.notBus");
            return false;
        }

        if (this.ReadFilterIds(bus).Count == 0)
        {
            message = ModText.Get("ui.transferBus.noFilterToToggle");
            return false;
        }

        var next = !this.GetBoolModData(bus, FilterBlacklistKey, false);
        bus.modData[FilterBlacklistKey] = next.ToString();
        this.SyncBusData(bus);
        message = next ? ModText.Get("ui.transferBus.filterModeBlacklist") : ModText.Get("ui.transferBus.filterModeWhitelist");
        return true;
    }

    public bool TryToggleQualityStrategy(SObject bus, out string message)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
        {
            message = ModText.Get("ui.transferBus.notBus");
            return false;
        }

        var strategy = this.GetQualityStrategy(bus, MaterialQualityStrategy.LowQualityFirst) == MaterialQualityStrategy.LowQualityFirst
            ? MaterialQualityStrategy.HighQualityFirst
            : MaterialQualityStrategy.LowQualityFirst;
        bus.modData[QualityStrategyKey] = strategy.ToString();
        this.SyncBusData(bus);
        message = ModText.Format("ui.transferBus.qualityChanged", FormatQualityStrategy(strategy));
        return true;
    }

    public bool TryToggleOreDictionaryMode(SObject bus, out string message)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
        {
            message = ModText.Get("ui.transferBus.notBus");
            return false;
        }

        var enabled = this.ToggleOreDictionaryFlag(bus);
        this.SyncBusData(bus);
        message = enabled ? ModText.Get("ui.transferBus.oreDictionaryOn") : ModText.Get("ui.transferBus.oreDictionaryOff");
        return true;
    }

    public bool TrySetFacingDirection(SObject bus, int facingDirection, out string message)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
        {
            message = ModText.Get("ui.transferBus.notBus");
            return false;
        }

        var normalized = NormalizeFacingDirection(facingDirection);
        bus.modData[FacingDirectionKey] = normalized.ToString();
        this.SyncBusData(bus);
        message = ModText.Format("ui.transferBus.directionChanged", FormatFacingDirection(normalized));
        return true;
    }

    public bool TrySetFilterSlot(SObject bus, int slotIndex, string qualifiedItemId, out string message)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
        {
            message = ModText.Get("ui.transferBus.notBus");
            return false;
        }

        if (slotIndex < 0 || slotIndex >= FilterSlotCount || string.IsNullOrWhiteSpace(qualifiedItemId))
        {
            message = ModText.Get("ui.transferBus.invalidFilterSlot");
            return false;
        }

        var ids = this.ReadFilterSlots(bus);
        ids[slotIndex] = qualifiedItemId;
        this.WriteFilterSlots(bus, ids);
        bus.modData.Remove(FilterBlacklistKey);
        this.SyncBusData(bus);
        message = ModText.Format("ui.transferBus.filterSlotSet", slotIndex + 1, ItemDisplayService.GetQualifiedItemDisplayName(qualifiedItemId));
        return true;
    }

    public bool TryClearFilterSlot(SObject bus, int slotIndex, out string message)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
        {
            message = ModText.Get("ui.transferBus.notBus");
            return false;
        }

        var ids = this.ReadFilterSlots(bus);
        if (slotIndex < 0 || slotIndex >= FilterSlotCount || string.IsNullOrWhiteSpace(ids[slotIndex]))
        {
            message = ModText.Get("ui.transferBus.slotAlreadyEmpty");
            return false;
        }

        ids[slotIndex] = string.Empty;
        this.WriteFilterSlots(bus, ids);
        this.SyncBusData(bus);
        message = ModText.Format("ui.transferBus.filterSlotCleared", slotIndex + 1);
        return true;
    }

    public IReadOnlyList<TransferFilterSlotView> GetFilterSlotViews(SObject bus)
    {
        var ids = this.ReadFilterSlots(bus);
        var result = new List<TransferFilterSlotView>();
        for (var index = 0; index < FilterSlotCount; index++)
        {
            if (string.IsNullOrWhiteSpace(ids[index]))
            {
                result.Add(TransferFilterSlotView.Empty(index));
                continue;
            }

            var item = CreateFilterIconItem(ids[index]);
            result.Add(new TransferFilterSlotView
            {
                SlotIndex = index,
                QualifiedItemId = ids[index],
                Item = item,
                DisplayName = item?.DisplayName ?? ids[index],
                OreGroups = item is null ? Array.Empty<string>() : OreDictionaryMatcher.GetDisplayGroups(item)
            });
        }

        return result;
    }

    public int GetFacingDirection(SObject bus)
    {
        return NormalizeFacingDirection(this.GetIntModData(bus, FacingDirectionKey, AllDirections));
    }

    public bool IsOreDictionaryModeEnabled(SObject bus)
    {
        return this.GetBoolModData(bus, OreDictionaryModeKey, false);
    }

    public StructuralActionResult ApplyConfigure(
        SObject bus,
        string heldQualifiedItemId,
        string heldDisplayName,
        int heldStack)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
            return StructuralActionResult.Fail(ModText.Get("ui.transferBus.notBus"));

        if (string.IsNullOrWhiteSpace(heldQualifiedItemId))
        {
            return new StructuralActionResult
            {
                Success = true,
                Message = this.DescribeConfiguration(bus)
            };
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.LinkTool)
            return StructuralActionResult.Fail(ModText.Get("ui.transferBus.linkToolNotUsed"));

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.FilterCard)
        {
            var hasFilter = this.ReadFilterIds(bus).Count > 0;
            if (!hasFilter)
            {
                bus.modData.Remove(FilterBlacklistKey);
                this.SyncBusData(bus);
                return Success(ModText.Get("ui.transferBus.filterAlreadyEmpty"));
            }

            if (!this.GetBoolModData(bus, FilterBlacklistKey, false))
            {
                bus.modData[FilterBlacklistKey] = true.ToString();
                this.SyncBusData(bus);
                return Success(ModText.Get("ui.transferBus.filterModeBlacklist"), consumeHeldOne: true);
            }

            bus.modData.Remove(FilterKey);
            bus.modData.Remove(FilterListKey);
            bus.modData.Remove(FilterBlacklistKey);
            bus.modData.Remove(MinSourceKeepKey);
            bus.modData.Remove(TargetKeepKey);
            this.SyncBusData(bus);
            return Success(ModText.Get("ui.transferBus.filterCleared"), consumeHeldOne: true);
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.OreDictionaryCard)
        {
            var enabled = this.ToggleOreDictionaryFlag(bus);
            this.SyncBusData(bus);
            return Success(enabled ? ModText.Get("ui.transferBus.oreDictionaryOn") : ModText.Get("ui.transferBus.oreDictionaryOff"), consumeHeldOne: true);
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.SpeedCard)
        {
            var interval = this.GetIntModData(bus, TickIntervalKey, DefaultTickInterval);
            bus.modData[TickIntervalKey] = Math.Max(MinTickInterval, interval / 2).ToString();
            this.SyncBusData(bus);
            return Success(ModText.Get("ui.transferBus.speedUpgraded"), consumeHeldOne: true);
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.CapacityCard)
        {
            var amount = this.GetIntModData(bus, ItemsPerOperationKey, DefaultItemsPerOperation);
            bus.modData[ItemsPerOperationKey] = Math.Min(MaxItemsPerOperation, Math.Max(1, amount) * 2).ToString();
            this.SyncBusData(bus);
            return Success(ModText.Get("ui.transferBus.capacityUpgraded"), consumeHeldOne: true);
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.QualityCard)
        {
            var strategy = this.GetQualityStrategy(bus, MaterialQualityStrategy.LowQualityFirst) == MaterialQualityStrategy.LowQualityFirst
                ? MaterialQualityStrategy.HighQualityFirst
                : MaterialQualityStrategy.LowQualityFirst;
            bus.modData[QualityStrategyKey] = strategy.ToString();
            this.SyncBusData(bus);
            return Success(ModText.Format("ui.transferBus.qualityChanged", FormatQualityStrategy(strategy)), consumeHeldOne: true);
        }

        if (this.ReadFilterIds(bus).Contains(heldQualifiedItemId, StringComparer.Ordinal))
        {
            var keep = Math.Max(0, heldStack);
            if (bus.QualifiedItemId == "(BC)" + ModItemCatalog.Importer)
            {
                bus.modData[MinSourceKeepKey] = keep.ToString();
                this.SyncBusData(bus);
                return Success(ModText.Format("ui.transferBus.importerKeep", keep));
            }

            bus.modData[TargetKeepKey] = keep.ToString();
            this.SyncBusData(bus);
            return Success(ModText.Format("ui.transferBus.exporterKeep", keep));
        }

        this.WriteFilterIds(bus, new[] { heldQualifiedItemId });
        bus.modData.Remove(FilterBlacklistKey);
        this.SyncBusData(bus);
        var displayName = string.IsNullOrWhiteSpace(heldDisplayName) ? heldQualifiedItemId : heldDisplayName;
        return Success(ModText.Format("ui.transferBus.filterSetWhitelist", displayName));
    }

    public StructuralActionResult ApplyToggleFilterMode(SObject bus)
    {
        return this.TryToggleFilterMode(bus, out var message)
            ? Success(message)
            : StructuralActionResult.Fail(message);
    }

    public StructuralActionResult ApplyToggleOreDictionaryMode(SObject bus)
    {
        return this.TryToggleOreDictionaryMode(bus, out var message)
            ? Success(message)
            : StructuralActionResult.Fail(message);
    }

    public StructuralActionResult ApplyToggleQualityStrategy(SObject bus)
    {
        return this.TryToggleQualityStrategy(bus, out var message)
            ? Success(message)
            : StructuralActionResult.Fail(message);
    }

    public StructuralActionResult ApplyClearFilter(SObject bus)
    {
        return this.TryClearFilter(bus, out var message)
            ? Success(message)
            : StructuralActionResult.Fail(message);
    }

    public StructuralActionResult ApplySetFacingDirection(SObject bus, int facingDirection)
    {
        return this.TrySetFacingDirection(bus, facingDirection, out var message)
            ? Success(message)
            : StructuralActionResult.Fail(message);
    }

    public StructuralActionResult ApplySetFilterSlot(SObject bus, int slotIndex, string qualifiedItemId)
    {
        return this.TrySetFilterSlot(bus, slotIndex, qualifiedItemId, out var message)
            ? Success(message)
            : StructuralActionResult.Fail(message);
    }

    public StructuralActionResult ApplyClearFilterSlot(SObject bus, int slotIndex)
    {
        return this.TryClearFilterSlot(bus, slotIndex, out var message)
            ? Success(message)
            : StructuralActionResult.Fail(message);
    }

    public void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        var maxOperations = Math.Max(1, this.getConfig().MaxOperationsPerTick);
        var operations = this.operationBudget.GetUsed(e.Ticks);

        foreach (var network in this.repository.Data.Networks.Values)
        {
            foreach (var endpoint in network.Endpoints.Where(endpoint => endpoint.Active && (endpoint.Type == EndpointType.Importer || endpoint.Type == EndpointType.Exporter)))
            {
                if (operations >= maxOperations)
                {
                    this.operationBudget.SetUsed(e.Ticks, operations, maxOperations);
                    return;
                }

                var bus = this.GetOrCreateBusData(network, endpoint);
                if (!bus.Enabled || e.Ticks % (uint)Math.Max(1, bus.TickInterval) != 0)
                    continue;

                var didWork = endpoint.Type == EndpointType.Importer
                    ? this.TryRunImporter(network, endpoint, bus)
                    : this.TryRunExporter(network, endpoint, bus);

                if (didWork)
                {
                    operations++;
                    this.operationBudget.SetUsed(e.Ticks, operations, maxOperations);
                }
            }
        }

        this.operationBudget.SetUsed(e.Ticks, operations, maxOperations);
    }

    private bool TryRunImporter(NetworkData network, NetworkEndpoint endpoint, TransferBusData bus)
    {
        var location = Game1.getLocationFromName(endpoint.LocationName);
        if (location is null)
            return false;

        foreach (var (tile, obj) in this.GetAdjacentObjects(location, endpoint, bus))
        {
            if (obj is Chest chest && this.TryImportFromChest(network, chest, bus))
                return true;

            if (this.TryImportReadyMachine(network, obj, bus))
                return true;
        }

        return false;
    }

    private bool TryRunExporter(NetworkData network, NetworkEndpoint endpoint, TransferBusData bus)
    {
        if (bus.FilterQualifiedItemIds.Count == 0)
            return false;

        var location = Game1.getLocationFromName(endpoint.LocationName);
        if (location is null)
            return false;

        foreach (var (tile, obj) in this.GetAdjacentObjects(location, endpoint, bus))
        {
            if (obj is Chest chest)
            {
                if (this.TryExportToChest(network, location, tile, chest, bus))
                    return true;

                continue;
            }

            if (this.TryExportToMachine(network, location, tile, obj, bus))
                return true;
        }

        return false;
    }

    private bool TryImportFromChest(NetworkData network, Chest chest, TransferBusData bus)
    {
        if (!ChestMutexHelper.TryAcquireImmediate(chest, out var chestLock))
            return false;

        try
        {
            var slots = Enumerable.Range(0, chest.Items.Count);
            slots = bus.QualityStrategy == MaterialQualityStrategy.HighQualityFirst
                ? slots.OrderByDescending(slot => chest.Items[slot]?.Quality ?? -1).ThenBy(slot => slot)
                : slots.OrderBy(slot => chest.Items[slot]?.Quality ?? int.MaxValue).ThenBy(slot => slot);

            foreach (var i in slots)
            {
                var source = chest.Items[i];
                if (source is null || source.Stack <= bus.MinSourceKeep || !MatchesFilter(source, bus))
                    continue;

                var movable = Math.Min(source.Stack - bus.MinSourceKeep, Math.Max(1, bus.ItemsPerOperation));
                var moving = source.getOne();
                moving.Stack = movable;

                if (!this.transactionService.TryDepositItem(network, moving, out var moved, excludedChest: chest) || moved <= 0)
                    continue;

                source.Stack -= moved;
                if (source.Stack <= 0)
                    chest.Items[i] = null;

                return true;
            }

            return false;
        }
        finally
        {
            chestLock.Release();
        }
    }

    private bool TryImportReadyMachine(NetworkData network, SObject machine, TransferBusData bus)
    {
        var held = machine.heldObject.Value;
        if (held is null || !machine.readyForHarvest.Value || !MatchesFilter(held, bus))
            return false;

        if (!this.TryDepositWholeMachineOutput(network, machine, held))
            return false;

        MachineStateHelper.ResetAfterAutomatedCollect(machine);
        return true;
    }

    private bool TryDepositWholeMachineOutput(NetworkData network, SObject machine, Item held)
    {
        var expectedCount = Math.Max(0, held.Stack);
        if (expectedCount <= 0)
            return false;

        var moving = held.getOne();
        moving.Stack = expectedCount;
        if (!this.transactionService.CanAcceptNetworkItem(network, moving, expectedCount))
            return false;

        this.transactionService.TryDepositItem(network, moving, out var moved);
        if (moved >= expectedCount)
            return true;

        if (moved > 0
            && !this.TryRollbackPartialMachineOutputDeposit(network, held, moved, out var rolledBack, out var rollbackMessage))
        {
            var unrolled = Math.Max(0, moved - rolledBack);
            if (unrolled > 0)
                TrimMachineOutputAfterPartialDeposit(machine, held, unrolled);
            this.monitor.Log($"Importer partial machine output rollback was incomplete for {held.QualifiedItemId} x{moved:N0}: {rollbackMessage}; reduced machine output by {unrolled:N0}.", LogLevel.Error);
        }

        return false;
    }

    private bool TryRollbackPartialMachineOutputDeposit(NetworkData network, Item output, int count, out int rolledBack, out string message)
    {
        rolledBack = 0;
        try
        {
            var request = new NetworkItemRequest
            {
                QualifiedItemId = output.QualifiedItemId,
                SerializedItemPrototype = SerializedItemCodec.SerializePrototype(output.getOne()),
                Count = count
            };

            if (this.transactionService.TryExtractItem(network, request, count, out var extracted, out var extractMessage)
                && extracted is not null
                && extracted.Stack >= count)
            {
                rolledBack = extracted.Stack;
                message = ModText.Get("machineOutput.rollbackSuccess");
                return true;
            }

            rolledBack = Math.Max(0, extracted?.Stack ?? 0);
            message = rolledBack > 0
                ? ModText.Format("machineOutput.rollbackPartial", rolledBack, count, extractMessage)
                : extractMessage;
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        this.monitor.Log($"Could not roll back importer partial machine output deposit for {output.QualifiedItemId} x{count:N0}: {message}", LogLevel.Error);
        return false;
    }

    private static void TrimMachineOutputAfterPartialDeposit(SObject machine, Item held, int moved)
    {
        var remaining = held.Stack - Math.Max(0, moved);
        if (remaining <= 0)
        {
            MachineStateHelper.ResetAfterAutomatedCollect(machine);
            return;
        }

        held.Stack = remaining;
    }

    private bool TryExportToChest(NetworkData network, GameLocation location, Vector2 tile, Chest chest, TransferBusData bus)
    {
        if (!ChestMutexHelper.TryAcquireImmediate(chest, out var chestLock))
            return false;

        try
        {
            var targetKeep = Math.Max(0, bus.TargetKeep);
            Item? extracted;
            var operationLimit = Math.Max(1, bus.ItemsPerOperation);
            if (!this.transactionService.TryExtractFirstMatchingItem(
                    network,
                    entry => MatchesFilter(entry.Prototype, bus)
                        && (targetKeep <= 0 || CountMatching(chest, entry.Prototype, bus) < targetKeep),
                    entry => targetKeep > 0
                        ? Math.Min(operationLimit, targetKeep - CountMatching(chest, entry.Prototype, bus))
                        : operationLimit,
                    out extracted,
                    out _,
                    bus.QualityStrategy) || extracted is null)
            {
                return false;
            }

            var before = extracted.Stack;
            var leftover = chest.addItem(extracted);
            var accepted = before - (leftover?.Stack ?? 0);
            if (leftover is not null && leftover.Stack > 0)
            {
                if (!this.transactionService.TryReturnItemToNetwork(network, leftover) || leftover.Stack > 0)
                    DropLeftover(location, tile, leftover);
            }

            return accepted > 0;
        }
        finally
        {
            chestLock.Release();
        }
    }

    private bool TryExportToMachine(NetworkData network, GameLocation location, Vector2 tile, SObject machine, TransferBusData bus)
    {
        if (machine.heldObject.Value is not null || bus.FilterQualifiedItemIds.Count == 0)
            return false;

        var count = Math.Max(1, bus.ItemsPerOperation);
        var probeMessage = string.Empty;
        Item? extracted;
        if (!this.transactionService.TryExtractFirstMatchingItem(
                network,
                entry => MatchesFilter(entry.Prototype, bus) && CanMachineAccept(machine, entry.Prototype.QualifiedItemId, count),
                _ => count,
                out extracted,
                out _,
                bus.QualityStrategy) || extracted is null)
        {
            return false;
        }

        var scratchInventory = new Inventory();
        scratchInventory.Add(extracted);
        try
        {
            if (!TryProbeMachineInput(machine, extracted.QualifiedItemId, extracted.Stack, out probeMessage))
            {
                this.ReturnScratchInventory(network, scratchInventory, location, tile);
                this.monitor.Log($"Exporter returned {extracted.QualifiedItemId}: {probeMessage}", LogLevel.Trace);
                return false;
            }

            machine.AttemptAutoLoad(scratchInventory, Game1.player);
        }
        catch (Exception ex)
        {
            this.ReturnScratchInventory(network, scratchInventory, location, tile);
            this.monitor.Log($"Exporter failed to auto-load {extracted.QualifiedItemId} into {machine.QualifiedItemId}: {ex.Message}", LogLevel.Trace);
            return false;
        }

        var accepted = machine.heldObject.Value is not null;
        this.ReturnScratchInventory(network, scratchInventory, location, tile);
        return accepted;
    }

    private static bool TryProbeMachineInput(SObject machine, string qualifiedItemId, int count, out string message)
    {
        message = string.Empty;
        Item probeItem;
        try
        {
            probeItem = ItemRegistry.Create(qualifiedItemId);
            probeItem.Stack = Math.Max(1, count);
        }
        catch (Exception ex)
        {
            message = $"could not create probe item {qualifiedItemId}: {ex.Message}";
            return false;
        }

        try
        {
            if (machine.performObjectDropInAction(probeItem, true, Game1.player, false))
                return true;
        }
        catch (Exception ex)
        {
            message = $"probe failed: {ex.Message}";
            return false;
        }

        message = $"machine rejected {probeItem.DisplayName}";
        return false;
    }

    private TransferBusData GetOrCreateBusData(NetworkData network, NetworkEndpoint endpoint)
    {
        if (!network.TransferBuses.TryGetValue(endpoint.EndpointId, out var bus))
        {
            bus = new TransferBusData
            {
                EndpointId = endpoint.EndpointId,
                Mode = endpoint.Type == EndpointType.Importer ? TransferBusMode.ImportAll : TransferBusMode.ExportFiltered,
                TickInterval = DefaultTickInterval,
                ItemsPerOperation = DefaultItemsPerOperation,
                QualityStrategy = MaterialQualityStrategy.LowQualityFirst,
                MinSourceKeep = 0,
                TargetKeep = 0,
                FilterBlacklist = false,
                FacingDirection = AllDirections
            };
            network.TransferBuses[endpoint.EndpointId] = bus;
        }

        var placed = this.FindPlacedObject(endpoint);
        if (placed is not null)
        {
            bus.FilterQualifiedItemIds = this.ReadFilterIds(placed);
            bus.FilterQualifiedItemId = bus.FilterQualifiedItemIds.FirstOrDefault();
            bus.FilterBlacklist = bus.FilterQualifiedItemIds.Count > 0 && this.GetBoolModData(placed, FilterBlacklistKey, false);
            bus.OreDictionaryMode = this.GetBoolModData(placed, OreDictionaryModeKey, false);
            bus.FacingDirection = NormalizeFacingDirection(this.GetIntModData(placed, FacingDirectionKey, bus.FacingDirection));
            bus.TickInterval = this.GetIntModData(placed, TickIntervalKey, bus.TickInterval);
            bus.ItemsPerOperation = this.GetIntModData(placed, ItemsPerOperationKey, bus.ItemsPerOperation);
            bus.QualityStrategy = this.GetQualityStrategy(placed, bus.QualityStrategy);
            bus.MinSourceKeep = this.GetIntModData(placed, MinSourceKeepKey, bus.MinSourceKeep);
            bus.TargetKeep = this.GetIntModData(placed, TargetKeepKey, bus.TargetKeep);
        }

        return bus;
    }

    private void SyncBusData(SObject busObject)
    {
        if (!Guid.TryParse(busObject.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId)
            || !Guid.TryParse(busObject.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out var endpointId)
            || !this.repository.TryGetNetwork(networkId, out var network))
        {
            return;
        }

        var endpoint = network.Endpoints.FirstOrDefault(entry => entry.EndpointId == endpointId);
        if (endpoint is null)
            return;

        var data = this.GetOrCreateBusData(network, endpoint);
        data.FilterQualifiedItemIds = this.ReadFilterIds(busObject);
        data.FilterQualifiedItemId = data.FilterQualifiedItemIds.FirstOrDefault();
        data.FilterBlacklist = data.FilterQualifiedItemIds.Count > 0 && this.GetBoolModData(busObject, FilterBlacklistKey, false);
        data.OreDictionaryMode = this.GetBoolModData(busObject, OreDictionaryModeKey, false);
        data.FacingDirection = NormalizeFacingDirection(this.GetIntModData(busObject, FacingDirectionKey, data.FacingDirection));
        data.TickInterval = this.GetIntModData(busObject, TickIntervalKey, data.TickInterval);
        data.ItemsPerOperation = this.GetIntModData(busObject, ItemsPerOperationKey, data.ItemsPerOperation);
        data.QualityStrategy = this.GetQualityStrategy(busObject, data.QualityStrategy);
        data.MinSourceKeep = this.GetIntModData(busObject, MinSourceKeepKey, data.MinSourceKeep);
        data.TargetKeep = this.GetIntModData(busObject, TargetKeepKey, data.TargetKeep);
        this.repository.Save();
    }

    private IEnumerable<(Vector2 Tile, SObject Object)> GetAdjacentObjects(GameLocation location, NetworkEndpoint endpoint, TransferBusData bus)
    {
        var origin = new Vector2(endpoint.TileX, endpoint.TileY);
        var offsets = bus.FacingDirection >= 0 && bus.FacingDirection < AdjacentOffsets.Length
            ? new[] { AdjacentOffsets[bus.FacingDirection] }
            : AdjacentOffsets;
        foreach (var offset in offsets)
        {
            var tile = origin + offset;
            if (location.objects.TryGetValue(tile, out SObject? obj))
                yield return (tile, obj);
        }
    }

    private SObject? FindPlacedObject(NetworkEndpoint endpoint)
    {
        var location = Game1.getLocationFromName(endpoint.LocationName);
        if (location is null)
            return null;

        return location.objects.TryGetValue(new Vector2(endpoint.TileX, endpoint.TileY), out SObject? obj)
            ? obj
            : null;
    }

    private static bool MatchesFilter(Item item, TransferBusData bus)
    {
        if (bus.FilterQualifiedItemIds.Count == 0)
            return true;

        var matches = bus.OreDictionaryMode
            ? OreDictionaryMatcher.IsMatch(item, bus.FilterQualifiedItemIds)
            : bus.FilterQualifiedItemIds.Contains(item.QualifiedItemId, StringComparer.Ordinal);
        return bus.FilterBlacklist ? !matches : matches;
    }

    private static int CountMatching(Chest chest, Item prototype, TransferBusData bus)
    {
        return chest.Items
            .Where(item => item is not null && MatchesTargetItem(item, prototype, bus))
            .Sum(item => item!.Stack);
    }

    private static bool MatchesTargetItem(Item item, Item prototype, TransferBusData bus)
    {
        if (!bus.OreDictionaryMode)
            return item.QualifiedItemId == prototype.QualifiedItemId;

        return OreDictionaryMatcher.IsMatch(item, new[] { prototype.QualifiedItemId });
    }

    private void ReturnScratchInventory(NetworkData network, Inventory scratchInventory, GameLocation fallbackLocation, Vector2 fallbackTile)
    {
        foreach (var leftover in scratchInventory.Where(item => item is not null && item.Stack > 0).ToList())
        {
            if (this.transactionService.TryReturnItemToNetwork(network, leftover) && leftover.Stack <= 0)
                continue;

            DropLeftover(fallbackLocation, fallbackTile, leftover);
        }

        scratchInventory.Clear();
    }

    private static void DropLeftover(GameLocation location, Vector2 tile, Item item)
    {
        Game1.createItemDebris(item, (tile + new Vector2(0.5f, 0.5f)) * Game1.tileSize, -1, location);
    }

    private static bool IsChestLocked(Chest chest)
    {
        return ChestMutexHelper.IsLockedByAnotherActor(chest);
    }

    private List<string> ReadFilterIds(SObject obj)
    {
        return this.ReadFilterSlots(obj)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(FilterSlotCount)
            .ToList();
    }

    private List<string> ReadFilterSlots(SObject obj)
    {
        if (obj.modData.TryGetValue(FilterListKey, out var rawList) && !string.IsNullOrWhiteSpace(rawList))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<List<string>>(rawList);
                if (ids is not null)
                    return NormalizeFilterSlots(ids);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Ignored unreadable SVSAP transfer filter list: {ex.Message}", LogLevel.Trace);
            }
        }

        var legacy = obj.modData.GetValueOrDefault(FilterKey);
        return string.IsNullOrWhiteSpace(legacy)
            ? NormalizeFilterSlots(Array.Empty<string>())
            : NormalizeFilterSlots(new[] { legacy });
    }

    private void WriteFilterIds(SObject obj, IEnumerable<string> ids)
    {
        var normalized = NormalizeFilterIds(ids);
        if (normalized.Count == 0)
        {
            obj.modData.Remove(FilterKey);
            obj.modData.Remove(FilterListKey);
            return;
        }

        obj.modData[FilterKey] = normalized[0];
        obj.modData[FilterListKey] = JsonSerializer.Serialize(normalized);
    }

    private void WriteFilterSlots(SObject obj, IReadOnlyList<string> ids)
    {
        var slots = NormalizeFilterSlots(ids);
        var first = slots.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        if (string.IsNullOrWhiteSpace(first))
        {
            obj.modData.Remove(FilterKey);
            obj.modData.Remove(FilterListKey);
            return;
        }

        obj.modData[FilterKey] = first;
        obj.modData[FilterListKey] = JsonSerializer.Serialize(slots);
    }

    private string FormatFilterSummary(SObject bus)
    {
        var ids = this.ReadFilterIds(bus);
        if (ids.Count == 0)
            return ModText.Get("ui.transferBus.none");

        var names = ids
            .Take(3)
            .Select(ItemDisplayService.GetQualifiedItemDisplayName)
            .ToList();
        var suffix = ids.Count > names.Count ? $" +{ids.Count - names.Count:N0}" : string.Empty;
        var mode = FormatFilterMode(this.GetBoolModData(bus, FilterBlacklistKey, false));
        var ore = this.GetBoolModData(bus, OreDictionaryModeKey, false)
            ? ModText.Get("ui.transferBus.oreDictionaryShort")
            : ModText.Get("ui.transferBus.exactShort");
        return ModText.Format("ui.transferBus.filterValue", string.Join(", ", names) + suffix, mode + "/" + ore);
    }

    private bool ToggleOreDictionaryFlag(SObject bus)
    {
        var next = !this.GetBoolModData(bus, OreDictionaryModeKey, false);
        bus.modData[OreDictionaryModeKey] = next.ToString();
        return next;
    }

    private static List<string> NormalizeFilterIds(IEnumerable<string> ids)
    {
        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(FilterSlotCount)
            .ToList();
    }

    private static List<string> NormalizeFilterSlots(IEnumerable<string> ids)
    {
        var result = ids
            .Take(FilterSlotCount)
            .Select(id => string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim())
            .ToList();
        while (result.Count < FilterSlotCount)
            result.Add(string.Empty);

        return result;
    }

    private static int NormalizeFacingDirection(int facingDirection)
    {
        return facingDirection is >= 0 and <= 3 ? facingDirection : AllDirections;
    }

    private static string FormatFacingDirection(int facingDirection)
    {
        return NormalizeFacingDirection(facingDirection) switch
        {
            0 => ModText.Get("ui.transferBus.direction.up"),
            1 => ModText.Get("ui.transferBus.direction.right"),
            2 => ModText.Get("ui.transferBus.direction.down"),
            3 => ModText.Get("ui.transferBus.direction.left"),
            _ => ModText.Get("ui.transferBus.direction.all")
        };
    }

    private static Item? CreateFilterIconItem(string qualifiedItemId)
    {
        try
        {
            return ItemRegistry.Create(qualifiedItemId);
        }
        catch
        {
            return null;
        }
    }

    private int GetIntModData(SObject obj, string key, int fallback)
    {
        return obj.modData.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
            ? parsed
            : fallback;
    }

    private bool GetBoolModData(SObject obj, string key, bool fallback)
    {
        return obj.modData.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed)
            ? parsed
            : fallback;
    }

    private MaterialQualityStrategy GetQualityStrategy(SObject obj, MaterialQualityStrategy fallback)
    {
        return obj.modData.TryGetValue(QualityStrategyKey, out var raw)
            && Enum.TryParse(raw, out MaterialQualityStrategy parsed)
            ? parsed
            : fallback;
    }

    private static string FormatQualityStrategy(MaterialQualityStrategy strategy)
    {
        return strategy switch
        {
            MaterialQualityStrategy.HighQualityFirst => ModText.Get("ui.transferBus.quality.highFirst"),
            MaterialQualityStrategy.PreserveGoldIridium => ModText.Get("ui.transferBus.quality.preserveGoldIridium"),
            _ => ModText.Get("ui.transferBus.quality.lowFirst")
        };
    }

    private static string FormatFilterMode(bool blacklist)
    {
        return blacklist
            ? ModText.Get("ui.transferBus.mode.blacklist")
            : ModText.Get("ui.transferBus.mode.whitelist");
    }

    private static bool CanMachineAccept(SObject machine, string qualifiedItemId, int count)
    {
        return TryProbeMachineInput(machine, qualifiedItemId, count, out _);
    }

    private void ConsumeHeldOne(Item held)
    {
        held.Stack -= 1;
        if (held.Stack <= 0)
            Game1.player.removeItemFromInventory(held);
    }

    private static StructuralActionResult Success(string message, bool consumeHeldOne = false)
    {
        return new StructuralActionResult
        {
            Success = true,
            Message = message,
            ConsumeHeldOne = consumeHeldOne
        };
    }
}

internal sealed class TransferFilterSlotView
{
    public int SlotIndex { get; set; }
    public string QualifiedItemId { get; set; } = string.Empty;
    public Item? Item { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public IReadOnlyList<string> OreGroups { get; set; } = Array.Empty<string>();
    public bool Occupied => this.Item is not null || !string.IsNullOrWhiteSpace(this.QualifiedItemId);

    public static TransferFilterSlotView Empty(int slotIndex)
    {
        return new TransferFilterSlotView { SlotIndex = slotIndex };
    }
}
