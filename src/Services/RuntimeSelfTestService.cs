using System.Reflection;
using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Models;
using SVSAP.UI;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class RuntimeSelfTestService
{
    private readonly NetworkRepository repository;
    private readonly InventoryTransactionService transactionService;
    private readonly StorageCellInitializer storageCellInitializer;
    private readonly StorageDriveService storageDriveService;
    private readonly TransferBusService transferBusService;
    private readonly PatternExecutionService patternExecutionService;
    private readonly NetworkInteractionService networkInteractionService;
    private readonly CraftingRecipeService craftingRecipeService;
    private readonly Func<ModConfig> getConfig;
    private readonly IMonitor monitor;

    public RuntimeSelfTestService(
        NetworkRepository repository,
        InventoryTransactionService transactionService,
        StorageCellInitializer storageCellInitializer,
        StorageDriveService storageDriveService,
        TransferBusService transferBusService,
        PatternExecutionService patternExecutionService,
        NetworkInteractionService networkInteractionService,
        CraftingRecipeService craftingRecipeService,
        Func<ModConfig> getConfig,
        IMonitor monitor)
    {
        this.repository = repository;
        this.transactionService = transactionService;
        this.storageCellInitializer = storageCellInitializer;
        this.storageDriveService = storageDriveService;
        this.transferBusService = transferBusService;
        this.patternExecutionService = patternExecutionService;
        this.networkInteractionService = networkInteractionService;
        this.craftingRecipeService = craftingRecipeService;
        this.getConfig = getConfig;
        this.monitor = monitor;
    }

    public void RunCommand(string command, string[] args)
    {
        var tests = new (string Name, Action Body)[]
        {
            ("storage-cell-roundtrip", this.TestStorageCellRoundTrip),
            ("storage-cell-stack-guard", this.TestStorageCellStackGuard),
            ("storage-cell-network-guard", this.TestStorageCellNetworkGuard),
            ("storage-cell-stack-normalize", this.TestStorageCellStackNormalize),
            ("deposit-route-and-extract", this.TestDepositRouteAndExtract),
            ("wal-snapshot-recovery", this.TestWalSnapshotRecovery),
            ("preserved-parent-output", this.TestPreservedParentOutputMatch),
            ("cask-quality-and-parent-match", this.TestCaskQualityAndParentMatch),
            ("pattern-provider-roundtrip", this.TestPatternProviderRoundTrip),
            ("machine-reset-after-collect", this.TestMachineResetAfterCollect),
            ("v1-processing-catalog-scope", this.TestV1ProcessingCatalogScope),
            ("farmhand-hard-block", this.TestFarmhandHardBlock),
            ("remote-action-response-cache", this.TestRemoteActionResponseCache),
            ("remote-terminal-payload-escrow", this.TestRemoteTerminalPayloadEscrow),
            ("remote-delivery-ack-contract", this.TestRemoteDeliveryAckContract),
            ("remote-snapshot-paging-contract", this.TestRemoteSnapshotPagingContract),
            ("remote-structural-snapshot-contract", this.TestRemoteStructuralSnapshotContract),
            ("remote-localized-snapshot-contract", this.TestRemoteLocalizedSnapshotContract),
            ("search-textbox-contract", this.TestSearchTextBoxContract),
            ("crafting-terminal-contention-no-dupe", this.TestCraftingTerminalContentionNoDupe),
            ("remote-lock-targets-request-item", this.TestRemoteLockTargetsRequestItem),
            ("terminal-slot-lock-guard", this.TestTerminalSlotLockGuard),
            ("persistent-state-network-guard", this.TestPersistentStateNetworkGuard),
            ("structural-kernel-no-inventory-side-effects", this.TestStructuralKernelNoInventorySideEffects),
            ("structural-request-idempotent", this.TestStructuralRequestIdempotent),
            ("structural-host-failure-response", this.TestStructuralHostFailureResponse),
            ("structural-consume-targets-captured-item", this.TestStructuralConsumeTargetsCapturedItem),
            ("transfer-bus-kernel-parity", this.TestTransferBusKernelParity),
            ("gui-layout-bounds", this.TestGuiLayoutBounds),
            ("debug-addon-vanilla-material-recipes-visible", this.TestDebugAddonVanillaMaterialRecipesVisible),
            ("cpu-reserve-fast-slot", this.TestCpuReserveFastSlot)
        };

        var passed = new List<string>();
        try
        {
            foreach (var test in tests)
            {
                test.Body();
                passed.Add(test.Name);
            }

            this.monitor.Log($"SVSAP_SELFTEST_OK {passed.Count}/{tests.Length}: {string.Join(", ", passed)}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            var root = ex is TargetInvocationException { InnerException: not null }
                ? ex.InnerException
                : ex;
            this.monitor.Log($"SVSAP_SELFTEST_FAIL after {passed.Count}/{tests.Length}: {root.GetType().Name}: {root.Message}", LogLevel.Error);
        }
    }

    private void TestStorageCellRoundTrip()
    {
        var slot = this.CreateStorageCellSlot(StorageCellTier.OneK, 0, ("(O)388", 37));
        var cellItem = StorageCellCodec.CreateItem(slot);

        Assert(StorageCellCodec.TryReadCellData(cellItem, out var data), "round-tripped cell item must contain readable cell data");
        Assert(data.CellId == slot.CellId, "cell id must follow the item modData");
        Assert(data.CapacityMax == StorageCellTierInfo.GetCapacity(StorageCellTier.OneK), "cell capacity must survive round-trip");
        Assert(data.Items.Count == 1 && data.Items[0].Count == 37, "stored stack count must survive round-trip");
        Assert(data.CapacityUsed == StorageCellTierInfo.CalculateUsedBytes(data.Items), "cell capacity used must be SVSAP byte usage");
    }

    private void TestGuiLayoutBounds()
    {
        Assert(StorageDriveMenu.LayoutFits(menuWidth: 760), "storage drive GUI must fit its full-width 10-slot layout");
        Assert(StorageDriveMenu.LayoutFits(menuWidth: 520), "storage drive GUI must wrap storage-cell slots before the summary panel overflows compact widths");
        Assert(TransferBusMenu.LayoutFits(menuWidth: 1040), "importer/exporter GUI must fit its 3x3 filter and controls at full width");
        Assert(TransferBusMenu.LayoutFits(menuWidth: 720), "importer/exporter GUI controls must wrap instead of overflowing compact widths");
    }

    private void TestStorageCellStackGuard()
    {
        var filled = StorageCellCodec.CreateItem(this.CreateStorageCellSlot(StorageCellTier.OneK, 0, ("(O)388", 37)));
        var empty = ItemRegistry.Create(GetStorageCellQualifiedItemId(StorageCellTier.OneK));
        this.storageCellInitializer.EnsureCellData(empty);

        Assert(!StorageCellStackingPatch.CanStackPreservingCellState(filled, empty), "SVSAP storage cells must never stack with each other");
        Assert(!filled.canStackWith(empty) && !empty.canStackWith(filled), "Harmony canStackWith guard must reject storage-cell merges both ways");
    }

    private void TestStorageCellNetworkGuard()
    {
        var network = this.CreateTemporaryNetwork(out var networkId);
        try
        {
            var cell = ItemRegistry.Create(GetStorageCellQualifiedItemId(StorageCellTier.OneK));
            this.storageCellInitializer.EnsureCellData(cell);
            Assert(!this.transactionService.CanDepositPlayerItem(network, cell, out var message), "storage cells must not enter normal network storage");
            Assert(message == ModText.Get("terminal.depositBlocked.storageCell"), "storage-cell network rejection should use a specific player-facing reason");
        }
        finally
        {
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestStorageCellStackNormalize()
    {
        var stacked = StorageCellCodec.CreateItem(this.CreateStorageCellSlot(StorageCellTier.OneK, 0, ("(O)388", 37)));
        stacked.Stack = 2;
        var items = new List<Item?> { stacked, null, null };

        this.storageCellInitializer.InitializeInventory(items);

        var cells = items.Where(item => item is not null && ModItemCatalog.TryGetStorageCellTier(item!.QualifiedItemId, out _)).Cast<Item>().ToList();
        Assert(cells.Count == 2, "stack normalization must split a two-cell stack into two physical cell items");
        Assert(cells.All(cell => cell.Stack == 1), "normalized storage cells must each have stack size one");
        Assert(StorageCellCodec.TryReadCellData(cells[0], out var firstData), "first split cell must retain readable data");
        Assert(StorageCellCodec.TryReadCellData(cells[1], out var secondData), "second split cell must contain readable data");
        Assert(firstData.Items.Sum(stack => stack.Count) == 37, "first split cell must keep the original stored contents");
        Assert(secondData.Items.Sum(stack => stack.Count) == 0, "extra split cells must become empty instead of duplicating contents");
        Assert(firstData.CellId != secondData.CellId, "split cells must have unique cell identities");
    }

    private void TestDepositRouteAndExtract()
    {
        var network = this.CreateTemporaryNetwork(out var networkId);
        try
        {
            var drive = network.StorageDrives.Values.Single();
            var existingSlot = this.CreateStorageCellSlot(StorageCellTier.OneK, 0, ("(O)388", 5));
            var emptySlot = this.CreateStorageCellSlot(StorageCellTier.OneK, 1);
            drive.Slots.Add(existingSlot);
            drive.Slots.Add(emptySlot);

            var source = ItemRegistry.Create("(O)388");
            source.Stack = 3;
            Assert(this.transactionService.TryDepositItem(network, source, out var moved), "wood deposit must succeed");
            Assert(moved == 3 && source.Stack == 0, "deposit must move the full source stack");

            var existingData = this.ReadCell(existingSlot);
            var emptyData = this.ReadCell(emptySlot);
            Assert(existingData.Items.Count == 1 && existingData.Items[0].Count == 8, "deposit must continue the existing ItemKey stack first");
            Assert(existingData.CapacityUsed == 9, "eight items in one type must use eight type bytes plus one count byte");
            Assert(emptyData.Items.Count == 0, "deposit must not consume a new type slot while an existing type stack has space");

            var request = new NetworkItemRequest { QualifiedItemId = "(O)388", Count = 4 };
            Assert(this.transactionService.TryExtractItem(network, request, 4, out var extracted, out var message) && extracted is not null, $"extract must succeed: {message}");
            Assert(extracted!.Stack == 4 && extracted.QualifiedItemId == "(O)388", "extract must return the requested stack");

            existingData = this.ReadCell(existingSlot);
            Assert(existingData.Items.Count == 1 && existingData.Items[0].Count == 4, "extract must decrement the storage cell stack");
            Assert(existingData.CapacityUsed == 9, "one to eight items must still use one count byte");
        }
        finally
        {
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestWalSnapshotRecovery()
    {
        var network = this.CreateTemporaryNetwork(out var networkId);
        try
        {
            var drive = network.StorageDrives.Values.Single();
            var slot = this.CreateStorageCellSlot(StorageCellTier.OneK, 0, ("(O)388", 7));
            drive.Slots.Add(slot);
            var beforeRaw = slot.ModData[StorageCellInitializer.CellDataKey];

            var changed = this.ReadCell(slot);
            changed.Items[0].Count = 2;
            changed.CapacityUsed = StorageCellTierInfo.CalculateUsedBytes(changed.Items);
            StorageCellCodec.WriteCellData(slot, changed);

            var tx = new TxLogRecord
            {
                TxId = Guid.NewGuid(),
                NetworkId = network.NetworkId,
                Kind = TxKind.CellWithdraw,
                State = TxState.Prepared,
                ApplyPhase = TxApplyPhase.CellApplied,
                SerializedItem = SerializedItemCodec.SerializePrototype(ItemRegistry.Create("(O)388")),
                Count = 5,
                SourceRef = "selftest",
                TargetCellId = slot.CellId,
                CellDataBefore = beforeRaw
            };

            var action = this.InvokeRecoverPreparedTransaction(network, tx);
            var restored = this.ReadCell(slot);
            Assert(action.Contains("restored", StringComparison.OrdinalIgnoreCase), "cell-only recovery must report snapshot restore");
            Assert(restored.Items.Count == 1 && restored.Items[0].Count == 7 && restored.CapacityUsed == 9, "cell-only recovery must restore the pre-transaction cell snapshot");

            changed = this.ReadCell(slot);
            changed.Items[0].Count = 3;
            changed.CapacityUsed = StorageCellTierInfo.CalculateUsedBytes(changed.Items);
            StorageCellCodec.WriteCellData(slot, changed);

            var completedTx = new TxLogRecord
            {
                TxId = Guid.NewGuid(),
                NetworkId = network.NetworkId,
                Kind = TxKind.CellWithdraw,
                State = TxState.Prepared,
                ApplyPhase = TxApplyPhase.CounterpartApplied,
                SerializedItem = SerializedItemCodec.SerializePrototype(ItemRegistry.Create("(O)388")),
                Count = 4,
                SourceRef = "selftest",
                TargetCellId = slot.CellId,
                CellDataBefore = beforeRaw
            };

            action = this.InvokeRecoverPreparedTransaction(network, completedTx);
            var completed = this.ReadCell(slot);
            Assert(action.Contains("both transaction sides", StringComparison.OrdinalIgnoreCase), "completed recovery must report stale log discard");
            Assert(completed.Items.Count == 1 && completed.Items[0].Count == 3 && completed.CapacityUsed == 9, "completed recovery must not roll back a two-sided applied transaction");

            changed = this.ReadCell(slot);
            changed.Items[0].Count = 1;
            changed.CapacityUsed = StorageCellTierInfo.CalculateUsedBytes(changed.Items);
            StorageCellCodec.WriteCellData(slot, changed);

            var legacyTx = new TxLogRecord
            {
                TxId = Guid.NewGuid(),
                NetworkId = network.NetworkId,
                Kind = TxKind.CellWithdraw,
                State = TxState.Prepared,
                SerializedItem = SerializedItemCodec.SerializePrototype(ItemRegistry.Create("(O)388")),
                Count = 6,
                SourceRef = "selftest",
                TargetCellId = slot.CellId,
                CellDataBefore = beforeRaw
            };

            action = this.InvokeRecoverPreparedTransaction(network, legacyTx);
            var legacy = this.ReadCell(slot);
            Assert(action.Contains("legacy prepared log", StringComparison.OrdinalIgnoreCase), "legacy recovery must report safe discard");
            Assert(legacy.Items.Count == 1 && legacy.Items[0].Count == 1 && legacy.CapacityUsed == 9, "legacy recovery must not perform unsafe one-sided rollback");
        }
        finally
        {
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestPreservedParentOutputMatch()
    {
        var pattern = new PatternData
        {
            Kind = PatternKind.Processing,
            DisplayName = "Ancient Fruit Wine",
            MachineQualifiedItemId = "(BC)12",
            Inputs = new List<NetworkItemRequest> { new() { QualifiedItemId = "(O)454", Count = 1 } },
            Outputs = new List<NetworkItemRequest> { new() { QualifiedItemId = "(O)348", Count = 1 } }
        };

        var matchingWine = ItemRegistry.Create("(O)348");
        var wrongWine = ItemRegistry.Create("(O)348");
        Assert(matchingWine is SObject && wrongWine is SObject, "wine output must be a Stardew object");
        ((SObject)matchingWine).preservedParentSheetIndex.Value = "454";
        ((SObject)wrongWine).preservedParentSheetIndex.Value = "268";

        Assert(this.InvokeOutputMatchesPattern(pattern, matchingWine), "wine from the pattern input must match the processing output");
        Assert(!this.InvokeOutputMatchesPattern(pattern, wrongWine), "wine from a different preserved parent must not match the processing output");
    }

    private void TestCpuReserveFastSlot()
    {
        var config = this.getConfig();
        var oldReserve = config.ReserveFastSlots;
        try
        {
            config.ReserveFastSlots = 1;
            var network = new NetworkData { NetworkId = Guid.NewGuid(), Name = "selftest-cpu" };
            var cpuA = Guid.NewGuid();
            var cpuB = Guid.NewGuid();
            network.Endpoints.Add(new NetworkEndpoint { EndpointId = cpuA, Type = EndpointType.CraftingCpuCore, Active = true });
            network.Endpoints.Add(new NetworkEndpoint { EndpointId = cpuB, Type = EndpointType.CraftingCpuCore, Active = true });
            network.Endpoints.Add(new NetworkEndpoint { EndpointId = Guid.NewGuid(), Type = EndpointType.CraftingMatrix64K, Active = true });

            network.Jobs.Add(new CraftingJob
            {
                JobId = Guid.NewGuid(),
                State = CraftingJobState.Running,
                AssignedCpuEndpointId = cpuA,
                IsLongRunning = true,
                NodeCount = 2
            });

            var waitingLong = new CraftingJob
            {
                JobId = Guid.NewGuid(),
                State = CraftingJobState.Planning,
                IsLongRunning = true,
                NodeCount = 2
            };
            var waitingFast = new CraftingJob
            {
                JobId = Guid.NewGuid(),
                State = CraftingJobState.Planning,
                IsLongRunning = false,
                NodeCount = 2
            };
            network.Jobs.Add(waitingLong);
            network.Jobs.Add(waitingFast);

            this.InvokeAssignPlanningJobs(network);
            Assert(waitingLong.State == CraftingJobState.Planning
                && waitingLong.StatusMessage == ModText.Get("cpu.status.waitingUnreservedCpuSlot"), "second long job must wait behind the reserved fast slot");
            Assert(waitingFast.State == CraftingJobState.Running
                && waitingFast.AssignedCpuEndpointId == cpuB, "fast job must receive the reserved free CPU slot");
        }
        finally
        {
            config.ReserveFastSlots = oldReserve;
        }
    }

    private void TestCaskQualityAndParentMatch()
    {
        var inputWine = ItemRegistry.Create("(O)348");
        Assert(inputWine is SObject, "cask input wine must be a Stardew object");
        ((SObject)inputWine).preservedParentSheetIndex.Value = "454";
        ((SObject)inputWine).Quality = 0;

        var serializedInput = SerializedItemCodec.SerializePrototype(inputWine);
        var pipeline = new ProductionPipelineData
        {
            Mode = ProductionPipelineMode.CaskAging,
            Pattern = new PatternData
            {
                Kind = PatternKind.Processing,
                DisplayName = "Ancient Fruit Wine Aging",
                Inputs = new List<NetworkItemRequest>
                {
                    new()
                    {
                        QualifiedItemId = "(O)348",
                        SerializedItemPrototype = serializedInput,
                        Count = 1
                    }
                },
                Outputs = new List<NetworkItemRequest>
                {
                    new()
                    {
                        QualifiedItemId = "(O)348",
                        SerializedItemPrototype = serializedInput,
                        Count = 1
                    }
                }
            },
            MachineQualifiedItemId = "(BC)163"
        };

        var matchingAgedWine = ItemRegistry.Create("(O)348");
        var wrongParentWine = ItemRegistry.Create("(O)348");
        Assert(matchingAgedWine is SObject && wrongParentWine is SObject, "aged wine outputs must be Stardew objects");
        ((SObject)matchingAgedWine).preservedParentSheetIndex.Value = "454";
        ((SObject)matchingAgedWine).Quality = 4;
        ((SObject)wrongParentWine).preservedParentSheetIndex.Value = "268";
        ((SObject)wrongParentWine).Quality = 4;

        Assert(this.InvokeCaskOutputMatchesPipeline(pipeline, matchingAgedWine), "cask output must match the same preserved parent while ignoring upgraded quality");
        Assert(!this.InvokeCaskOutputMatchesPipeline(pipeline, wrongParentWine), "cask output must reject a different preserved parent");
    }

    private void TestPatternProviderRoundTrip()
    {
        var pattern = new PatternData
        {
            Kind = PatternKind.Processing,
            DisplayName = "Copper Bar",
            MachineQualifiedItemId = "(BC)13",
            ProcessingMinutes = 30,
            SpeedClass = ProcessingSpeedClass.Fast,
            Inputs = new List<NetworkItemRequest>
            {
                new() { QualifiedItemId = "(O)378", Count = 5 },
                new() { QualifiedItemId = "(O)382", Count = 1 }
            },
            Outputs = new List<NetworkItemRequest>
            {
                new() { QualifiedItemId = "(O)334", Count = 1 }
            }
        };

        var item = PatternCodec.CreatePatternItem(pattern);
        Assert(PatternCodec.IsPatternItem(item), "encoded processing pattern must be recognized as a pattern item");
        var slot = PatternCodec.ToSlotData(item, 3);
        var roundTripped = PatternCodec.CreateItem(slot);
        Assert(PatternCodec.TryRead(roundTripped, out var restored), "pattern item must survive provider slot round-trip");
        Assert(restored.Kind == PatternKind.Processing, "restored pattern kind must survive");
        Assert(restored.MachineQualifiedItemId == "(BC)13", "restored machine id must survive");
        Assert(restored.Inputs.Count == 2 && restored.Outputs.Count == 1, "restored input/output counts must survive");
        Assert(restored.Outputs[0].QualifiedItemId == "(O)334", "restored output id must survive");
    }

    private void TestMachineResetAfterCollect()
    {
        var machine = ItemRegistry.Create("(BC)12");
        if (machine is not SObject obj)
            throw new InvalidOperationException("keg machine item must be a Stardew object");

        obj.heldObject.Value = ItemRegistry.Create<SObject>("(O)348");
        obj.readyForHarvest.Value = true;
        obj.MinutesUntilReady = 500;
        obj.showNextIndex.Value = true;

        MachineStateHelper.ResetAfterAutomatedCollect(obj);

        Assert(obj.heldObject.Value is null, "automated collection reset must clear heldObject");
        Assert(!obj.readyForHarvest.Value, "automated collection reset must clear readyForHarvest");
        Assert(obj.MinutesUntilReady == 0, "automated collection reset must clear MinutesUntilReady");
        Assert(!obj.showNextIndex.Value, "automated collection reset must clear showNextIndex");
    }

    private void TestV1ProcessingCatalogScope()
    {
        var patterns = ProcessingPatternCatalog.CreateDefaults().ToList();
        var machineIds = patterns.Select(pattern => pattern.MachineQualifiedItemId).ToHashSet(StringComparer.Ordinal);

        foreach (var required in new[] { "(BC)13", "(BC)114", "(BC)16", "(BC)24", "(BC)17", "(BC)19", "(BC)Dehydrator", "(BC)FishSmoker", "(BC)12", "(BC)15" })
            Assert(machineIds.Contains(required), $"V1 processing catalog must include deterministic machine {required}");

        Assert(!machineIds.Contains("(BC)25"), "V1 processing catalog must not include seed maker random outputs");
        Assert(!machineIds.Contains("(BC)182"), "V1 processing catalog must not include geode crusher random outputs");
        Assert(!machineIds.Contains("(BC)163"), "V1 processing catalog must not expose cask aging as a CPU processing pattern");
        Assert(patterns.Any(pattern => pattern.MachineQualifiedItemId == "(BC)FishSmoker" && pattern.ProcessingMinutes == 50 && pattern.SpeedClass == ProcessingSpeedClass.Medium), "fish smoker must remain a medium 50-minute pattern");
        Assert(patterns.Any(pattern => pattern.MachineQualifiedItemId == "(BC)12" && pattern.SpeedClass == ProcessingSpeedClass.Slow), "keg patterns must remain slow CPU-capable processing patterns");
        Assert(patterns.Any(pattern => pattern.MachineQualifiedItemId == "(BC)15" && pattern.SpeedClass == ProcessingSpeedClass.Slow), "preserves jar patterns must remain slow CPU-capable processing patterns");
    }

    private void TestFarmhandHardBlock()
    {
        Assert(!this.InvokeIsHostOnlySVSAPInteraction(CreateBigCraftable(ModItemCatalog.StorageDrive)), "storage drive interactions must not be hard-blocked for farmhands");
        Assert(!this.InvokeIsHostOnlySVSAPInteraction(CreateBigCraftable(ModItemCatalog.Importer)), "importer interactions must not be hard-blocked for farmhands");
        Assert(!this.InvokeIsHostOnlySVSAPInteraction(CreateBigCraftable(ModItemCatalog.Exporter)), "exporter interactions must not be hard-blocked for farmhands");
        Assert(!this.InvokeIsHostOnlySVSAPInteraction(CreateBigCraftable(ModItemCatalog.PatternProvider)), "pattern provider interactions must not be hard-blocked for farmhands");
        Assert(!this.InvokeIsHostOnlySVSAPInteraction(CreateBigCraftable(ModItemCatalog.CraftingMonitor)), "crafting monitor interactions must use remote paths without a hard farmhand block");
        Assert(!this.InvokeIsHostOnlySVSAPInteraction(CreateBigCraftable(ModItemCatalog.PatternTerminal)), "pattern terminal must remain local because it only encodes held patterns");
    }

    private void TestRemoteActionResponseCache()
    {
        this.networkInteractionService.ClearActionResponseCaches();
        try
        {
            var playerId = 987654321L;
            var otherPlayerId = playerId + 1;
            var terminalTx = Guid.NewGuid();
            var craftingTx = Guid.NewGuid();
            var monitorTx = Guid.NewGuid();

            var terminalResponse = new TerminalActionResponseMessage
            {
                TransactionId = terminalTx,
                NetworkId = Guid.NewGuid(),
                Success = true,
                Message = "terminal cached"
            };
            var craftingResponse = new CraftingActionResponseMessage
            {
                TransactionId = craftingTx,
                NetworkId = Guid.NewGuid(),
                Success = true,
                Message = "crafting cached"
            };
            var monitorResponse = new CraftingMonitorActionResponseMessage
            {
                TransactionId = monitorTx,
                NetworkId = Guid.NewGuid(),
                Success = true,
                Message = "monitor cached"
            };

            this.InvokeRememberResponse("RememberTerminalActionResponse", playerId, terminalTx, terminalResponse);
            this.InvokeRememberResponse("RememberCraftingActionResponse", playerId, craftingTx, craftingResponse);
            this.InvokeRememberResponse("RememberCraftingMonitorActionResponse", playerId, monitorTx, monitorResponse);

            Assert(ReferenceEquals(this.InvokeTryGetCachedResponse<TerminalActionResponseMessage>("TryGetCachedTerminalActionResponse", playerId, terminalTx), terminalResponse), "terminal duplicate request must return the cached response");
            Assert(ReferenceEquals(this.InvokeTryGetCachedResponse<CraftingActionResponseMessage>("TryGetCachedCraftingActionResponse", playerId, craftingTx), craftingResponse), "crafting duplicate request must return the cached response");
            Assert(ReferenceEquals(this.InvokeTryGetCachedResponse<CraftingMonitorActionResponseMessage>("TryGetCachedCraftingMonitorActionResponse", playerId, monitorTx), monitorResponse), "monitor duplicate request must return the cached response");
            Assert(this.InvokeTryGetCachedResponse<TerminalActionResponseMessage>("TryGetCachedTerminalActionResponse", otherPlayerId, terminalTx) is null, "response cache keys must include the requesting player");

            var ignoredResponse = new TerminalActionResponseMessage
            {
                TransactionId = Guid.Empty,
                NetworkId = Guid.NewGuid(),
                Success = true,
                Message = "ignored"
            };
            this.InvokeRememberResponse("RememberTerminalActionResponse", playerId, Guid.Empty, ignoredResponse);
            Assert(this.InvokeTryGetCachedResponse<TerminalActionResponseMessage>("TryGetCachedTerminalActionResponse", playerId, Guid.Empty) is null, "empty transaction ids must not be cached");

            var otherResponse = new TerminalActionResponseMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = Guid.NewGuid(),
                Success = true,
                Message = "other cached"
            };
            this.InvokeRememberResponse("RememberTerminalActionResponse", otherPlayerId, otherResponse.TransactionId, otherResponse);
            this.networkInteractionService.ClearActionResponseCaches(playerId);
            Assert(this.InvokeTryGetCachedResponse<TerminalActionResponseMessage>("TryGetCachedTerminalActionResponse", playerId, terminalTx) is null, "per-player cache clear must remove that player's terminal response");
            Assert(ReferenceEquals(this.InvokeTryGetCachedResponse<TerminalActionResponseMessage>("TryGetCachedTerminalActionResponse", otherPlayerId, otherResponse.TransactionId), otherResponse), "per-player cache clear must not remove other players' responses");
        }
        finally
        {
            this.networkInteractionService.ClearActionResponseCaches();
        }
    }

    private void TestRemoteTerminalPayloadEscrow()
    {
        var requestType = typeof(TerminalActionRequestMessage);
        var responseType = typeof(TerminalActionResponseMessage);
        Assert(requestType.GetProperty(nameof(TerminalActionRequestMessage.DepositItems)) is not null, "terminal requests must carry serialized deposit payloads");
        Assert(responseType.GetProperty(nameof(TerminalActionResponseMessage.ReturnedDepositItems)) is not null, "terminal responses must carry returned deposit payloads");
        Assert(typeof(RemoteNetworkTerminalMenu).GetMethod("CaptureDepositSlot", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "remote terminal UI must locally escrow slot deposits");
        Assert(typeof(RemoteNetworkTerminalMenu).GetMethod("CaptureDepositBatch", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "remote terminal UI must locally escrow batch deposits");
        Assert(typeof(NetworkInteractionService).GetField("pendingTerminalDepositItems", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "client service must track pending terminal deposit escrow");
        Assert(typeof(NetworkInteractionService).GetMethod("CreateTerminalFailureResponse", BindingFlags.Static | BindingFlags.NonPublic) is not null, "host terminal failure path must be able to return deposit escrow payloads");
        Assert(typeof(NetworkInteractionService).GetMethod("TryTakeHostStructuralHeldItem", BindingFlags.Instance | BindingFlags.NonPublic) is null, "host must not consume farmhand inventory copies for structural actions");

        var beforeInventory = SnapshotPlayerInventory();
        var network = this.CreateTemporaryNetwork(out var networkId);
        try
        {
            var drive = network.StorageDrives.Values.Single();
            drive.Slots.Add(this.CreateStorageCellSlot(StorageCellTier.OneK, 0));

            var woodPrototype = ItemRegistry.Create("(O)388");
            woodPrototype.Stack = 1;
            var depositRequest = new TerminalActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = networkId,
                Action = TerminalActionKind.DepositAll,
                DepositItems = new List<TerminalItemPayloadMessage>
                {
                    new()
                    {
                        SerializedItem = SerializedItemCodec.SerializePrototype(woodPrototype),
                        Count = 7
                    }
                }
            };
            var depositResponse = new TerminalActionResponseMessage();

            Assert(this.InvokeRemoteTerminalDepositPayloads(network, depositRequest, depositResponse, out var depositMessage), $"payload deposit must succeed: {depositMessage}");
            Assert(depositResponse.ReturnedDepositItems.Count == 0, "successful full payload deposit must not return escrow leftovers");
            Assert(this.ReadCell(drive.Slots.Single()).Items.Single().Count == 7, "payload deposit must move the serialized item into network storage");
            AssertInventoryUnchanged(beforeInventory, "host-side payload deposit must not mutate Game1.player inventory");

            var withdrawRequest = new TerminalActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = networkId,
                Action = TerminalActionKind.Withdraw,
                ItemKey = ItemKeyFactory.FromItem(woodPrototype),
                Amount = 3
            };
            var withdrawResponse = new TerminalActionResponseMessage();
            Assert(this.InvokeRemoteTerminalWithdraw(network, withdrawRequest, withdrawResponse, out var withdrawMessage), $"payload withdraw must succeed: {withdrawMessage}");
            Assert(!string.IsNullOrWhiteSpace(withdrawResponse.ReturnedSerializedItem), "remote withdraw must return a serialized item payload");
            Assert(withdrawResponse.ReturnedCount == 3, "remote withdraw response must return the extracted count");
            Assert(this.ReadCell(drive.Slots.Single()).Items.Single().Count == 4, "remote withdraw must deduct only the host network storage");
            AssertInventoryUnchanged(beforeInventory, "host-side withdraw must not add items to Game1.player inventory");

            var badRequest = new TerminalActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = networkId,
                Action = TerminalActionKind.DepositAll,
                DepositItems = new List<TerminalItemPayloadMessage>
                {
                    new() { SerializedItem = string.Empty, Count = 1 }
                }
            };
            var badResponse = new TerminalActionResponseMessage();
            Assert(!this.InvokeRemoteTerminalDepositPayloads(network, badRequest, badResponse, out _), "malformed payload deposit must fail");
            Assert(badResponse.ReturnedDepositItems.Count == 1, "malformed payload deposit must return the escrow payload for client restoration");
            AssertInventoryUnchanged(beforeInventory, "malformed payload handling must not mutate Game1.player inventory");
        }
        finally
        {
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestRemoteDeliveryAckContract()
    {
        Assert(typeof(TerminalActionResponseMessage).GetProperty(nameof(TerminalActionResponseMessage.DeliveryId))?.PropertyType == typeof(Guid), "terminal responses must carry a durable delivery id");
        Assert(typeof(StructuralActionResponseMessage).GetProperty(nameof(StructuralActionResponseMessage.DeliveryId))?.PropertyType == typeof(Guid), "structural responses must carry a durable delivery id");
        Assert(typeof(RemoteDeliveryAckMessage).GetProperty(nameof(RemoteDeliveryAckMessage.DeliveryId))?.PropertyType == typeof(Guid), "remote delivery ACK must name the delivered payload");
        Assert(typeof(RemoteDeliveryAckMessage).GetProperty(nameof(RemoteDeliveryAckMessage.TransactionId))?.PropertyType == typeof(Guid), "remote delivery ACK must name the original transaction");
        Assert(typeof(NetworkSaveData).GetProperty(nameof(NetworkSaveData.SchemaVersion))?.PropertyType == typeof(int), "network save data must carry a schema version");
        Assert(typeof(NetworkSaveData).GetProperty(nameof(NetworkSaveData.PendingRemoteDeliveries))?.PropertyType == typeof(List<PendingRemoteDelivery>), "network save data must persist pending remote deliveries");
        Assert(typeof(NetworkInteractionService).GetMethod(nameof(NetworkInteractionService.ResendPendingRemoteDeliveries), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.ReturnType == typeof(void), "host must expose pending remote delivery resend on peer reconnect");
        Assert(typeof(NetworkInteractionService).GetMethod("HandleRemoteDeliveryAck", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "host must handle remote delivery ACK messages");
        Assert(typeof(NetworkInteractionService).GetMethod("MarkRemoteDeliveryReconciled", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "client must remember delivered payload ids before ACKing duplicates");
        Assert(typeof(NetworkInteractionService).GetMethod("DeliverStructuralReturnedItem", BindingFlags.Instance | BindingFlags.NonPublic)?.ReturnType == typeof(bool), "structural returned-item delivery must be retryable without reapplying held-item side effects");
        Assert(typeof(NetworkInteractionService).GetMethod("RegisterTerminalRemoteDelivery", BindingFlags.Instance | BindingFlags.NonPublic)?.ReturnType == typeof(bool), "terminal withdraw payloads must be registered in durable pending delivery storage");
        Assert(typeof(NetworkInteractionService).GetMethod("RegisterStructuralRemoteDelivery", BindingFlags.Instance | BindingFlags.NonPublic)?.ReturnType == typeof(bool), "structural returned items must be registered in durable pending delivery storage");
    }

    private void TestRemoteSnapshotPagingContract()
    {
        Assert(typeof(TerminalSnapshotRequestMessage).GetProperty(nameof(TerminalSnapshotRequestMessage.EntryOffset)) is not null, "terminal snapshot requests must carry a page offset");
        Assert(typeof(TerminalSnapshotRequestMessage).GetProperty(nameof(TerminalSnapshotRequestMessage.EntryLimit)) is not null, "terminal snapshot requests must carry a bounded page size");
        Assert(typeof(TerminalSnapshotResponseMessage).GetProperty(nameof(TerminalSnapshotResponseMessage.TotalEntryCount)) is not null, "terminal snapshot responses must report total entries");
        Assert(typeof(TerminalSnapshotResponseMessage).GetProperty(nameof(TerminalSnapshotResponseMessage.Truncated)) is not null, "terminal snapshot responses must report truncation");
        Assert(NetworkInteractionService.NormalizeTerminalSnapshotEntryLimit(0) == 512, "remote terminal snapshots must default to a bounded page size");
        Assert(NetworkInteractionService.NormalizeTerminalSnapshotEntryLimit(2000) == 1024, "remote terminal snapshots must clamp oversized page requests");
        Assert(typeof(RemoteNetworkTerminalMenu).GetMethod("RequestPage", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "remote terminal UI must expose page navigation over bounded snapshots");
    }

    private void TestRemoteStructuralSnapshotContract()
    {
        Assert(MultiplayerMessageTypes.StructuralSnapshotRequest == "StructuralSnapshotRequest", "storage drive / transfer bus remote menus must have a structural snapshot request message");
        Assert(MultiplayerMessageTypes.StructuralSnapshotResponse == "StructuralSnapshotResponse", "storage drive / transfer bus remote menus must have a structural snapshot response message");
        Assert(typeof(StructuralSnapshotRequestMessage).GetProperty(nameof(StructuralSnapshotRequestMessage.Kind))?.PropertyType == typeof(StructuralSnapshotKind), "structural snapshot requests must identify the requested menu kind");
        Assert(typeof(StructuralSnapshotResponseMessage).GetProperty(nameof(StructuralSnapshotResponseMessage.StorageDrive))?.PropertyType == typeof(RemoteStorageDriveSnapshotMessage), "structural snapshots must carry storage drive slot state");
        Assert(typeof(StructuralSnapshotResponseMessage).GetProperty(nameof(StructuralSnapshotResponseMessage.TransferBus))?.PropertyType == typeof(RemoteTransferBusSnapshotMessage), "structural snapshots must carry transfer bus configuration state");
        Assert(typeof(RemoteStorageDriveMenu).GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)?.PropertyType == typeof(StructuralSnapshotResponseMessage), "remote storage drive menu must render host-authored snapshots");
        Assert(typeof(RemoteTransferBusMenu).GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)?.PropertyType == typeof(StructuralSnapshotResponseMessage), "remote transfer bus menu must render host-authored snapshots");
        Assert(typeof(NetworkInteractionService).GetMethod("CreateStructuralSnapshotResponse", BindingFlags.Instance | BindingFlags.NonPublic)?.ReturnType == typeof(StructuralSnapshotResponseMessage), "host must produce structural snapshots for farmhand empty-hand menus");
        Assert(typeof(NetworkInteractionService).GetMethod("HandleStructuralSnapshotRequest", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "host must receive structural snapshot requests");
        Assert(typeof(NetworkInteractionService).GetMethod("HandleStructuralSnapshotResponse", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "farmhand must receive structural snapshot responses");
        Assert(typeof(NetworkInteractionService).GetMethod("RefreshActiveRemoteStructuralMenu", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "structural actions must refresh open remote storage/transfer menus");
        Assert(typeof(StorageDriveService).GetMethod(nameof(StorageDriveService.ApplyEjectCellSlot))?.ReturnType == typeof(StructuralActionResult), "remote storage drive slot ejection must use a host-authoritative kernel");
        Assert(typeof(TransferBusService).GetMethod(nameof(TransferBusService.ApplySetFilterSlot))?.ReturnType == typeof(StructuralActionResult), "remote transfer filter edits must use a host-authoritative kernel");
        Assert(Enum.IsDefined(typeof(StructuralActionKind), StructuralActionKind.StorageDriveEjectSlot), "structural action enum must include storage drive slot ejection");
        Assert(Enum.IsDefined(typeof(StructuralActionKind), StructuralActionKind.TransferBusSetFilterSlot), "structural action enum must include transfer bus filter slot edits");
    }

    private void TestRemoteLocalizedSnapshotContract()
    {
        Assert(typeof(RemoteCraftingRecipeMessage).GetProperty(nameof(RemoteCraftingRecipeMessage.MissingIngredients)) is not null, "remote crafting recipes must carry structured missing ingredients for client-local rendering");
        Assert(typeof(RemoteCraftingJobMessage).GetProperty(nameof(RemoteCraftingJobMessage.Pattern)) is not null, "remote monitor jobs must carry pattern data for client-local names");
        Assert(typeof(RemoteProductionPipelineMessage).GetProperty(nameof(RemoteProductionPipelineMessage.Pattern)) is not null, "remote monitor pipelines must carry pattern data for client-local names");
        Assert(Enum.IsDefined(typeof(CraftingMonitorActionKind), CraftingMonitorActionKind.PreviewQueueJob), "remote CPU monitor must request a host-side queue preview before queueing");
        Assert(typeof(CraftingMonitorActionResponseMessage).GetProperty(nameof(CraftingMonitorActionResponseMessage.PreviewPattern)) is not null, "remote CPU preview responses must carry the previewed pattern");
        Assert(typeof(CraftingMonitorActionResponseMessage).GetProperty(nameof(CraftingMonitorActionResponseMessage.PreviewBatches)) is not null, "remote CPU preview responses must carry the previewed batch count");
        Assert(typeof(CraftingMonitorActionResponseMessage).GetProperty(nameof(CraftingMonitorActionResponseMessage.PreviewLines)) is not null, "remote CPU preview responses must carry confirmation lines");
        Assert(typeof(RemoteCraftingTerminalMenu).GetMethod("GetRecipeDisplayName", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "remote crafting UI must derive recipe names on the client");
        Assert(typeof(RemoteCraftingMonitorMenu).GetMethod("GetJobDisplayName", BindingFlags.Static | BindingFlags.NonPublic) is not null, "remote monitor UI must derive job names on the client");
        Assert(typeof(RemoteCraftingMonitorMenu).GetMethod("FormatPipelineStatus", BindingFlags.Static | BindingFlags.NonPublic) is not null, "remote monitor UI must derive pipeline status on the client");
        Assert(typeof(RemoteCraftingMonitorMenu).GetMethod("QueuePreviewedPattern", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "remote monitor UI must queue only after the preview confirmation dialog");
    }

    private void TestSearchTextBoxContract()
    {
        Assert(typeof(SVSAPMenuWidgets).GetMethod(nameof(SVSAPMenuWidgets.CreateSearchTextBox)) is not null, "search boxes must use Stardew TextBox input for IME text");
        Assert(typeof(SVSAPMenuWidgets).GetMethod(nameof(SVSAPMenuWidgets.SyncSearchText)) is not null, "search boxes must synchronize TextBox text into menu filters");
        Assert(typeof(NetworkTerminalMenu).GetField("searchInput", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "network terminal search must use TextBox input");
        Assert(typeof(CraftingTerminalMenu).GetField("searchInput", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "crafting terminal search must use TextBox input");
        Assert(typeof(PatternTerminalMenu).GetField("searchInput", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "pattern terminal search must use TextBox input");
        Assert(typeof(RemoteNetworkTerminalMenu).GetField("searchInput", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "remote terminal search must use TextBox input");
        Assert(typeof(RemoteCraftingTerminalMenu).GetField("searchInput", BindingFlags.Instance | BindingFlags.NonPublic) is not null, "remote crafting search must use TextBox input");
    }

    private void TestRemoteLockTargetsRequestItem()
    {
        var player = Game1.player ?? throw new InvalidOperationException("selftest requires a player");
        var originalSlot0 = player.Items[0];
        var originalToolIndex = player.CurrentToolIndex;
        var network = this.CreateNetworkWithEndpoint(EndpointType.NetworkTerminal, out var networkId, out _);
        try
        {
            player.Items[0] = ItemRegistry.Create("(O)390");
            player.CurrentToolIndex = 0;

            var request = new TerminalActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = networkId,
                Action = TerminalActionKind.ToggleHeldItemLock,
                HeldQualifiedItemId = "(O)388",
                HeldDisplayName = "Wood"
            };

            Assert(this.InvokeTryToggleHeldItemLock(network, player, request, out var message), $"remote lock request must succeed: {message}");
            Assert(network.LockedQualifiedItemIds.Contains("(O)388", StringComparer.Ordinal), "remote lock must target the item id captured in the request");
            Assert(!network.LockedQualifiedItemIds.Contains("(O)390", StringComparer.Ordinal), "remote lock must not target the current item at response handling time");
        }
        finally
        {
            player.Items[0] = originalSlot0;
            player.CurrentToolIndex = originalToolIndex;
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestTerminalSlotLockGuard()
    {
        var player = Game1.player ?? throw new InvalidOperationException("selftest requires a player");
        var originalSlot0 = player.Items[0];
        var network = this.CreateTemporaryNetwork(out var networkId);
        try
        {
            network.StorageDrives.Values.Single().Slots.Add(this.CreateStorageCellSlot(StorageCellTier.OneK, 0));
            network.LockedQualifiedItemIds.Add("(O)388");
            var wood = ItemRegistry.Create("(O)388");
            wood.Stack = 3;
            player.Items[0] = wood;

            Assert(!this.transactionService.TryDepositPlayerSlot(network, player, 0, single: false, out var moved, out var message), "locked single-slot deposit must fail");
            Assert(moved == 0, "locked single-slot deposit must not move items");
            Assert(player.Items[0]?.Stack == 3, "locked single-slot deposit must preserve the player stack");
            Assert(message == ModText.Get("terminal.depositBlocked.locked"), "locked single-slot deposit must report the lock reason");

            network.LockedQualifiedItemIds.Clear();
            Assert(this.transactionService.TryDepositPlayerSlot(network, player, 0, single: false, out moved, out message), $"unlocked single-slot deposit must succeed: {message}");
            Assert(moved == 3 && player.Items[0] is null, "unlocked single-slot deposit must move the full stack");
        }
        finally
        {
            player.Items[0] = originalSlot0;
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestPersistentStateNetworkGuard()
    {
        var item = ItemRegistry.Create("(O)388");
        item.Stack = 1;
        item.modData[ModItemCatalog.UniqueId + "/MachineGuid"] = Guid.NewGuid().ToString("N");
        var network = this.CreateTemporaryNetwork(out var networkId);
        try
        {
            network.StorageDrives.Values.Single().Slots.Add(this.CreateStorageCellSlot(StorageCellTier.OneK, 0));
            Assert(!this.transactionService.TryDepositItem(network, item, out var moved), "persistent-state item must not enter SVSAP network storage");
            Assert(moved == 0 && item.Stack == 1, "persistent-state rejection must preserve the source item");
        }
        finally
        {
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestStructuralKernelNoInventorySideEffects()
    {
        var beforeInventory = SnapshotPlayerInventory();
        var network = this.CreateNetworkWithEndpoint(EndpointType.StorageDrive, out var networkId, out var endpointId);
        try
        {
            var drive = CreateLinkedBigCraftable(ModItemCatalog.StorageDrive, networkId, endpointId);
            var cell = ItemRegistry.Create("(O)" + ModItemCatalog.StorageCell1K);
            this.storageCellInitializer.EnsureCellData(cell);

            var insert = this.storageDriveService.ApplyInteract(
                drive,
                null!,
                Vector2.Zero,
                cell.QualifiedItemId,
                SerializedItemCodec.SerializePrototype(cell));
            Assert(insert.Success, $"storage drive kernel insert must succeed: {insert.Message}");
            Assert(insert.ConsumeHeldOne, "storage drive kernel insert must request held-item consumption");
            Assert(network.StorageDrives.TryGetValue(endpointId, out var driveData), "storage drive kernel must create drive data");
            Assert(driveData!.Slots.Count == 1, "storage drive kernel must write one storage cell slot");
            var inserted = this.ReadCell(driveData.Slots.Single());
            AssertInventoryUnchanged(beforeInventory, "storage drive insert kernel must not touch Game1.player inventory");

            var eject = this.storageDriveService.ApplyInteract(drive, null!, Vector2.Zero, string.Empty, string.Empty);
            Assert(eject.Success, $"storage drive kernel eject must succeed: {eject.Message}");
            Assert(!string.IsNullOrWhiteSpace(eject.ReturnedSerializedItem), "storage drive kernel eject must return a serialized item payload");
            var returned = SerializedItemCodec.CreateItem(eject.ReturnedSerializedItem, 1);
            Assert(StorageCellCodec.TryReadCellData(returned, out var returnedData), "returned storage cell payload must decode");
            Assert(returnedData.CellId == inserted.CellId, "ejected storage cell must preserve the inserted cell id");
            Assert(driveData.Slots.Count == 0, "storage drive kernel eject must remove the network slot");
            AssertInventoryUnchanged(beforeInventory, "storage drive eject kernel must not touch Game1.player inventory");
        }
        finally
        {
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestStructuralRequestIdempotent()
    {
        this.networkInteractionService.ClearActionResponseCaches();
        var network = this.CreateNetworkWithEndpoint(EndpointType.StorageDrive, out var networkId, out var endpointId);
        try
        {
            var playerId = 99887766L;
            var transactionId = Guid.NewGuid();
            var drive = CreateLinkedBigCraftable(ModItemCatalog.StorageDrive, networkId, endpointId);
            var cell = ItemRegistry.Create("(O)" + ModItemCatalog.StorageCell1K);
            this.storageCellInitializer.EnsureCellData(cell);
            var payload = SerializedItemCodec.SerializePrototype(cell);

            var first = this.ExecuteStorageInsertThroughStructuralCache(playerId, transactionId, drive, cell.QualifiedItemId, payload);
            var second = this.ExecuteStorageInsertThroughStructuralCache(playerId, transactionId, drive, cell.QualifiedItemId, payload);

            Assert(first.Success, $"first structural cached insert must succeed: {first.Message}");
            Assert(ReferenceEquals(first, second), "duplicate structural request must return the cached response object");
            Assert(network.StorageDrives.TryGetValue(endpointId, out var driveData), "structural cached insert must create drive data");
            Assert(driveData!.Slots.Count == 1, "duplicate structural request must not insert the same storage cell twice");
        }
        finally
        {
            this.networkInteractionService.ClearActionResponseCaches();
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void TestStructuralHostFailureResponse()
    {
        var request = new StructuralActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            Kind = StructuralActionKind.TransferBusConfigure
        };

        var response = this.InvokeCreateStructuralFailureResponse(request);
        Assert(response.TransactionId == request.TransactionId, "structural host failure response must preserve the request transaction id");
        Assert(response.Kind == request.Kind, "structural host failure response must preserve the request kind");
        Assert(!response.Success, "structural host failure response must not report success");
        Assert(!response.ConsumeHeldOne, "structural host failure response must not consume the escrowed held item");
        Assert(string.IsNullOrWhiteSpace(response.ReturnedSerializedItem), "structural host failure response must not mint a returned item");
    }

    private void TestStructuralConsumeTargetsCapturedItem()
    {
        var player = Game1.player ?? throw new InvalidOperationException("selftest requires a player");
        Assert(player.Items.Count >= 2, "selftest requires at least two inventory slots");

        var originalSlot0 = player.Items[0];
        var originalSlot1 = player.Items[1];
        var originalToolIndex = player.CurrentToolIndex;
        try
        {
            var linkTool = ItemRegistry.Create("(O)" + ModItemCatalog.LinkTool);
            var currentItem = ItemRegistry.Create("(O)390");
            currentItem.Stack = 5;
            player.Items[0] = linkTool;
            player.Items[1] = currentItem;
            player.CurrentToolIndex = 1;

            var networkId = Guid.NewGuid();
            this.InvokeReconcileStructuralResult(
                StructuralActionKind.LinkSelectCore,
                new StructuralActionResult { Success = true, ResultNetworkId = networkId },
                location: null,
                showMessage: false,
                actingItem: linkTool);
            Assert(linkTool.modData.TryGetValue(NetworkInteractionService.SelectedNetworkIdKey, out var selectedNetworkId)
                && selectedNetworkId == networkId.ToString("N"), "link reconcile must update the captured link tool");
            Assert(!currentItem.modData.ContainsKey(NetworkInteractionService.SelectedNetworkIdKey), "link reconcile must not update the current non-tool item");

            var itemA = ItemRegistry.Create("(O)" + ModItemCatalog.StorageCell1K);
            itemA.Stack = 2;
            this.storageCellInitializer.EnsureCellData(itemA);
            var itemB = ItemRegistry.Create("(O)390");
            itemB.Stack = 5;
            player.Items[0] = itemA;
            player.Items[1] = itemB;
            player.CurrentToolIndex = 1;

            var consume = new StructuralActionResult { Success = true, ConsumeHeldOne = true };
            this.InvokeReconcileStructuralResult(
                StructuralActionKind.StorageDriveInteract,
                consume,
                location: null,
                showMessage: false,
                actingItem: itemA);
            Assert(itemA.Stack == 1, "consume reconcile must decrement the captured item");
            Assert(itemB.Stack == 5, "consume reconcile must not decrement the current item after hand switch");

            itemA.Stack = 1;
            this.InvokeReconcileStructuralResult(
                StructuralActionKind.StorageDriveInteract,
                consume,
                location: null,
                showMessage: false,
                actingItem: itemA,
                consumeHeldAlreadyApplied: true);
            Assert(itemA.Stack == 1, "escrowed structural consume must not decrement the captured item twice");
            Assert(itemB.Stack == 5, "escrowed structural consume must still not decrement the current item");

            player.Items[0] = null;
            var itemBBeforeMissingCapturedItem = itemB.Stack;
            this.InvokeReconcileStructuralResult(
                StructuralActionKind.StorageDriveInteract,
                consume,
                location: null,
                showMessage: false,
                actingItem: itemA);
            Assert(player.Items.IndexOf(itemA) < 0, "captured item must remain absent from inventory for the missing-item branch");
            Assert(itemB.Stack == itemBBeforeMissingCapturedItem, "missing captured item must not cause any current-item consumption");
        }
        finally
        {
            player.Items[0] = originalSlot0;
            player.Items[1] = originalSlot1;
            player.CurrentToolIndex = originalToolIndex;
        }
    }

    private void TestTransferBusKernelParity()
    {
        var networkA = this.CreateNetworkWithEndpoint(EndpointType.Importer, out var networkIdA, out var endpointIdA);
        var networkB = this.CreateNetworkWithEndpoint(EndpointType.Importer, out var networkIdB, out var endpointIdB);
        try
        {
            var directBus = CreateLinkedBigCraftable(ModItemCatalog.Importer, networkIdA, endpointIdA);
            var requestBus = CreateLinkedBigCraftable(ModItemCatalog.Importer, networkIdB, endpointIdB);
            var speedCardId = "(O)" + ModItemCatalog.SpeedCard;
            var oreCardId = "(O)" + ModItemCatalog.OreDictionaryCard;

            var direct = this.transferBusService.ApplyConfigure(directBus, speedCardId, "Speed Card", 1);
            var requestEquivalent = this.InvokeApplyStructuralAction(
                StructuralActionKind.TransferBusConfigure,
                requestBus,
                null!,
                Vector2.Zero,
                Guid.Empty,
                speedCardId,
                "Speed Card",
                1,
                string.Empty);

            Assert(direct.Success && requestEquivalent.Success, "direct and request-equivalent transfer bus kernels must both succeed");
            Assert(direct.ConsumeHeldOne && requestEquivalent.ConsumeHeldOne, "speed card configuration must request one held-card consumption on both paths");
            Assert(direct.Message == requestEquivalent.Message, "transfer bus kernel messages must match");
            Assert(SameModData(directBus, requestBus), "transfer bus direct and request-equivalent kernels must write the same modData");
            var directOre = this.transferBusService.ApplyConfigure(directBus, oreCardId, "Ore Dictionary Card", 1);
            var requestOre = this.InvokeApplyStructuralAction(
                StructuralActionKind.TransferBusConfigure,
                requestBus,
                null!,
                Vector2.Zero,
                Guid.Empty,
                oreCardId,
                "Ore Dictionary Card",
                1,
                string.Empty);
            Assert(directOre.Success && requestOre.Success, "ore dictionary card configuration must succeed on both paths");
            Assert(directOre.ConsumeHeldOne && requestOre.ConsumeHeldOne, "ore dictionary card must request one held-card consumption on both paths");
            Assert(SameModData(directBus, requestBus), "ore dictionary direct and request-equivalent kernels must write the same modData");
            Assert(this.transferBusService.TrySetFilterSlot(directBus, 4, "(O)378", out _), "transfer bus 3x3 filter slot must accept a filter item");
            Assert(this.transferBusService.GetFilterSlotViews(directBus).Any(slot => slot.SlotIndex == 4 && slot.QualifiedItemId == "(O)378"), "transfer bus filter slot view must preserve the selected 3x3 slot");
            Assert(this.transferBusService.TrySetFacingDirection(directBus, 1, out _), "transfer bus direction control must accept right-facing direction");
            Assert(this.transferBusService.GetFacingDirection(directBus) == 1, "transfer bus direction control must persist the selected side");
            var directData = GetTransferBusData(networkA, endpointIdA);
            var requestData = GetTransferBusData(networkB, endpointIdB);
            Assert(directData.TickInterval == requestData.TickInterval, "transfer bus tick interval must match between direct and request-equivalent kernels");
            Assert(directData.ItemsPerOperation == requestData.ItemsPerOperation, "transfer bus capacity must match between direct and request-equivalent kernels");
            Assert(directData.QualityStrategy == requestData.QualityStrategy, "transfer bus quality strategy must match between direct and request-equivalent kernels");
        }
        finally
        {
            this.CleanupTemporaryNetwork(networkIdA);
            this.CleanupTemporaryNetwork(networkIdB);
        }
    }

    private void TestDebugAddonVanillaMaterialRecipesVisible()
    {
        const string carbonRod = "Koizumi.SVSAPME.CarbonRod";
        const string copperCoil = "Koizumi.SVSAPME.CopperCoil";

        var rawRecipes = Game1.content.Load<Dictionary<string, string>>("Data/CraftingRecipes");
        if (!rawRecipes.ContainsKey(carbonRod) && !rawRecipes.ContainsKey(copperCoil))
            return;

        Assert(rawRecipes.ContainsKey(carbonRod), "SVSAPME debug recipe visibility selftest expected CarbonRod when addon recipes are loaded");
        Assert(rawRecipes.ContainsKey(copperCoil), "SVSAPME debug recipe visibility selftest expected CopperCoil when addon recipes are loaded");

        var config = this.getConfig();
        var previousMode = config.RecipeCostMode;
        var previousCasual = config.CasualRecipeCosts;
        try
        {
            config.RecipeCostMode = RecipeCostModes.Debug;
            config.CasualRecipeCosts = false;
            var known = this.craftingRecipeService.GetKnownRecipesForPlayer(Game1.player)
                .ToDictionary(recipe => recipe.Name, StringComparer.Ordinal);

            Assert(known.ContainsKey(carbonRod), "Debug terminal recipes must include SVSAPME CarbonRod even though its ingredients are vanilla-only");
            Assert(known.ContainsKey(copperCoil), "Debug terminal recipes must include SVSAPME CopperCoil even though its ingredients are vanilla-only");

            var network = this.CreateTemporaryNetwork(out var networkId);
            try
            {
                network.StorageDrives.Values.Single().Slots.Add(this.CreateStorageCellSlot(StorageCellTier.OneK, 0));
                this.DepositSelfTestStack(network, "(O)382", 5);
                this.DepositSelfTestStack(network, "(O)334", 2);
                this.DepositSelfTestStack(network, "(O)338", 1);

                var carbonAvailability = this.craftingRecipeService.GetAvailability(network, known[carbonRod], 1);
                var coilAvailability = this.craftingRecipeService.GetAvailability(network, known[copperCoil], 1);
                Assert(carbonAvailability.CanCraft, "Debug terminal CarbonRod recipe must be craftable");
                Assert(coilAvailability.CanCraft, "Debug terminal CopperCoil recipe must be craftable");
            }
            finally
            {
                this.CleanupTemporaryNetwork(networkId);
            }
        }
        finally
        {
            config.RecipeCostMode = previousMode;
            config.CasualRecipeCosts = previousCasual;
        }
    }

    private void TestCraftingTerminalContentionNoDupe()
    {
        var network = this.CreateTemporaryNetwork(out var networkId);
        try
        {
            network.StorageDrives.Values.Single().Slots.Add(this.CreateStorageCellSlot(StorageCellTier.OneK, 0, ("(O)390", 5)));

            var output = ItemRegistry.Create("(O)338");
            output.Stack = 1;
            var recipe = new NetworkCraftingRecipe
            {
                Name = "SVSAP selftest contention",
                DisplayName = output.DisplayName,
                OutputPrototype = output,
                OutputCount = 1,
                Ingredients = new List<NetworkItemRequest>
                {
                    new() { QualifiedItemId = "(O)390", Count = 5 }
                }
            };

            Assert(this.craftingRecipeService.TryCraftForPlayer(network, Game1.player, recipe, 1, MaterialQualityStrategy.LowQualityFirst, out var firstMessage), $"first crafting terminal action must succeed: {firstMessage}");
            Assert(!this.craftingRecipeService.TryCraftForPlayer(network, Game1.player, recipe, 1, MaterialQualityStrategy.LowQualityFirst, out _), "second identical crafting action must fail after the first one consumes the exact ingredients");

            var woodCount = this.CountNetworkItem(network, "(O)390");
            var outputCount = this.CountNetworkItem(network, "(O)338");
            Assert(woodCount == 0, "contention test must not leave consumed ingredients in the network");
            Assert(outputCount == 1, "contention test must create exactly one output stack");
        }
        finally
        {
            this.CleanupTemporaryNetwork(networkId);
        }
    }

    private void DepositSelfTestStack(NetworkData network, string qualifiedItemId, int count)
    {
        var item = ItemRegistry.Create(qualifiedItemId);
        item.Stack = count;
        Assert(this.transactionService.TryDepositItem(network, item, out var moved) && moved == count && item.Stack == 0, $"selftest deposit must move {qualifiedItemId} x{count}");
    }

    private NetworkData CreateTemporaryNetwork(out Guid networkId)
    {
        networkId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var network = new NetworkData { NetworkId = networkId, Name = "SVSAP selftest" };
        network.Endpoints.Add(new NetworkEndpoint
        {
            EndpointId = endpointId,
            Type = EndpointType.StorageDrive,
            Active = true
        });
        network.StorageDrives[endpointId] = new StorageDriveData { EndpointId = endpointId };
        this.repository.Data.Networks[networkId] = network;
        return network;
    }

    private NetworkData CreateNetworkWithEndpoint(EndpointType endpointType, out Guid networkId, out Guid endpointId)
    {
        networkId = Guid.NewGuid();
        endpointId = Guid.NewGuid();
        var network = new NetworkData { NetworkId = networkId, Name = "SVSAP structural selftest" };
        network.Endpoints.Add(new NetworkEndpoint
        {
            EndpointId = endpointId,
            Type = endpointType,
            Active = true,
            LocationName = "SVSAPStructuralSelfTest",
            TileX = 0,
            TileY = 0
        });
        this.repository.Data.Networks[networkId] = network;
        return network;
    }

    private void CleanupTemporaryNetwork(Guid networkId)
    {
        this.repository.Data.Networks.Remove(networkId);
        this.repository.Data.PendingTransactions.RemoveAll(tx => tx.NetworkId == networkId);
    }

    private StorageDriveSlotData CreateStorageCellSlot(StorageCellTier tier, int slotIndex, params (string QualifiedItemId, int Count)[] stacks)
    {
        var cell = ItemRegistry.Create(GetStorageCellQualifiedItemId(tier));
        this.storageCellInitializer.EnsureCellData(cell);
        var slot = StorageCellCodec.ToSlotData(cell, slotIndex);
        var data = this.ReadCell(slot);
        data.Items.Clear();
        data.CapacityUsed = 0;

        foreach (var stack in stacks)
        {
            var prototype = ItemRegistry.Create(stack.QualifiedItemId);
            prototype.Stack = 1;
            data.Items.Add(new StoredItemStack
            {
                Key = ItemKeyFactory.FromItem(prototype),
                Count = stack.Count,
                SerializedItemPrototype = SerializedItemCodec.SerializePrototype(prototype)
            });
        }

        data.CapacityUsed = StorageCellTierInfo.CalculateUsedBytes(data.Items);
        StorageCellCodec.WriteCellData(slot, data);
        return slot;
    }

    private StorageCellData ReadCell(StorageDriveSlotData slot)
    {
        Assert(StorageCellCodec.TryReadCellData(slot, out var data), "storage cell slot must contain readable data");
        return data;
    }

    private int CountNetworkItem(NetworkData network, string qualifiedItemId)
    {
        return network.StorageDrives.Values
            .SelectMany(drive => drive.Slots)
            .Select(this.ReadCell)
            .SelectMany(cell => cell.Items)
            .Where(stack => stack.Key.QualifiedItemId == qualifiedItemId)
            .Sum(stack => stack.Count);
    }

    private string InvokeRecoverPreparedTransaction(NetworkData network, TxLogRecord tx)
    {
        var method = typeof(InventoryTransactionService).GetMethod("RecoverPreparedTransaction", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "RecoverPreparedTransaction reflection hook must exist");
        return (string)(method!.Invoke(this.transactionService, new object[] { network, tx }) ?? string.Empty);
    }

    private bool InvokeOutputMatchesPattern(PatternData pattern, Item output)
    {
        var method = typeof(PatternExecutionService).GetMethod("OutputMatchesPattern", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(method is not null, "OutputMatchesPattern reflection hook must exist");
        return (bool)(method!.Invoke(null, new object[] { pattern, output }) ?? false);
    }

    private bool InvokeCaskOutputMatchesPipeline(ProductionPipelineData pipeline, Item output)
    {
        var method = typeof(PatternExecutionService).GetMethod("CaskOutputMatchesPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "CaskOutputMatchesPipeline reflection hook must exist");
        return (bool)(method!.Invoke(this.patternExecutionService, new object[] { pipeline, output }) ?? false);
    }

    private void InvokeAssignPlanningJobs(NetworkData network)
    {
        var method = typeof(PatternExecutionService).GetMethod("AssignPlanningJobs", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "AssignPlanningJobs reflection hook must exist");
        method!.Invoke(this.patternExecutionService, new object[] { network });
    }

    private bool InvokeIsHostOnlySVSAPInteraction(SObject target)
    {
        var method = typeof(NetworkInteractionService).GetMethod("IsHostOnlySVSAPInteraction", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "IsHostOnlySVSAPInteraction reflection hook must exist");
        return (bool)(method!.Invoke(this.networkInteractionService, new object[] { target }) ?? false);
    }

    private void InvokeRememberResponse<TResponse>(string methodName, long playerId, Guid transactionId, TResponse response)
        where TResponse : class
    {
        var method = typeof(NetworkInteractionService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, $"{methodName} reflection hook must exist");
        method!.Invoke(this.networkInteractionService, new object[] { playerId, transactionId, response });
    }

    private TResponse? InvokeTryGetCachedResponse<TResponse>(string methodName, long playerId, Guid transactionId)
        where TResponse : class
    {
        var method = typeof(NetworkInteractionService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, $"{methodName} reflection hook must exist");
        object?[] parameters = { playerId, transactionId, null };
        var found = (bool)(method!.Invoke(this.networkInteractionService, parameters) ?? false);
        return found ? parameters[2] as TResponse : null;
    }

    private StructuralActionResponseMessage InvokeCreateStructuralFailureResponse(StructuralActionRequestMessage request)
    {
        var method = typeof(NetworkInteractionService).GetMethod("CreateStructuralFailureResponse", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(method is not null, "CreateStructuralFailureResponse reflection hook must exist");
        var response = method!.Invoke(null, new object?[] { request, null }) as StructuralActionResponseMessage;
        Assert(response is not null, "CreateStructuralFailureResponse must return a structural response");
        return response!;
    }

    private StructuralActionResponseMessage ExecuteStorageInsertThroughStructuralCache(
        long playerId,
        Guid transactionId,
        SObject drive,
        string heldQualifiedItemId,
        string heldSerializedItem)
    {
        var cached = this.InvokeTryGetCachedResponse<StructuralActionResponseMessage>(
            "TryGetCachedStructuralActionResponse",
            playerId,
            transactionId);
        if (cached is not null)
            return cached;

        var result = this.storageDriveService.ApplyInteract(drive, null!, Vector2.Zero, heldQualifiedItemId, heldSerializedItem);
        var response = new StructuralActionResponseMessage
        {
            TransactionId = transactionId,
            Kind = StructuralActionKind.StorageDriveInteract,
            Success = result.Success,
            Message = result.Message,
            ConsumeHeldOne = result.ConsumeHeldOne,
            ReturnedSerializedItem = result.ReturnedSerializedItem,
            ResultNetworkId = result.ResultNetworkId
        };
        this.InvokeRememberResponse("RememberStructuralActionResponse", playerId, transactionId, response);
        return response;
    }

    private StructuralActionResult InvokeApplyStructuralAction(
        StructuralActionKind kind,
        SObject target,
        GameLocation location,
        Vector2 tile,
        Guid selectedNetworkId,
        string heldQualifiedItemId,
        string heldDisplayName,
        int heldStack,
        string heldSerializedItem,
        int slotIndex = -1,
        string filterQualifiedItemId = "",
        int facingDirection = -1)
    {
        var method = typeof(NetworkInteractionService).GetMethod("ApplyStructuralAction", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "ApplyStructuralAction reflection hook must exist");
        return (StructuralActionResult)(method!.Invoke(
            this.networkInteractionService,
            new object[] { kind, target, location, tile, selectedNetworkId, heldQualifiedItemId, heldDisplayName, heldStack, heldSerializedItem, slotIndex, filterQualifiedItemId, facingDirection }) ?? StructuralActionResult.Fail("missing result"));
    }

    private void InvokeReconcileStructuralResult(
        StructuralActionKind kind,
        StructuralActionResult result,
        GameLocation? location,
        bool showMessage,
        Item? actingItem,
        bool consumeHeldAlreadyApplied = false)
    {
        var method = typeof(NetworkInteractionService).GetMethod("ReconcileStructuralResult", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "ReconcileStructuralResult reflection hook must exist");
        method!.Invoke(this.networkInteractionService, new object?[] { kind, result, location, showMessage, actingItem, consumeHeldAlreadyApplied });
    }

    private bool InvokeRemoteTerminalDepositPayloads(
        NetworkData network,
        TerminalActionRequestMessage request,
        TerminalActionResponseMessage response,
        out string message)
    {
        var method = typeof(NetworkInteractionService).GetMethod("TryExecuteRemoteTerminalDepositPayloads", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "TryExecuteRemoteTerminalDepositPayloads reflection hook must exist");
        object?[] parameters = { network, request, response, null };
        var result = (bool)(method!.Invoke(this.networkInteractionService, parameters) ?? false);
        message = parameters[3] as string ?? string.Empty;
        return result;
    }

    private bool InvokeRemoteTerminalWithdraw(
        NetworkData network,
        TerminalActionRequestMessage request,
        TerminalActionResponseMessage response,
        out string message)
    {
        var method = typeof(NetworkInteractionService).GetMethod("TryExecuteRemoteTerminalWithdraw", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "TryExecuteRemoteTerminalWithdraw reflection hook must exist");
        object?[] parameters = { network, request, response, null };
        var result = (bool)(method!.Invoke(this.networkInteractionService, parameters) ?? false);
        message = parameters[3] as string ?? string.Empty;
        return result;
    }

    private bool InvokeTryToggleHeldItemLock(NetworkData network, Farmer player, TerminalActionRequestMessage request, out string message)
    {
        var method = typeof(NetworkInteractionService).GetMethod("TryToggleHeldItemLock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method is not null, "TryToggleHeldItemLock reflection hook must exist");
        object?[] parameters = { network, player, request, null };
        var result = (bool)(method!.Invoke(this.networkInteractionService, parameters) ?? false);
        message = parameters[3] as string ?? string.Empty;
        return result;
    }

    private static SObject CreateBigCraftable(string id)
    {
        var item = ItemRegistry.Create("(BC)" + id);
        if (item is not SObject obj)
            throw new InvalidOperationException($"Expected {id} to create a Stardew object.");

        return obj;
    }

    private static SObject CreateLinkedBigCraftable(string id, Guid networkId, Guid endpointId)
    {
        var obj = CreateBigCraftable(id);
        obj.modData[EndpointIdentityService.NetworkIdKey] = networkId.ToString("N");
        obj.modData[EndpointIdentityService.EndpointIdKey] = endpointId.ToString("N");
        return obj;
    }

    private static TransferBusData GetTransferBusData(NetworkData network, Guid endpointId)
    {
        Assert(network.TransferBuses.TryGetValue(endpointId, out var data), "transfer bus data must exist");
        return data!;
    }

    private static bool SameModData(SObject left, SObject right)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            EndpointIdentityService.NetworkIdKey,
            EndpointIdentityService.EndpointIdKey
        };
        var leftKeys = left.modData.Keys.Where(key => !ignored.Contains(key)).ToHashSet(StringComparer.Ordinal);
        var rightKeys = right.modData.Keys.Where(key => !ignored.Contains(key)).ToHashSet(StringComparer.Ordinal);
        return leftKeys.SetEquals(rightKeys)
            && leftKeys.All(key => string.Equals(left.modData[key], right.modData[key], StringComparison.Ordinal));
    }

    private static List<string> SnapshotPlayerInventory()
    {
        if (Game1.player is null)
            return new List<string> { "<no-player>" };

        return Game1.player.Items
            .Select(item => item is null
                ? "<null>"
                : $"{item.QualifiedItemId}|{item.Stack}|{SerializedItemCodec.SerializePrototype(item)}")
            .ToList();
    }

    private static void AssertInventoryUnchanged(List<string> before, string message)
    {
        Assert(before.SequenceEqual(SnapshotPlayerInventory()), message);
    }

    private static string GetStorageCellQualifiedItemId(StorageCellTier tier)
    {
        return tier switch
        {
            StorageCellTier.OneK => "(O)" + ModItemCatalog.StorageCell1K,
            StorageCellTier.FourK => "(O)" + ModItemCatalog.StorageCell4K,
            StorageCellTier.SixtyFourK => "(O)" + ModItemCatalog.StorageCell64K,
            StorageCellTier.TwoHundredFiftySixK => "(O)" + ModItemCatalog.StorageCell256K,
            StorageCellTier.OneThousandTwentyFourK => "(O)" + ModItemCatalog.StorageCell1024K,
            StorageCellTier.FourThousandNinetySixK => "(O)" + ModItemCatalog.StorageCell4096K,
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown storage cell tier.")
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
