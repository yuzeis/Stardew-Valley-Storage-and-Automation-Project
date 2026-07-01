using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class TransferBusService
{
    private const string FilterKey = ModItemCatalog.UniqueId + "/TransferFilter";
    private const string FilterBlacklistKey = ModItemCatalog.UniqueId + "/TransferFilterBlacklist";
    private const string TickIntervalKey = ModItemCatalog.UniqueId + "/TransferTickInterval";
    private const string ItemsPerOperationKey = ModItemCatalog.UniqueId + "/TransferItemsPerOperation";
    private const string QualityStrategyKey = ModItemCatalog.UniqueId + "/TransferQualityStrategy";
    private const string MinSourceKeepKey = ModItemCatalog.UniqueId + "/TransferMinSourceKeep";
    private const string TargetKeepKey = ModItemCatalog.UniqueId + "/TransferTargetKeep";
    private const int DefaultItemsPerOperation = 64;
    private const int MaxItemsPerOperation = 999;
    private const int DefaultTickInterval = 120;
    private const int MinTickInterval = 30;

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
            var rawFilter = bus.modData.GetValueOrDefault(FilterKey);
            var filter = string.IsNullOrWhiteSpace(rawFilter)
                ? "无"
                : $"{rawFilter} ({(this.GetBoolModData(bus, FilterBlacklistKey, false) ? "黑名单" : "白名单")})";
            var quality = Enum.TryParse(bus.modData.GetValueOrDefault(QualityStrategyKey), out MaterialQualityStrategy parsedQuality)
                ? FormatQualityStrategy(parsedQuality)
                : FormatQualityStrategy(MaterialQualityStrategy.LowQualityFirst);
            var minSourceKeep = this.GetIntModData(bus, MinSourceKeepKey, 0);
            var targetKeep = this.GetIntModData(bus, TargetKeepKey, 0);
            Game1.addHUDMessage(new HUDMessage($"搬运过滤：{filter}；质量：{quality}；保留 {minSourceKeep:N0}/{targetKeep:N0}", HUDMessage.newQuest_type));
            return true;
        }

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.LinkTool)
            return false;

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.FilterCard)
        {
            var hasFilter = !string.IsNullOrWhiteSpace(bus.modData.GetValueOrDefault(FilterKey));
            if (!hasFilter)
            {
                bus.modData.Remove(FilterBlacklistKey);
                this.SyncBusData(bus);
                Game1.addHUDMessage(new HUDMessage("搬运过滤已经为空。", HUDMessage.newQuest_type));
                return true;
            }

            if (!this.GetBoolModData(bus, FilterBlacklistKey, false))
            {
                bus.modData[FilterBlacklistKey] = true.ToString();
                this.SyncBusData(bus);
                this.ConsumeHeldOne(held);
                Game1.addHUDMessage(new HUDMessage("搬运过滤模式：黑名单。", HUDMessage.newQuest_type));
                return true;
            }

            bus.modData.Remove(FilterKey);
            bus.modData.Remove(FilterBlacklistKey);
            bus.modData.Remove(MinSourceKeepKey);
            bus.modData.Remove(TargetKeepKey);
            this.SyncBusData(bus);
            this.ConsumeHeldOne(held);
            Game1.addHUDMessage(new HUDMessage("搬运过滤已清空。", HUDMessage.newQuest_type));
            return true;
        }

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.SpeedCard)
        {
            var interval = this.GetIntModData(bus, TickIntervalKey, DefaultTickInterval);
            bus.modData[TickIntervalKey] = Math.Max(MinTickInterval, interval / 2).ToString();
            this.SyncBusData(bus);
            this.ConsumeHeldOne(held);
            Game1.addHUDMessage(new HUDMessage("搬运速度已升级。", HUDMessage.newQuest_type));
            return true;
        }

        if (held.QualifiedItemId == "(O)" + ModItemCatalog.CapacityCard)
        {
            var amount = this.GetIntModData(bus, ItemsPerOperationKey, DefaultItemsPerOperation);
            bus.modData[ItemsPerOperationKey] = Math.Min(MaxItemsPerOperation, Math.Max(1, amount) * 2).ToString();
            this.SyncBusData(bus);
            this.ConsumeHeldOne(held);
            Game1.addHUDMessage(new HUDMessage("搬运容量已升级。", HUDMessage.newQuest_type));
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
            Game1.addHUDMessage(new HUDMessage($"搬运质量策略：{FormatQualityStrategy(strategy)}。", HUDMessage.newQuest_type));
            return true;
        }

        if (bus.modData.GetValueOrDefault(FilterKey) == held.QualifiedItemId)
        {
            var keep = Math.Max(0, held.Stack);
            if (bus.QualifiedItemId == "(BC)" + ModItemCatalog.Importer)
            {
                bus.modData[MinSourceKeepKey] = keep.ToString();
                this.SyncBusData(bus);
                Game1.addHUDMessage(new HUDMessage($"导入总线会在来源保留 {keep:N0} 个。", HUDMessage.newQuest_type));
                return true;
            }

            bus.modData[TargetKeepKey] = keep.ToString();
            this.SyncBusData(bus);
            Game1.addHUDMessage(new HUDMessage($"导出总线会把目标维持在 {keep:N0} 个。", HUDMessage.newQuest_type));
            return true;
        }

        bus.modData[FilterKey] = held.QualifiedItemId;
        bus.modData.Remove(FilterBlacklistKey);
        this.SyncBusData(bus);
        Game1.addHUDMessage(new HUDMessage($"搬运过滤已设为 {held.DisplayName}（白名单）。", HUDMessage.newQuest_type));
        return true;
    }

    public string DescribeConfiguration(SObject bus)
    {
        var rawFilter = bus.modData.GetValueOrDefault(FilterKey);
        var filter = string.IsNullOrWhiteSpace(rawFilter)
            ? "无"
            : $"{rawFilter} ({(this.GetBoolModData(bus, FilterBlacklistKey, false) ? "黑名单" : "白名单")})";
        var quality = Enum.TryParse(bus.modData.GetValueOrDefault(QualityStrategyKey), out MaterialQualityStrategy parsedQuality)
            ? FormatQualityStrategy(parsedQuality)
            : FormatQualityStrategy(MaterialQualityStrategy.LowQualityFirst);
        var minSourceKeep = this.GetIntModData(bus, MinSourceKeepKey, 0);
        var targetKeep = this.GetIntModData(bus, TargetKeepKey, 0);
        return $"搬运过滤：{filter}；质量：{quality}；保留 {minSourceKeep:N0}/{targetKeep:N0}";
    }

    public StructuralActionResult ApplyConfigure(
        SObject bus,
        string heldQualifiedItemId,
        string heldDisplayName,
        int heldStack)
    {
        if (bus.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
            return StructuralActionResult.Fail("目标不是搬运总线。");

        if (string.IsNullOrWhiteSpace(heldQualifiedItemId))
        {
            return new StructuralActionResult
            {
                Success = true,
                Message = this.DescribeConfiguration(bus)
            };
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.LinkTool)
            return StructuralActionResult.Fail("链接工具不用于配置搬运总线。");

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.FilterCard)
        {
            var hasFilter = !string.IsNullOrWhiteSpace(bus.modData.GetValueOrDefault(FilterKey));
            if (!hasFilter)
            {
                bus.modData.Remove(FilterBlacklistKey);
                this.SyncBusData(bus);
                return Success("搬运过滤已经为空。");
            }

            if (!this.GetBoolModData(bus, FilterBlacklistKey, false))
            {
                bus.modData[FilterBlacklistKey] = true.ToString();
                this.SyncBusData(bus);
                return Success("搬运过滤模式：黑名单。", consumeHeldOne: true);
            }

            bus.modData.Remove(FilterKey);
            bus.modData.Remove(FilterBlacklistKey);
            bus.modData.Remove(MinSourceKeepKey);
            bus.modData.Remove(TargetKeepKey);
            this.SyncBusData(bus);
            return Success("搬运过滤已清空。", consumeHeldOne: true);
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.SpeedCard)
        {
            var interval = this.GetIntModData(bus, TickIntervalKey, DefaultTickInterval);
            bus.modData[TickIntervalKey] = Math.Max(MinTickInterval, interval / 2).ToString();
            this.SyncBusData(bus);
            return Success("搬运速度已升级。", consumeHeldOne: true);
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.CapacityCard)
        {
            var amount = this.GetIntModData(bus, ItemsPerOperationKey, DefaultItemsPerOperation);
            bus.modData[ItemsPerOperationKey] = Math.Min(MaxItemsPerOperation, Math.Max(1, amount) * 2).ToString();
            this.SyncBusData(bus);
            return Success("搬运容量已升级。", consumeHeldOne: true);
        }

        if (heldQualifiedItemId == "(O)" + ModItemCatalog.QualityCard)
        {
            var strategy = this.GetQualityStrategy(bus, MaterialQualityStrategy.LowQualityFirst) == MaterialQualityStrategy.LowQualityFirst
                ? MaterialQualityStrategy.HighQualityFirst
                : MaterialQualityStrategy.LowQualityFirst;
            bus.modData[QualityStrategyKey] = strategy.ToString();
            this.SyncBusData(bus);
            return Success($"搬运质量策略：{FormatQualityStrategy(strategy)}。", consumeHeldOne: true);
        }

        if (bus.modData.GetValueOrDefault(FilterKey) == heldQualifiedItemId)
        {
            var keep = Math.Max(0, heldStack);
            if (bus.QualifiedItemId == "(BC)" + ModItemCatalog.Importer)
            {
                bus.modData[MinSourceKeepKey] = keep.ToString();
                this.SyncBusData(bus);
                return Success($"导入总线会在来源保留 {keep:N0} 个。");
            }

            bus.modData[TargetKeepKey] = keep.ToString();
            this.SyncBusData(bus);
            return Success($"导出总线会把目标维持在 {keep:N0} 个。");
        }

        bus.modData[FilterKey] = heldQualifiedItemId;
        bus.modData.Remove(FilterBlacklistKey);
        this.SyncBusData(bus);
        var displayName = string.IsNullOrWhiteSpace(heldDisplayName) ? heldQualifiedItemId : heldDisplayName;
        return Success($"搬运过滤已设为 {displayName}（白名单）。");
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

        foreach (var (tile, obj) in this.GetAdjacentObjects(location, endpoint))
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
        if (string.IsNullOrWhiteSpace(bus.FilterQualifiedItemId))
            return false;

        var location = Game1.getLocationFromName(endpoint.LocationName);
        if (location is null)
            return false;

        foreach (var (tile, obj) in this.GetAdjacentObjects(location, endpoint))
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
                message = "已回滚部分机器产物存入。";
                return true;
            }

            rolledBack = Math.Max(0, extracted?.Stack ?? 0);
            message = rolledBack > 0
                ? $"只回滚了 {rolledBack:N0}/{count:N0}；{extractMessage}"
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
            if (bus.FilterBlacklist)
            {
                var operationLimit = Math.Max(1, bus.ItemsPerOperation);
                if (!this.transactionService.TryExtractFirstMatchingItem(
                        network,
                        entry => MatchesFilter(entry.Prototype, bus) && (targetKeep <= 0 || CountMatching(chest, entry.Key.QualifiedItemId) < targetKeep),
                        entry => targetKeep > 0
                            ? Math.Min(operationLimit, targetKeep - CountMatching(chest, entry.Key.QualifiedItemId))
                            : operationLimit,
                        out extracted,
                        out _,
                        bus.QualityStrategy) || extracted is null)
                {
                    return false;
                }
            }
            else
            {
                if (targetKeep > 0 && CountMatching(chest, bus.FilterQualifiedItemId!) >= targetKeep)
                    return false;

                var count = targetKeep > 0
                    ? Math.Min(Math.Max(1, bus.ItemsPerOperation), targetKeep - CountMatching(chest, bus.FilterQualifiedItemId!))
                    : Math.Max(1, bus.ItemsPerOperation);

                var request = new NetworkItemRequest
                {
                    QualifiedItemId = bus.FilterQualifiedItemId,
                    Count = count
                };

                if (!this.transactionService.TryExtractItem(network, request, count, out extracted, out _, qualityStrategy: bus.QualityStrategy) || extracted is null)
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
        if (machine.heldObject.Value is not null || string.IsNullOrWhiteSpace(bus.FilterQualifiedItemId))
            return false;

        var count = Math.Max(1, bus.ItemsPerOperation);
        var probeMessage = string.Empty;
        Item? extracted;
        if (bus.FilterBlacklist)
        {
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
        }
        else
        {
            if (!TryProbeMachineInput(machine, bus.FilterQualifiedItemId!, count, out probeMessage))
            {
                this.monitor.Log($"Exporter skipped {machine.QualifiedItemId}: {probeMessage}", LogLevel.Trace);
                return false;
            }

            var request = new NetworkItemRequest
            {
                QualifiedItemId = bus.FilterQualifiedItemId,
                Count = count
            };

            if (!this.transactionService.TryExtractItem(network, request, count, out extracted, out _, qualityStrategy: bus.QualityStrategy) || extracted is null)
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
                FilterBlacklist = false
            };
            network.TransferBuses[endpoint.EndpointId] = bus;
        }

        var placed = this.FindPlacedObject(endpoint);
        if (placed is not null)
        {
            bus.FilterQualifiedItemId = placed.modData.GetValueOrDefault(FilterKey);
            bus.FilterBlacklist = !string.IsNullOrWhiteSpace(bus.FilterQualifiedItemId) && this.GetBoolModData(placed, FilterBlacklistKey, false);
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
        data.FilterQualifiedItemId = busObject.modData.GetValueOrDefault(FilterKey);
        data.FilterBlacklist = !string.IsNullOrWhiteSpace(data.FilterQualifiedItemId) && this.GetBoolModData(busObject, FilterBlacklistKey, false);
        data.TickInterval = this.GetIntModData(busObject, TickIntervalKey, data.TickInterval);
        data.ItemsPerOperation = this.GetIntModData(busObject, ItemsPerOperationKey, data.ItemsPerOperation);
        data.QualityStrategy = this.GetQualityStrategy(busObject, data.QualityStrategy);
        data.MinSourceKeep = this.GetIntModData(busObject, MinSourceKeepKey, data.MinSourceKeep);
        data.TargetKeep = this.GetIntModData(busObject, TargetKeepKey, data.TargetKeep);
        this.repository.Save();
    }

    private IEnumerable<(Vector2 Tile, SObject Object)> GetAdjacentObjects(GameLocation location, NetworkEndpoint endpoint)
    {
        var origin = new Vector2(endpoint.TileX, endpoint.TileY);
        foreach (var offset in AdjacentOffsets)
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
        if (string.IsNullOrWhiteSpace(bus.FilterQualifiedItemId))
            return true;

        var matches = item.QualifiedItemId == bus.FilterQualifiedItemId;
        return bus.FilterBlacklist ? !matches : matches;
    }

    private static int CountMatching(Chest chest, string qualifiedItemId)
    {
        return chest.Items
            .Where(item => item is not null && item.QualifiedItemId == qualifiedItemId)
            .Sum(item => item!.Stack);
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
            MaterialQualityStrategy.HighQualityFirst => "高品质优先",
            MaterialQualityStrategy.PreserveGoldIridium => "保留金/铱",
            _ => "低品质优先"
        };
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
