using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Models;
using SVSAP.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Tools;
using System.Text.Json;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class NetworkInteractionService
{
    internal const string SelectedNetworkIdKey = ModItemCatalog.UniqueId + "/SelectedNetworkId";
    private const int ActionResponseCacheLimit = 256;
    private const int ClientReconcileCacheLimit = 256;
    private const int PendingRemoteDeliveryRetentionDays = 7;
    private const int ClientEscrowResponseTimeoutTicks = 300;
    private const int ClientEscrowRetryLimit = 3;
    private const int RemoteTerminalSnapshotDefaultEntryLimit = 512;
    private const int RemoteTerminalSnapshotMaxEntryLimit = 1024;
    private const int RemoteTerminalSnapshotLocationLimit = 32;
    private const string ReconciledRemoteDeliveryModDataKey = ModItemCatalog.UniqueId + "/ReconciledRemoteDeliveries";
    private const string DurableRemoteDeliveryModDataKey = ModItemCatalog.UniqueId + "/DurableRemoteDeliveries";
    private const string DurableClientActionEscrowModDataKey = ModItemCatalog.UniqueId + "/PendingClientActionEscrows";

    private static readonly Vector2[] AdjacentOffsets =
    {
        new Vector2(0, -1),
        new Vector2(1, 0),
        new Vector2(0, 1),
        new Vector2(-1, 0)
    };

    private readonly NetworkRepository repository;
    private readonly EndpointIdentityService endpointIdentityService;
    private readonly InventoryScanner inventoryScanner;
    private readonly InventoryTransactionService transactionService;
    private readonly CraftingRecipeService craftingRecipeService;
    private readonly StorageDriveService storageDriveService;
    private readonly TransferBusService transferBusService;
    private readonly PatternEncodingService patternEncodingService;
    private readonly PatternProviderService patternProviderService;
    private readonly PatternExecutionService patternExecutionService;
    private readonly Func<ModConfig> getConfig;
    private readonly IInputHelper inputHelper;
    private readonly IMultiplayerHelper multiplayerHelper;
    private readonly IManifest modManifest;
    private readonly IMonitor monitor;
    private readonly Dictionary<(long PlayerId, Guid TransactionId), TerminalActionResponseMessage> terminalActionResponseCache = new();
    private readonly Queue<(long PlayerId, Guid TransactionId)> terminalActionResponseOrder = new();
    private readonly Dictionary<(long PlayerId, Guid TransactionId), CraftingActionResponseMessage> craftingActionResponseCache = new();
    private readonly Queue<(long PlayerId, Guid TransactionId)> craftingActionResponseOrder = new();
    private readonly Dictionary<(long PlayerId, Guid TransactionId), CraftingMonitorActionResponseMessage> craftingMonitorActionResponseCache = new();
    private readonly Queue<(long PlayerId, Guid TransactionId)> craftingMonitorActionResponseOrder = new();
    private readonly Dictionary<(long PlayerId, Guid TransactionId), StructuralActionResponseMessage> structuralResponseCache = new();
    private readonly Queue<(long PlayerId, Guid TransactionId)> structuralResponseOrder = new();
    private readonly HashSet<Guid> reconciledTerminalTx = new();
    private readonly Queue<Guid> reconciledTerminalTxOrder = new();
    private readonly HashSet<Guid> reconciledCraftingTx = new();
    private readonly Queue<Guid> reconciledCraftingTxOrder = new();
    private readonly HashSet<Guid> reconciledCraftingMonitorTx = new();
    private readonly Queue<Guid> reconciledCraftingMonitorTxOrder = new();
    private readonly HashSet<Guid> reconciledStructuralTx = new();
    private readonly Queue<Guid> reconciledStructuralTxOrder = new();
    private readonly HashSet<Guid> reconciledRemoteDeliveries = new();
    private readonly Queue<Guid> reconciledRemoteDeliveryOrder = new();
    private readonly Dictionary<Guid, List<TerminalItemPayloadMessage>> pendingTerminalDepositItems = new();
    private readonly Dictionary<Guid, PendingStructuralHeldItem> pendingStructuralHeldItems = new();
    private readonly Dictionary<Guid, TerminalActionRequestMessage> pendingTerminalEscrowRequests = new();
    private readonly Dictionary<Guid, StructuralActionRequestMessage> pendingStructuralEscrowRequests = new();
    private readonly Dictionary<Guid, ClientEscrowRetryState> clientEscrowRetryStates = new();
    private readonly Dictionary<long, string> blockedPeerActionMessages = new();

    internal event Action<StructuralActionResponseMessage>? StructuralActionResponseReceived;

    public NetworkInteractionService(
        NetworkRepository repository,
        EndpointIdentityService endpointIdentityService,
        InventoryScanner inventoryScanner,
        InventoryTransactionService transactionService,
        CraftingRecipeService craftingRecipeService,
        StorageDriveService storageDriveService,
        TransferBusService transferBusService,
        PatternEncodingService patternEncodingService,
        PatternProviderService patternProviderService,
        PatternExecutionService patternExecutionService,
        Func<ModConfig> getConfig,
        IInputHelper inputHelper,
        IMultiplayerHelper multiplayerHelper,
        IManifest modManifest,
        IMonitor monitor)
    {
        this.repository = repository;
        this.endpointIdentityService = endpointIdentityService;
        this.inventoryScanner = inventoryScanner;
        this.transactionService = transactionService;
        this.craftingRecipeService = craftingRecipeService;
        this.storageDriveService = storageDriveService;
        this.transferBusService = transferBusService;
        this.patternEncodingService = patternEncodingService;
        this.patternProviderService = patternProviderService;
        this.patternExecutionService = patternExecutionService;
        this.getConfig = getConfig;
        this.inputHelper = inputHelper;
        this.multiplayerHelper = multiplayerHelper;
        this.modManifest = modManifest;
        this.monitor = monitor;
    }

    public void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.activeClickableMenu is not null || !e.Button.IsActionButton())
            return;

        var location = Game1.currentLocation;
        if (location is null)
            return;

        var tile = e.Cursor.GrabTile;
        if (!location.objects.TryGetValue(tile, out SObject? target))
            return;

        if (this.IsHoldingLinkTool())
        {
            this.HandleLinkToolUse(target, location, tile);
            this.Suppress(e);
            return;
        }

        if (!Context.IsMainPlayer && this.IsRemoteTerminalInteraction(target, out var remoteCrafting))
        {
            this.RequestRemoteTerminalSnapshot(target, remoteCrafting);
            this.Suppress(e);
            return;
        }

        if (!Context.IsMainPlayer && target.QualifiedItemId == "(BC)" + ModItemCatalog.CraftingMonitor)
        {
            this.RequestRemoteCraftingMonitorSnapshot(target);
            this.Suppress(e);
            return;
        }

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.StorageDrive)
        {
            if (Game1.player.CurrentItem is null)
            {
                if (Context.IsMainPlayer)
                    this.OpenStorageDriveMenu(target, location, tile);
                else
                    this.RequestRemoteStructuralSnapshot(StructuralSnapshotKind.StorageDrive, target, location, tile);
            }
            else if (Context.IsMainPlayer)
                this.RunStructuralLocally(StructuralActionKind.StorageDriveInteract, target, location, tile);
            else
                this.SendItemBearingStructuralRequest(StructuralActionKind.StorageDriveInteract, target, location, tile);

            this.Suppress(e);
            return;
        }

        if (target.QualifiedItemId is ("(BC)" + ModItemCatalog.Importer) or ("(BC)" + ModItemCatalog.Exporter))
        {
            if (Game1.player.CurrentItem is null)
            {
                if (Context.IsMainPlayer)
                    this.OpenTransferBusMenu(target);
                else
                    this.RequestRemoteStructuralSnapshot(StructuralSnapshotKind.TransferBus, target, location, tile);
            }
            else if (Context.IsMainPlayer)
            {
                this.RunStructuralLocally(StructuralActionKind.TransferBusConfigure, target, location, tile);
            }
            else
            {
                this.SendItemBearingStructuralRequest(StructuralActionKind.TransferBusConfigure, target, location, tile);
            }

            this.Suppress(e);
            return;
        }

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.PatternProvider)
        {
            if (Game1.player.CurrentItem is null)
            {
                if (Context.IsMainPlayer)
                    this.OpenPatternProviderMenu(target);
                else
                    this.RequestRemoteStructuralSnapshot(StructuralSnapshotKind.PatternProvider, target, location, tile);
            }
            else if (Context.IsMainPlayer)
                this.RunStructuralLocally(StructuralActionKind.PatternProviderInteract, target, location, tile);
            else
                this.SendItemBearingStructuralRequest(StructuralActionKind.PatternProviderInteract, target, location, tile);

            this.Suppress(e);
            return;
        }

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.CraftingMonitor)
        {
            this.OpenCraftingMonitor(target);
            this.Suppress(e);
            return;
        }

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.NetworkTerminal)
        {
            this.OpenTerminal(target, crafting: false);
            this.Suppress(e);
            return;
        }

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.CraftingTerminal)
        {
            this.OpenTerminal(target, crafting: true);
            this.Suppress(e);
            return;
        }

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.PatternTerminal)
        {
            Game1.activeClickableMenu = new PatternTerminalMenu(this.patternEncodingService);
            this.Suppress(e);
        }
    }

    public void RebuildPlacedEndpointCache()
    {
        foreach (var location in Game1.locations)
        {
            foreach (var pair in location.objects.Pairs)
            {
                var placedObject = pair.Value;
                if (!Guid.TryParse(placedObject.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
                    continue;

                if (!Guid.TryParse(placedObject.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out var endpointId))
                    endpointId = this.endpointIdentityService.EnsureEndpointId(placedObject);

                var type = this.GetEndpointType(placedObject);
                if (type is null)
                    continue;

                this.repository.UpsertEndpoint(networkId, this.CreateEndpoint(endpointId, location, pair.Key, type.Value));
            }
        }

        this.RefreshEndpointConnectivity();
    }

    public bool TryRegisterPlacedEndpoint(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!Guid.TryParse(placedObject.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
            return false;

        if (!Guid.TryParse(placedObject.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out var endpointId))
            endpointId = this.endpointIdentityService.EnsureEndpointId(placedObject);

        var type = this.GetEndpointType(placedObject);
        if (type is null)
            return false;

        this.repository.UpsertEndpoint(networkId, this.CreateEndpoint(endpointId, location, tile, type.Value));
        return true;
    }

    public bool TryRemovePlacedEndpoint(SObject removedObject)
    {
        if (!Guid.TryParse(removedObject.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId)
            || !Guid.TryParse(removedObject.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out var endpointId)
            || !this.repository.TryGetNetwork(networkId, out var network))
        {
            return false;
        }

        var changed = network.Endpoints.RemoveAll(endpoint => endpoint.EndpointId == endpointId) > 0;
        changed |= network.TransferBuses.Remove(endpointId);
        changed |= network.PatternProviders.Remove(endpointId);

        if (changed)
            this.repository.Save();

        return changed;
    }

    public void RecoverPendingTransactions()
    {
        this.transactionService.RecoverPendingTransactions();
    }

    public void ClearActionResponseCaches()
    {
        this.ClearActionResponseCaches(restorePendingItems: true);
    }

    public void ClearActionResponseCachesWithoutRestore()
    {
        this.ClearActionResponseCaches(restorePendingItems: false);
    }

    private void ClearActionResponseCaches(bool restorePendingItems)
    {
        this.terminalActionResponseCache.Clear();
        this.terminalActionResponseOrder.Clear();
        this.craftingActionResponseCache.Clear();
        this.craftingActionResponseOrder.Clear();
        this.craftingMonitorActionResponseCache.Clear();
        this.craftingMonitorActionResponseOrder.Clear();
        this.structuralResponseCache.Clear();
        this.structuralResponseOrder.Clear();
        this.reconciledTerminalTx.Clear();
        this.reconciledTerminalTxOrder.Clear();
        this.reconciledCraftingTx.Clear();
        this.reconciledCraftingTxOrder.Clear();
        this.reconciledCraftingMonitorTx.Clear();
        this.reconciledCraftingMonitorTxOrder.Clear();
        this.reconciledStructuralTx.Clear();
        this.reconciledStructuralTxOrder.Clear();
        this.reconciledRemoteDeliveries.Clear();
        this.reconciledRemoteDeliveryOrder.Clear();
        if (restorePendingItems)
        {
            this.RestorePendingTerminalDepositItems();
            this.RestorePendingStructuralHeldItems();
        }
        else
        {
            this.pendingTerminalDepositItems.Clear();
            this.pendingStructuralHeldItems.Clear();
            this.pendingTerminalEscrowRequests.Clear();
            this.pendingStructuralEscrowRequests.Clear();
            this.clientEscrowRetryStates.Clear();
        }
    }

    public void ClearActionResponseCaches(long playerId)
    {
        ClearCachedResponsesForPlayer(this.terminalActionResponseCache, this.terminalActionResponseOrder, playerId);
        ClearCachedResponsesForPlayer(this.craftingActionResponseCache, this.craftingActionResponseOrder, playerId);
        ClearCachedResponsesForPlayer(this.craftingMonitorActionResponseCache, this.craftingMonitorActionResponseOrder, playerId);
        ClearCachedResponsesForPlayer(this.structuralResponseCache, this.structuralResponseOrder, playerId);
    }

    public void ResendPendingRemoteDeliveries(long playerId)
    {
        if (!Context.IsMainPlayer || playerId <= 0)
            return;

        foreach (var delivery in this.repository.Data.PendingRemoteDeliveries
                     .Where(delivery => delivery.PlayerId == playerId)
                     .ToList())
        {
            try
            {
                if (delivery.Kind == RemoteDeliveryKind.TerminalWithdraw)
                    this.SendTerminalActionResponse(this.CreateTerminalResponse(delivery), playerId);
                else if (delivery.Kind == RemoteDeliveryKind.StructuralReturnedItem)
                    this.SendStructuralResponse(this.CreateStructuralResponse(delivery), playerId);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Failed to resend pending SVSAP remote delivery {delivery.DeliveryId:N} to {playerId}: {ex.Message}", LogLevel.Trace);
            }
        }
    }

    public int PruneExpiredPendingRemoteDeliveries(int currentDay)
    {
        if (!Context.IsMainPlayer || currentDay <= 0)
            return 0;

        var changed = false;
        var removed = 0;
        var queued = 0;
        foreach (var delivery in this.repository.Data.PendingRemoteDeliveries.ToList())
        {
            if (delivery.CreatedDay <= 0)
            {
                delivery.CreatedDay = currentDay;
                delivery.CreatedTick = Game1.ticks;
                changed = true;
                continue;
            }

            if (currentDay - delivery.CreatedDay < PendingRemoteDeliveryRetentionDays)
                continue;

            if (PlayerHasPersistedReconciledRemoteDelivery(delivery.PlayerId, delivery.DeliveryId))
            {
                this.RemovePendingRemoteDelivery(delivery);
                removed++;
                changed = true;
            }
            else if (this.QueueDurableRemoteDeliveryForPlayer(delivery))
            {
                this.RemovePendingRemoteDelivery(delivery);
                queued++;
                changed = true;
            }
        }

        if (!changed)
            return 0;

        this.repository.Save();
        if (removed > 0 || queued > 0)
            this.monitor.Log($"Pruned {removed:N0} confirmed and queued {queued:N0} expired SVSAP remote delivery record(s).", LogLevel.Info);

        return removed + queued;
    }

    public int RestoreDurableRemoteDeliveries()
    {
        var player = Game1.player;
        if (player is null)
            return 0;

        var records = ReadDurableRemoteDeliveryRecords(player);
        if (records.Count == 0)
            return 0;

        var remaining = new List<DurableRemoteDeliveryRecord>();
        var restored = 0;
        var changed = false;
        foreach (var record in records)
        {
            if (record.DeliveryId == Guid.Empty
                || string.IsNullOrWhiteSpace(record.ReturnedSerializedItem)
                || record.ReturnedCount <= 0)
            {
                changed = true;
                continue;
            }

            try
            {
                var item = SerializedItemCodec.CreateItem(record.ReturnedSerializedItem, record.ReturnedCount);
                this.RestoreItemsToFarmer(player, new[] { item }, Game1.currentLocation);
                PersistReconciledRemoteDelivery(record.DeliveryId);
                restored++;
                changed = true;
            }
            catch (Exception ex)
            {
                remaining.Add(record);
                this.monitor.Log($"Could not restore durable SVSAP remote delivery {record.DeliveryId:N}: {ex.Message}", LogLevel.Warn);
            }
        }

        if (!changed)
            return 0;

        WriteDurableRemoteDeliveryRecords(player, remaining);
        if (restored > 0)
            this.monitor.Log($"Restored {restored:N0} durable SVSAP remote delivery item(s).", LogLevel.Warn);

        return restored;
    }

    public void SetPeerActionBlock(long playerId, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            this.blockedPeerActionMessages.Remove(playerId);
        else
            this.blockedPeerActionMessages[playerId] = message;
    }

    public void ClearPeerActionBlock(long playerId)
    {
        this.blockedPeerActionMessages.Remove(playerId);
    }

    private static void ClearCachedResponsesForPlayer<TResponse>(
        Dictionary<(long PlayerId, Guid TransactionId), TResponse> cache,
        Queue<(long PlayerId, Guid TransactionId)> order,
        long playerId)
    {
        if (cache.Count == 0)
            return;

        foreach (var key in cache.Keys.Where(key => key.PlayerId == playerId).ToList())
            cache.Remove(key);

        if (order.Count == 0)
            return;

        var retained = order.Where(key => key.PlayerId != playerId).ToList();
        order.Clear();
        foreach (var key in retained)
            order.Enqueue(key);
    }

    public void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != this.modManifest.UniqueID)
            return;

        try
        {
            if (e.Type == MultiplayerMessageTypes.TerminalSnapshotRequest)
                this.HandleTerminalSnapshotRequest(e);
            else if (e.Type == MultiplayerMessageTypes.TerminalSnapshotResponse)
                this.HandleTerminalSnapshotResponse(e);
            else if (e.Type == MultiplayerMessageTypes.TerminalActionRequest)
                this.HandleTerminalActionRequest(e);
            else if (e.Type == MultiplayerMessageTypes.TerminalActionResponse)
                this.HandleTerminalActionResponse(e);
            else if (e.Type == MultiplayerMessageTypes.CraftingSnapshotRequest)
                this.HandleCraftingSnapshotRequest(e);
            else if (e.Type == MultiplayerMessageTypes.CraftingSnapshotResponse)
                this.HandleCraftingSnapshotResponse(e);
            else if (e.Type == MultiplayerMessageTypes.CraftingActionRequest)
                this.HandleCraftingActionRequest(e);
            else if (e.Type == MultiplayerMessageTypes.CraftingActionResponse)
                this.HandleCraftingActionResponse(e);
            else if (e.Type == MultiplayerMessageTypes.CraftingMonitorSnapshotRequest)
                this.HandleCraftingMonitorSnapshotRequest(e);
            else if (e.Type == MultiplayerMessageTypes.CraftingMonitorSnapshotResponse)
                this.HandleCraftingMonitorSnapshotResponse(e);
            else if (e.Type == MultiplayerMessageTypes.CraftingMonitorActionRequest)
                this.HandleCraftingMonitorActionRequest(e);
            else if (e.Type == MultiplayerMessageTypes.CraftingMonitorActionResponse)
                this.HandleCraftingMonitorActionResponse(e);
            else if (e.Type == MultiplayerMessageTypes.StructuralSnapshotRequest)
                this.HandleStructuralSnapshotRequest(e);
            else if (e.Type == MultiplayerMessageTypes.StructuralSnapshotResponse)
                this.HandleStructuralSnapshotResponse(e);
            else if (e.Type == MultiplayerMessageTypes.StructuralActionRequest)
                this.HandleStructuralActionRequest(e);
            else if (e.Type == MultiplayerMessageTypes.StructuralActionResponse)
                this.HandleStructuralActionResponse(e);
            else if (e.Type == MultiplayerMessageTypes.RemoteDeliveryAck)
                this.HandleRemoteDeliveryAck(e);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to handle SVSAP multiplayer message '{e.Type}' from {e.FromPlayerID}: {ex.Message}", LogLevel.Warn);
        }
    }

    private void HandleLinkToolUse(SObject target, GameLocation location, Vector2 tile)
    {
        if (Context.IsMainPlayer)
            this.RunLinkLocally(target, location, tile);
        else
            this.SendLinkToolRequest(target, location, tile);
    }

    private void RunLinkLocally(SObject target, GameLocation location, Vector2 tile)
    {
        var held = Game1.player.CurrentItem;
        if (held is null)
            return;

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.NetworkCore)
        {
            this.RunStructuralLocally(StructuralActionKind.LinkSelectCore, target, location, tile);
            return;
        }

        if (!Guid.TryParse(held.modData.GetValueOrDefault(SelectedNetworkIdKey), out var selectedNetworkId))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("network.link.selectCoreFirst"), HUDMessage.error_type));
            this.LogGameplay($"action=link_endpoint result=fail player={DescribePlayer(Game1.player)} target={Quote(target.QualifiedItemId)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} reason=\"no_selected_network\"");
            return;
        }

        this.RunStructuralLocally(StructuralActionKind.LinkBindEndpoint, target, location, tile, selectedNetworkId);
    }

    private void SendLinkToolRequest(SObject target, GameLocation location, Vector2 tile)
    {
        var held = Game1.player.CurrentItem;
        if (held is null)
            return;

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.NetworkCore)
        {
            var req = this.CreateStructuralRequest(StructuralActionKind.LinkSelectCore, location, tile);
            this.SendStructuralRequest(req);
            return;
        }

        if (!Guid.TryParse(held.modData.GetValueOrDefault(SelectedNetworkIdKey), out var selectedNetworkId))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("network.link.selectCoreFirst"), HUDMessage.error_type));
            this.LogGameplay($"action=link_endpoint result=fail player={DescribePlayer(Game1.player)} target={Quote(target.QualifiedItemId)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} reason=\"no_selected_network\"");
            return;
        }

        var request = this.CreateStructuralRequest(StructuralActionKind.LinkBindEndpoint, location, tile, selectedNetworkId);
        this.SendStructuralRequest(request);
    }

    private void RunStructuralLocally(
        StructuralActionKind kind,
        SObject target,
        GameLocation location,
        Vector2 tile,
        Guid selectedNetworkId = default)
    {
        var held = Game1.player.CurrentItem;
        var result = this.ApplyStructuralAction(
            kind,
            target,
            location,
            tile,
            selectedNetworkId,
            held?.QualifiedItemId ?? string.Empty,
            held?.DisplayName ?? string.Empty,
            held?.Stack ?? 0,
            SerializeHeldItem(held));

        this.ReconcileStructuralResult(kind, result, location, showMessage: true, actingItem: held);
    }

    private StructuralActionRequestMessage CreateStructuralRequest(
        StructuralActionKind kind,
        GameLocation location,
        Vector2 tile,
        Guid selectedNetworkId = default)
    {
        var held = Game1.player.CurrentItem;
        return new StructuralActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            Kind = kind,
            LocationName = location.NameOrUniqueName,
            TileX = (int)tile.X,
            TileY = (int)tile.Y,
            SelectedNetworkId = selectedNetworkId,
            HeldQualifiedItemId = held?.QualifiedItemId ?? string.Empty,
            HeldDisplayName = held?.DisplayName ?? string.Empty,
            HeldStack = held?.Stack ?? 0,
            HeldSerializedItem = SerializeHeldItem(held)
        };
    }

    private void SendItemBearingStructuralRequest(
        StructuralActionKind kind,
        SObject target,
        GameLocation location,
        Vector2 tile)
    {
        var request = this.CreateStructuralRequest(kind, location, tile);
        this.SendStructuralRequest(request);
    }

    private void SendStructuralRequest(StructuralActionRequestMessage request)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAP"), HUDMessage.error_type));
            return;
        }

        if (!this.TrackStructuralActingItem(request, out var trackingMessage))
        {
            Game1.addHUDMessage(new HUDMessage(trackingMessage, HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        try
        {
            this.multiplayerHelper.SendMessage(
                request,
                MultiplayerMessageTypes.StructuralActionRequest,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
            if (this.pendingStructuralEscrowRequests.ContainsKey(request.TransactionId))
                this.MarkClientEscrowSent(request.TransactionId);
        }
        catch (Exception ex)
        {
            this.RestorePendingStructuralHeldItem(request.TransactionId);
            this.monitor.Log($"Failed to send SVSAP structural request {request.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("network.structural.sendFailed"), HUDMessage.error_type));
            return;
        }

        this.LogGameplay($"action=structural_request result=sent player={DescribePlayer(Game1.player)} host={host.PlayerID} kind={request.Kind} location={Quote(request.LocationName)} tile=({request.TileX:0},{request.TileY:0}) tx={ShortId(request.TransactionId)}");
    }

    private void RequestRemoteStructuralSnapshot(StructuralSnapshotKind kind, SObject target, GameLocation location, Vector2 tile)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAP"), HUDMessage.error_type));
            return;
        }

        var request = new StructuralSnapshotRequestMessage
        {
            Kind = kind,
            LocationName = location.NameOrUniqueName,
            TileX = (int)tile.X,
            TileY = (int)tile.Y
        };

        try
        {
            this.multiplayerHelper.SendMessage(
                request,
                MultiplayerMessageTypes.StructuralSnapshotRequest,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.requestSent"), HUDMessage.newQuest_type));
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to request SVSAP structural snapshot for {target.QualifiedItemId}: {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.sendFailed"), HUDMessage.error_type));
        }
    }

    private void SendRemoteStorageDriveAction(
        StructuralSnapshotResponseMessage snapshot,
        StructuralActionKind kind,
        int slotIndex,
        Item? heldItem)
    {
        this.SendStructuralRequest(new StructuralActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            Kind = kind,
            LocationName = snapshot.LocationName,
            TileX = snapshot.TileX,
            TileY = snapshot.TileY,
            SlotIndex = slotIndex,
            HeldQualifiedItemId = heldItem?.QualifiedItemId ?? string.Empty,
            HeldDisplayName = heldItem?.DisplayName ?? string.Empty,
            HeldStack = heldItem?.Stack ?? 0,
            HeldSerializedItem = SerializeHeldItem(heldItem)
        });
    }

    private void SendRemoteTransferBusAction(
        StructuralSnapshotResponseMessage snapshot,
        StructuralActionKind kind,
        int slotIndex = -1,
        string filterQualifiedItemId = "",
        int facingDirection = -1,
        Item? heldItem = null)
    {
        this.SendStructuralRequest(new StructuralActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            Kind = kind,
            LocationName = snapshot.LocationName,
            TileX = snapshot.TileX,
            TileY = snapshot.TileY,
            SlotIndex = slotIndex,
            FilterQualifiedItemId = filterQualifiedItemId,
            FacingDirection = facingDirection,
            HeldQualifiedItemId = heldItem?.QualifiedItemId ?? string.Empty,
            HeldDisplayName = heldItem?.DisplayName ?? string.Empty,
            HeldStack = heldItem?.Stack ?? 0,
            HeldSerializedItem = SerializeHeldItem(heldItem)
        });
    }

    private void SendRemotePatternProviderAction(
        StructuralSnapshotResponseMessage snapshot,
        StructuralActionKind kind,
        int slotIndex = -1,
        int actionValue = 0,
        Item? heldItem = null)
    {
        this.SendStructuralRequest(new StructuralActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            Kind = kind,
            LocationName = snapshot.LocationName,
            TileX = snapshot.TileX,
            TileY = snapshot.TileY,
            SlotIndex = slotIndex,
            ActionValue = actionValue,
            HeldQualifiedItemId = heldItem?.QualifiedItemId ?? string.Empty,
            HeldDisplayName = heldItem?.DisplayName ?? string.Empty,
            HeldStack = heldItem?.Stack ?? 0,
            HeldSerializedItem = SerializeHeldItem(heldItem)
        });
    }

    private bool TrackStructuralActingItem(StructuralActionRequestMessage request, out string message)
    {
        message = string.Empty;
        if (Context.IsMainPlayer || request.TransactionId == Guid.Empty)
            return true;

        var held = Game1.player.CurrentItem;
        if (held is null)
            return true;

        Item? escrowedItem = null;
        if (ShouldEscrowStructuralHeldItem(request.Kind, held))
        {
            if (!string.Equals(held.QualifiedItemId, request.HeldQualifiedItemId, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(request.HeldSerializedItem) && !SerializedPrototypeMatches(held, request.HeldSerializedItem)))
            {
                message = ModText.Get("network.structural.heldChanged");
                return false;
            }

            escrowedItem = held.getOne();
            escrowedItem.Stack = 1;
            held.Stack -= 1;
            if (held.Stack <= 0)
            {
                Game1.player.removeItemFromInventory(held);
            }
            else if (request.Kind == StructuralActionKind.StorageDriveInteract)
            {
                this.storageDriveService.ResetRemainingHeldStorageCell(held);
            }
        }

        var pending = new PendingStructuralHeldItem(held, escrowedItem);
        this.pendingStructuralHeldItems[request.TransactionId] = pending;
        if (escrowedItem is not null)
        {
            this.pendingStructuralEscrowRequests[request.TransactionId] = request;
            if (!this.TrackDurableStructuralEscrow(request))
            {
                this.pendingStructuralHeldItems.Remove(request.TransactionId);
                this.pendingStructuralEscrowRequests.Remove(request.TransactionId);
                this.RestoreEscrowedStructuralItem(pending, Game1.currentLocation);
                message = ModText.Get("network.structural.sendFailed");
                return false;
            }
        }

        return true;
    }

    private static bool ShouldEscrowStructuralHeldItem(StructuralActionKind kind, Item held)
    {
        return kind switch
        {
            StructuralActionKind.StorageDriveInteract or StructuralActionKind.StorageDriveInsertSlot
                => ModItemCatalog.TryGetStorageCellTier(held.QualifiedItemId, out _),
            StructuralActionKind.PatternProviderInteract or StructuralActionKind.PatternProviderInsertSlot
                => PatternCodec.IsPatternItem(held),
            StructuralActionKind.TransferBusConfigure
                => TransferBusService.IsConfigurationCard(held.QualifiedItemId),
            StructuralActionKind.TransferBusInsertUpgradeSlot
                => TransferBusService.IsUpgradeCard(held.QualifiedItemId),
            _ => false
        };
    }

    internal Guid SendStructuralActionForRouteSVSAPE(
        StructuralActionKind kind,
        GameLocation location,
        Vector2 tile,
        Guid selectedNetworkId = default)
    {
        var request = this.CreateStructuralRequest(kind, location, tile, selectedNetworkId);
        this.SendStructuralRequest(request);
        return request.TransactionId;
    }

    private void HandleStructuralSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<StructuralSnapshotRequestMessage>();
        var response = this.CreateStructuralSnapshotResponse(request);
        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.StructuralSnapshotResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { e.FromPlayerID });
    }

    private void HandleStructuralSnapshotResponse(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
            return;

        var response = e.ReadAs<StructuralSnapshotResponseMessage>();
        if (!response.Success)
        {
            Game1.addHUDMessage(new HUDMessage(
                LocalizeRemoteResponse(false, response.Message, "network.structural.succeeded", "Structure action completed.", "network.structural.failed", "Structure action failed; please retry."),
                HUDMessage.error_type));
            return;
        }

        if (response.Kind == StructuralSnapshotKind.StorageDrive && response.StorageDrive is not null)
        {
            if (Game1.activeClickableMenu is RemoteStorageDriveMenu activeStorage)
                activeStorage.ApplySnapshot(response);
            else
                Game1.activeClickableMenu = new RemoteStorageDriveMenu(
                    response,
                    (kind, slot, held) => this.SendRemoteStorageDriveAction(response, kind, slot, held),
                    () => this.RequestRemoteStructuralSnapshot(response));
            return;
        }

        if (response.Kind == StructuralSnapshotKind.TransferBus && response.TransferBus is not null)
        {
            if (Game1.activeClickableMenu is RemoteTransferBusMenu activeTransfer)
                activeTransfer.ApplySnapshot(response);
            else
                Game1.activeClickableMenu = new RemoteTransferBusMenu(
                    response,
                    (kind, slotIndex, itemId, direction, held) => this.SendRemoteTransferBusAction(response, kind, slotIndex, itemId, direction, held),
                    () => this.RequestRemoteStructuralSnapshot(response));
            return;
        }

        if (response.Kind == StructuralSnapshotKind.PatternProvider && response.PatternProvider is not null)
        {
            if (Game1.activeClickableMenu is RemotePatternProviderMenu activeProvider)
                activeProvider.ApplySnapshot(response);
            else
                Game1.activeClickableMenu = new RemotePatternProviderMenu(
                    response,
                    (kind, slotIndex, value, held) => this.SendRemotePatternProviderAction(response, kind, slotIndex, value, held),
                    () => this.RequestRemoteStructuralSnapshot(response));
        }
    }

    private void RequestRemoteStructuralSnapshot(StructuralSnapshotResponseMessage snapshot)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
            return;

        this.multiplayerHelper.SendMessage(
            new StructuralSnapshotRequestMessage
            {
                Kind = snapshot.Kind,
                LocationName = snapshot.LocationName,
                TileX = snapshot.TileX,
                TileY = snapshot.TileY
            },
            MultiplayerMessageTypes.StructuralSnapshotRequest,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { host.PlayerID });
    }

    private StructuralSnapshotResponseMessage CreateStructuralSnapshotResponse(StructuralSnapshotRequestMessage request)
    {
        var response = new StructuralSnapshotResponseMessage
        {
            Kind = request.Kind,
            LocationName = request.LocationName,
            TileX = request.TileX,
            TileY = request.TileY
        };

        var location = Game1.getLocationFromName(request.LocationName);
        var tile = new Vector2(request.TileX, request.TileY);
        if (location is null || !location.objects.TryGetValue(tile, out SObject? target))
        {
            response.Message = ModText.Get("network.structural.targetMissing");
            return response;
        }

        response.DisplayName = target.DisplayName;
        if (request.Kind == StructuralSnapshotKind.StorageDrive)
        {
            if (target.QualifiedItemId != "(BC)" + ModItemCatalog.StorageDrive)
            {
                response.Message = ModText.Get("ui.storageDrive.notDrive");
                return response;
            }

            response.Success = true;
            response.StorageDrive = new RemoteStorageDriveSnapshotMessage
            {
                SummaryLines = this.storageDriveService.DescribeDrive(target).ToList(),
                Slots = this.storageDriveService.GetSlotViews(target)
                    .Select(slot => new RemoteStorageDriveSlotMessage
                    {
                        SlotIndex = slot.SlotIndex,
                        Occupied = slot.Occupied,
                        QualifiedItemId = slot.Item?.QualifiedItemId ?? string.Empty,
                        DisplayName = slot.DisplayName,
                        CapacityUsed = slot.CapacityUsed,
                        CapacityMax = slot.CapacityMax,
                        TypesUsed = slot.TypesUsed,
                        TypesMax = slot.TypesMax
                    })
                    .ToList()
            };
            return response;
        }

        if (request.Kind == StructuralSnapshotKind.TransferBus)
        {
            if (target.QualifiedItemId is not ("(BC)" + ModItemCatalog.Importer) and not ("(BC)" + ModItemCatalog.Exporter))
            {
                response.Message = ModText.Get("ui.transferBus.notBus");
                return response;
            }

            response.Success = true;
            response.TransferBus = new RemoteTransferBusSnapshotMessage
            {
                IsExporter = target.QualifiedItemId == "(BC)" + ModItemCatalog.Exporter,
                FilterBlacklist = this.transferBusService.IsFilterBlacklistModeEnabled(target),
                OreDictionaryMode = this.transferBusService.IsOreDictionaryModeEnabled(target),
                QualityStrategy = this.transferBusService.GetConfiguredQualityStrategy(target),
                FacingDirection = this.transferBusService.GetFacingDirection(target),
                ConfigurationLines = this.transferBusService.DescribeConfigurationLines(target).ToList(),
                UpgradeSlotCapacity = TransferBusService.UpgradeSlotCount,
                UpgradeSlots = this.transferBusService.GetUpgradeSlotViews(target)
                    .Select(slot => new RemoteTransferUpgradeSlotMessage
                    {
                        SlotIndex = slot.SlotIndex,
                        Occupied = slot.Occupied,
                        QualifiedItemId = slot.QualifiedItemId,
                        DisplayName = slot.DisplayName
                    })
                    .ToList(),
                FilterSlots = this.transferBusService.GetFilterSlotViews(target)
                    .Select(slot => new RemoteTransferFilterSlotMessage
                    {
                        SlotIndex = slot.SlotIndex,
                        Occupied = slot.Occupied,
                        QualifiedItemId = slot.QualifiedItemId,
                        DisplayName = slot.DisplayName,
                        OreGroups = slot.OreGroups.ToList()
                    })
                    .ToList()
            };
            return response;
        }

        if (request.Kind == StructuralSnapshotKind.PatternProvider)
        {
            if (target.QualifiedItemId != "(BC)" + ModItemCatalog.PatternProvider)
            {
                response.Message = ModText.Get("ui.patternProvider.notProvider");
                return response;
            }

            response.Success = true;
            response.PatternProvider = new RemotePatternProviderSnapshotMessage
            {
                Priority = this.patternProviderService.GetPriority(target),
                Slots = this.patternProviderService.GetSlotViews(target)
                    .Select(slot => new RemotePatternProviderSlotMessage
                    {
                        SlotIndex = slot.SlotIndex,
                        SerializedItem = SerializedItemCodec.SerializePrototype(slot.Item),
                        DisplayName = slot.Item.DisplayName
                    })
                    .ToList()
            };
            return response;
        }

        response.Message = ModText.Get("network.structural.unknownAction");
        return response;
    }

    private void HandleStructuralActionRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        StructuralActionRequestMessage? request = null;
        try
        {
            request = e.ReadAs<StructuralActionRequestMessage>();
            if (this.TryGetPeerActionBlock(e.FromPlayerID, out var blockMessage))
            {
                var blocked = CreateStructuralFailureResponse(request, blockMessage);
                this.RememberStructuralActionResponse(e.FromPlayerID, request.TransactionId, blocked);
                this.SendStructuralResponse(blocked, e.FromPlayerID);
                return;
            }

            if (this.TryGetCachedStructuralActionResponse(e.FromPlayerID, request.TransactionId, out var cached))
            {
                this.SendStructuralResponse(cached, e.FromPlayerID);
                return;
            }

            if (this.TryGetPendingRemoteDelivery(e.FromPlayerID, request.TransactionId, RemoteDeliveryKind.StructuralReturnedItem, out var pendingDelivery))
            {
                var pendingResponse = this.CreateStructuralResponse(pendingDelivery);
                this.RememberStructuralActionResponse(e.FromPlayerID, request.TransactionId, pendingResponse);
                this.SendStructuralResponse(pendingResponse, e.FromPlayerID);
                return;
            }

            var response = this.ExecuteStructuralAction(request, e.FromPlayerID);
            this.RememberStructuralActionResponse(e.FromPlayerID, request.TransactionId, response);
            this.SendStructuralResponse(response, e.FromPlayerID);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to execute SVSAP structural request from {e.FromPlayerID}: {ex.Message}", LogLevel.Warn);
            if (request is null || request.TransactionId == Guid.Empty)
                return;

            var response = CreateStructuralFailureResponse(request);
            this.RememberStructuralActionResponse(e.FromPlayerID, request.TransactionId, response);

            try
            {
                this.SendStructuralResponse(response, e.FromPlayerID);
            }
            catch (Exception sendEx)
            {
                this.monitor.Log($"Failed to send SVSAP structural failure response to {e.FromPlayerID}: {sendEx.Message}", LogLevel.Warn);
            }
        }
    }

    private static StructuralActionResponseMessage CreateStructuralFailureResponse(StructuralActionRequestMessage request, string? message = null)
    {
        return new StructuralActionResponseMessage
        {
            TransactionId = request.TransactionId,
            Kind = request.Kind,
            Message = message ?? ModText.Get("network.structural.failedRestored")
        };
    }

    private void SendStructuralResponse(StructuralActionResponseMessage response, long playerId)
    {
        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.StructuralActionResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { playerId });
    }

    private void SendRemoteDeliveryAck(Guid deliveryId, Guid transactionId)
    {
        if (deliveryId == Guid.Empty || transactionId == Guid.Empty || Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
            return;

        try
        {
            this.multiplayerHelper.SendMessage(
                new RemoteDeliveryAckMessage
                {
                    DeliveryId = deliveryId,
                    TransactionId = transactionId
                },
                MultiplayerMessageTypes.RemoteDeliveryAck,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to ACK SVSAP remote delivery {deliveryId:N}: {ex.Message}", LogLevel.Trace);
        }
    }

    private void HandleRemoteDeliveryAck(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var ack = e.ReadAs<RemoteDeliveryAckMessage>();
        if (ack.DeliveryId == Guid.Empty || ack.TransactionId == Guid.Empty)
            return;

        var removed = this.repository.Data.PendingRemoteDeliveries.RemoveAll(delivery =>
            delivery.PlayerId == e.FromPlayerID
            && delivery.DeliveryId == ack.DeliveryId
            && delivery.TransactionId == ack.TransactionId);
        if (removed <= 0)
            return;

        var key = (e.FromPlayerID, ack.TransactionId);
        this.terminalActionResponseCache.Remove(key);
        this.structuralResponseCache.Remove(key);
        this.repository.Save();
    }

    private void RemovePendingRemoteDelivery(PendingRemoteDelivery delivery)
    {
        this.repository.Data.PendingRemoteDeliveries.Remove(delivery);
        var key = (delivery.PlayerId, delivery.TransactionId);
        this.terminalActionResponseCache.Remove(key);
        this.structuralResponseCache.Remove(key);
    }

    private bool QueueDurableRemoteDeliveryForPlayer(PendingRemoteDelivery delivery)
    {
        if (delivery.DeliveryId == Guid.Empty
            || delivery.PlayerId <= 0
            || string.IsNullOrWhiteSpace(delivery.ReturnedSerializedItem)
            || delivery.ReturnedCount <= 0)
        {
            return false;
        }

        var player = Game1.GetPlayer(delivery.PlayerId, onlyOnline: false);
        if (player is null)
            return false;

        try
        {
            var records = ReadDurableRemoteDeliveryRecords(player)
                .Where(record => record.DeliveryId != delivery.DeliveryId)
                .ToList();
            records.Add(new DurableRemoteDeliveryRecord
            {
                DeliveryId = delivery.DeliveryId,
                TransactionId = delivery.TransactionId,
                Message = delivery.Message,
                ReturnedSerializedItem = delivery.ReturnedSerializedItem,
                ReturnedCount = delivery.ReturnedCount,
                CreatedDay = delivery.CreatedDay
            });

            while (records.Count > ClientReconcileCacheLimit)
                records.RemoveAt(0);

            WriteDurableRemoteDeliveryRecords(player, records);
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to queue expired SVSAP remote delivery {delivery.DeliveryId:N} for player {delivery.PlayerId}: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private bool TryGetCachedStructuralActionResponse(long playerId, Guid transactionId, out StructuralActionResponseMessage response)
    {
        response = null!;
        return transactionId != Guid.Empty
            && this.structuralResponseCache.TryGetValue((playerId, transactionId), out response!);
    }

    private StructuralActionResponseMessage CreateStructuralResponse(PendingRemoteDelivery delivery)
    {
        return new StructuralActionResponseMessage
        {
            TransactionId = delivery.TransactionId,
            Kind = delivery.StructuralKind,
            Success = true,
            Message = delivery.Message,
            DeliveryId = delivery.DeliveryId,
            ReturnedSerializedItem = delivery.ReturnedSerializedItem,
            ResultNetworkId = delivery.ResultNetworkId
        };
    }

    private void RememberStructuralActionResponse(long playerId, Guid transactionId, StructuralActionResponseMessage response)
    {
        if (transactionId == Guid.Empty)
            return;

        var key = (playerId, transactionId);
        if (this.structuralResponseCache.ContainsKey(key))
            return;

        this.structuralResponseCache[key] = response;
        this.structuralResponseOrder.Enqueue(key);
        while (this.structuralResponseOrder.Count > ActionResponseCacheLimit)
            this.structuralResponseCache.Remove(this.structuralResponseOrder.Dequeue());
    }

    private void HandleStructuralActionResponse(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
            return;

        var response = e.ReadAs<StructuralActionResponseMessage>();
        if (response.DeliveryId != Guid.Empty && this.IsRemoteDeliveryReconciled(response.DeliveryId))
        {
            this.SendRemoteDeliveryAck(response.DeliveryId, response.TransactionId);
            this.FinalizeClientActionEscrow(response.TransactionId);
            this.RefreshActiveRemoteStructuralMenu();
            return;
        }

        this.StructuralActionResponseReceived?.Invoke(response);

        if (response.TransactionId != Guid.Empty && !this.MarkClientTransactionReconciled(response.TransactionId, this.reconciledStructuralTx, this.reconciledStructuralTxOrder))
        {
            if (this.IsRemoteDeliveryReconciled(response.DeliveryId))
            {
                this.SendRemoteDeliveryAck(response.DeliveryId, response.TransactionId);
            }
            else if (response.Success
                     && response.DeliveryId != Guid.Empty
                     && !string.IsNullOrWhiteSpace(response.ReturnedSerializedItem)
                     && this.DeliverStructuralReturnedItem(response.ReturnedSerializedItem, Game1.currentLocation))
            {
                this.MarkRemoteDeliveryReconciled(response.DeliveryId);
                this.SendRemoteDeliveryAck(response.DeliveryId, response.TransactionId);
            }

            this.FinalizeClientActionEscrow(response.TransactionId);
            this.RefreshActiveRemoteStructuralMenu();
            return;
        }

        Game1.addHUDMessage(new HUDMessage(
            LocalizeRemoteResponse(response.Success, response.Message, "network.structural.succeeded", "Structure action completed.", "network.structural.failed", "Structure action failed; please retry."),
            response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        this.pendingStructuralHeldItems.TryGetValue(response.TransactionId, out var pending);
        this.pendingStructuralHeldItems.Remove(response.TransactionId);

        if (!response.Success)
        {
            if (pending is not null)
                this.RestoreEscrowedStructuralItem(pending, Game1.currentLocation);

            this.FinalizeClientActionEscrow(response.TransactionId);
            this.RefreshActiveRemoteStructuralMenu();
            return;
        }

        if (pending?.EscrowedItem is not null && !response.ConsumeHeldOne)
            this.RestoreEscrowedStructuralItem(pending, Game1.currentLocation);

        var result = new StructuralActionResult
        {
            Success = response.Success,
            Message = response.Message,
            ConsumeHeldOne = response.ConsumeHeldOne,
            ReturnedSerializedItem = response.ReturnedSerializedItem,
            ResultNetworkId = response.ResultNetworkId
        };
        var reconciled = this.ReconcileStructuralResult(
            response.Kind,
            result,
            Game1.currentLocation,
            showMessage: false,
            actingItem: pending?.ActingItem,
            consumeHeldAlreadyApplied: pending?.EscrowedItem is not null && response.ConsumeHeldOne);
        if (response.DeliveryId != Guid.Empty && reconciled)
        {
            this.MarkRemoteDeliveryReconciled(response.DeliveryId);
            this.SendRemoteDeliveryAck(response.DeliveryId, response.TransactionId);
        }

        this.FinalizeClientActionEscrow(response.TransactionId);
        this.RefreshActiveRemoteStructuralMenu();
    }

    private void RefreshActiveRemoteStructuralMenu()
    {
        if (Game1.activeClickableMenu is RemoteStorageDriveMenu storageMenu)
            this.RequestRemoteStructuralSnapshot(storageMenu.Snapshot);
        else if (Game1.activeClickableMenu is RemoteTransferBusMenu transferMenu)
            this.RequestRemoteStructuralSnapshot(transferMenu.Snapshot);
        else if (Game1.activeClickableMenu is RemotePatternProviderMenu providerMenu)
            this.RequestRemoteStructuralSnapshot(providerMenu.Snapshot);
    }

    private StructuralActionResponseMessage ExecuteStructuralAction(StructuralActionRequestMessage request, long fromPlayerId)
    {
        var response = new StructuralActionResponseMessage
        {
            TransactionId = request.TransactionId,
            Kind = request.Kind
        };

        var location = Game1.getLocationFromName(request.LocationName);
        var tile = new Vector2(request.TileX, request.TileY);
        if (location is null || !location.objects.TryGetValue(tile, out SObject? target))
        {
            response.Message = ModText.Get("network.structural.targetMissing");
            return response;
        }

        var player = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        if (player is null)
        {
            response.Message = ModText.Get("multiplayer.requestPlayerOffline");
            return response;
        }

        var heldSerializedItem = request.HeldSerializedItem;
        if (this.StructuralActionMayConsumeHeldItem(request) && string.IsNullOrWhiteSpace(heldSerializedItem))
        {
            response.Message = ModText.Get("network.structural.heldChanged");
            return response;
        }

        var result = this.ApplyStructuralAction(
            request.Kind,
            target,
            location,
            tile,
            request.SelectedNetworkId,
            request.HeldQualifiedItemId,
            request.HeldDisplayName,
            request.HeldStack,
            heldSerializedItem,
            request.SlotIndex,
            request.FilterQualifiedItemId,
            request.FacingDirection,
            request.ActionValue);

        response.Success = result.Success;
        response.Message = result.Message;
        response.ConsumeHeldOne = result.ConsumeHeldOne;
        response.ReturnedSerializedItem = result.ReturnedSerializedItem;
        response.ResultNetworkId = result.ResultNetworkId;
        if (this.RegisterStructuralRemoteDelivery(fromPlayerId, request, response))
            this.repository.Save();
        return response;
    }

    private bool StructuralActionMayConsumeHeldItem(StructuralActionRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(request.HeldQualifiedItemId))
            return false;

        if (!string.IsNullOrWhiteSpace(request.HeldSerializedItem))
        {
            try
            {
                var held = SerializedItemCodec.CreateItem(request.HeldSerializedItem, 1);
                if (ShouldEscrowStructuralHeldItem(request.Kind, held))
                    return true;
            }
            catch
            {
                // Fall back to qualified-id checks below.
            }
        }

        return request.Kind switch
        {
            StructuralActionKind.StorageDriveInteract or StructuralActionKind.StorageDriveInsertSlot
                => ModItemCatalog.TryGetStorageCellTier(request.HeldQualifiedItemId, out _),
            StructuralActionKind.PatternProviderInteract or StructuralActionKind.PatternProviderInsertSlot
                => request.HeldQualifiedItemId is "(O)" + ModItemCatalog.CraftingPattern
                    or "(O)" + ModItemCatalog.ProcessingPattern,
            StructuralActionKind.TransferBusConfigure
                => TransferBusService.IsConfigurationCard(request.HeldQualifiedItemId),
            StructuralActionKind.TransferBusInsertUpgradeSlot
                => TransferBusService.IsUpgradeCard(request.HeldQualifiedItemId),
            _ => false
        };
    }

    private static bool SerializedPrototypeMatches(Item item, string expectedSerializedPrototype)
    {
        try
        {
            return string.Equals(
                SerializedItemCodec.SerializePrototype(item.getOne()),
                expectedSerializedPrototype,
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private StructuralActionResult ApplyStructuralAction(
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
        int facingDirection = -1,
        int actionValue = 0)
    {
        return kind switch
        {
            StructuralActionKind.LinkSelectCore => this.ApplyLinkSelectCore(target, location, tile),
            StructuralActionKind.LinkBindEndpoint => this.ApplyLinkBindEndpoint(target, location, tile, selectedNetworkId),
            StructuralActionKind.StorageDriveInteract => this.storageDriveService.ApplyInteract(target, location, tile, heldQualifiedItemId, heldSerializedItem),
            StructuralActionKind.PatternProviderInteract => this.patternProviderService.ApplyInteract(target, heldQualifiedItemId, heldSerializedItem),
            StructuralActionKind.TransferBusConfigure => this.transferBusService.ApplyConfigure(target, heldQualifiedItemId, heldDisplayName, heldStack),
            StructuralActionKind.StorageDriveInsertSlot => this.storageDriveService.ApplyInsertCellSlot(target, slotIndex, heldQualifiedItemId, heldSerializedItem),
            StructuralActionKind.StorageDriveEjectSlot => this.storageDriveService.ApplyEjectCellSlot(target, slotIndex),
            StructuralActionKind.PatternProviderInsertSlot => this.patternProviderService.ApplyInsertPatternSlot(target, slotIndex, heldQualifiedItemId, heldSerializedItem),
            StructuralActionKind.PatternProviderEjectSlot => this.patternProviderService.ApplyEjectPatternSlot(target, slotIndex),
            StructuralActionKind.PatternProviderMoveSlot => this.patternProviderService.ApplyMovePatternSlot(target, slotIndex, actionValue),
            StructuralActionKind.PatternProviderAdjustPriority => this.patternProviderService.ApplyAdjustPriority(target, actionValue),
            StructuralActionKind.TransferBusToggleFilterMode => this.transferBusService.ApplyToggleFilterMode(target),
            StructuralActionKind.TransferBusToggleOreDictionary => this.transferBusService.ApplyToggleOreDictionaryMode(target),
            StructuralActionKind.TransferBusToggleQuality => this.transferBusService.ApplyToggleQualityStrategy(target),
            StructuralActionKind.TransferBusClearFilter => this.transferBusService.ApplyClearFilter(target),
            StructuralActionKind.TransferBusSetFacing => this.transferBusService.ApplySetFacingDirection(target, facingDirection),
            StructuralActionKind.TransferBusSetFilterSlot => this.transferBusService.ApplySetFilterSlot(target, slotIndex, filterQualifiedItemId),
            StructuralActionKind.TransferBusClearFilterSlot => this.transferBusService.ApplyClearFilterSlot(target, slotIndex),
            StructuralActionKind.TransferBusInsertUpgradeSlot => this.transferBusService.ApplyInsertUpgradeSlot(target, slotIndex, heldQualifiedItemId, heldSerializedItem),
            StructuralActionKind.TransferBusEjectUpgradeSlot => this.transferBusService.ApplyEjectUpgradeSlot(target, slotIndex),
            _ => StructuralActionResult.Fail(ModText.Get("network.structural.unknownAction"))
        };
    }

    private StructuralActionResult ApplyLinkSelectCore(SObject target, GameLocation location, Vector2 tile)
    {
        if (target.QualifiedItemId != "(BC)" + ModItemCatalog.NetworkCore)
            return StructuralActionResult.Fail(ModText.Get("network.link.selectCoreFirst"));

        var networkId = this.endpointIdentityService.EnsureNetworkId(target);
        var endpointId = this.endpointIdentityService.EnsureEndpointId(target);
        target.modData[EndpointIdentityService.NetworkIdKey] = networkId.ToString("N");
        this.repository.UpsertEndpoint(networkId, this.CreateEndpoint(endpointId, location, tile, EndpointType.NetworkCore));

        return new StructuralActionResult
        {
            Success = true,
            Message = ModText.Get("network.link.selected"),
            ResultNetworkId = networkId
        };
    }

    private StructuralActionResult ApplyLinkBindEndpoint(SObject target, GameLocation location, Vector2 tile, Guid selectedNetworkId)
    {
        if (selectedNetworkId == Guid.Empty)
            return StructuralActionResult.Fail(ModText.Get("network.link.selectCoreFirst"));

        var endpointType = this.GetEndpointType(target);
        if (endpointType is null)
            return StructuralActionResult.Fail(ModText.Get("network.link.unsupportedObject"));

        var boundEndpointId = this.endpointIdentityService.EnsureEndpointId(target);
        var hadPreviousNetworkId = target.modData.TryGetValue(EndpointIdentityService.NetworkIdKey, out var previousNetworkIdRaw);
        var previousNetworkId = Guid.TryParse(previousNetworkIdRaw, out var parsedPreviousNetworkId)
            ? parsedPreviousNetworkId
            : (Guid?)null;
        var selectedNetwork = this.repository.GetOrCreateNetwork(selectedNetworkId);
        if (selectedNetwork.Endpoints.All(endpoint => endpoint.EndpointId != boundEndpointId)
            && selectedNetwork.Endpoints.Count >= Math.Max(1, this.getConfig().MaxEndpointsPerNetwork))
        {
            return StructuralActionResult.Fail(ModText.Get("network.link.endpointLimit"));
        }

        target.modData[EndpointIdentityService.NetworkIdKey] = selectedNetworkId.ToString("N");
        var endpoint = this.CreateEndpoint(boundEndpointId, location, tile, endpointType.Value);
        if (!this.IsEndpointConnected(selectedNetwork, endpoint, out var connectionMessage))
        {
            if (hadPreviousNetworkId)
                target.modData[EndpointIdentityService.NetworkIdKey] = previousNetworkIdRaw!;
            else
                target.modData.Remove(EndpointIdentityService.NetworkIdKey);

            return StructuralActionResult.Fail(connectionMessage);
        }

        this.MoveEndpointStateToNetwork(previousNetworkId, selectedNetworkId, boundEndpointId);
        this.repository.UpsertEndpoint(selectedNetworkId, endpoint);
        return new StructuralActionResult
        {
            Success = true,
            Message = ModText.Get("network.link.bound")
        };
    }

    private bool ReconcileStructuralResult(
        StructuralActionKind kind,
        StructuralActionResult result,
        GameLocation? location,
        bool showMessage,
        Item? actingItem = null,
        bool consumeHeldAlreadyApplied = false)
    {
        if (showMessage)
            Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        if (!result.Success)
            return false;

        if (kind == StructuralActionKind.LinkSelectCore && result.ResultNetworkId != Guid.Empty)
        {
            var tool = actingItem;
            if (tool is not null)
                tool.modData[SelectedNetworkIdKey] = result.ResultNetworkId.ToString("N");
        }

        if (result.ConsumeHeldOne)
        {
            var held = actingItem;
            if (consumeHeldAlreadyApplied)
            {
                if (kind == StructuralActionKind.StorageDriveInteract
                    && held is not null
                    && Game1.player.Items.IndexOf(held) >= 0
                    && held.Stack > 0)
                {
                    this.storageDriveService.ResetRemainingHeldStorageCell(held);
                }
            }
            else if (held is null || Game1.player.Items.IndexOf(held) < 0 || held.Stack <= 0)
            {
                this.monitor.Log("structural reconcile skipped: held item changed before response", LogLevel.Warn);
            }
            else
            {
                held.Stack -= 1;
                if (held.Stack <= 0)
                {
                    Game1.player.removeItemFromInventory(held);
                }
                else if (kind == StructuralActionKind.StorageDriveInteract)
                {
                    this.storageDriveService.ResetRemainingHeldStorageCell(held);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(result.ReturnedSerializedItem))
            return true;

        return this.DeliverStructuralReturnedItem(result.ReturnedSerializedItem, location);
    }

    private bool DeliverStructuralReturnedItem(string serializedItem, GameLocation? location)
    {
        try
        {
            var item = SerializedItemCodec.CreateItem(serializedItem, 1);
            if (!Game1.player.addItemToInventoryBool(item))
                Game1.createItemDebris(item, Game1.player.getStandingPosition(), -1, location ?? Game1.currentLocation);
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not reconcile structural returned item: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private static string SerializeHeldItem(Item? held)
    {
        return held is null
            ? string.Empty
            : SerializedItemCodec.SerializePrototype(held);
    }

    private bool TrackDurableTerminalEscrow(TerminalActionRequestMessage request)
    {
        return this.TrackDurableClientEscrow(new DurableClientActionEscrowRecord
        {
            TransactionId = request.TransactionId,
            Kind = DurableClientActionEscrowRecord.TerminalKind,
            TerminalRequest = request
        });
    }

    private bool TrackDurableStructuralEscrow(StructuralActionRequestMessage request)
    {
        return this.TrackDurableClientEscrow(new DurableClientActionEscrowRecord
        {
            TransactionId = request.TransactionId,
            Kind = DurableClientActionEscrowRecord.StructuralKind,
            StructuralRequest = request
        });
    }

    private bool TrackDurableClientEscrow(DurableClientActionEscrowRecord record)
    {
        var player = Game1.player;
        if (player is null || record.TransactionId == Guid.Empty)
            return false;

        try
        {
            if (!this.TryReadDurableClientEscrows(player, out var existing))
                return false;

            var records = existing
                .Where(candidate => candidate.TransactionId != record.TransactionId)
                .ToList();
            records.Add(record);
            this.WriteDurableClientEscrows(player, records);
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to persist SVSAP client action escrow {record.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    public int RehydrateDurableClientEscrows()
    {
        if (Context.IsMainPlayer || Game1.player is not Farmer player)
            return 0;

        if (!this.TryReadDurableClientEscrows(player, out var records))
            return 0;

        var loaded = 0;
        foreach (var record in records)
        {
            if (record.TransactionId == Guid.Empty)
                continue;

            try
            {
                if (string.Equals(record.Kind, DurableClientActionEscrowRecord.TerminalKind, StringComparison.Ordinal)
                    && record.TerminalRequest is { } terminalRequest
                    && terminalRequest.TransactionId == record.TransactionId
                    && terminalRequest.DepositItems.Count > 0)
                {
                    this.pendingTerminalDepositItems[record.TransactionId] = CloneTerminalPayloads(terminalRequest.DepositItems);
                    this.pendingTerminalEscrowRequests[record.TransactionId] = terminalRequest;
                    this.clientEscrowRetryStates[record.TransactionId] = ClientEscrowRetryState.CreateReadyForRetry();
                    loaded++;
                    continue;
                }

                if (string.Equals(record.Kind, DurableClientActionEscrowRecord.StructuralKind, StringComparison.Ordinal)
                    && record.StructuralRequest is { } structuralRequest
                    && structuralRequest.TransactionId == record.TransactionId
                    && !string.IsNullOrWhiteSpace(structuralRequest.HeldSerializedItem))
                {
                    var item = SerializedItemCodec.CreateItem(structuralRequest.HeldSerializedItem, 1);
                    this.pendingStructuralHeldItems[record.TransactionId] = new PendingStructuralHeldItem(item, item);
                    this.pendingStructuralEscrowRequests[record.TransactionId] = structuralRequest;
                    this.clientEscrowRetryStates[record.TransactionId] = ClientEscrowRetryState.CreateReadyForRetry();
                    loaded++;
                    continue;
                }

                this.monitor.Log($"Durable SVSAP client action escrow {record.TransactionId:N} has no replayable request and remains quarantined.", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Failed to rehydrate SVSAP client action escrow {record.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            }
        }

        if (loaded > 0)
            this.monitor.Log($"Rehydrated {loaded:N0} durable SVSAP client action escrow(s) for host reconciliation.", LogLevel.Warn);

        return loaded;
    }

    public void RetryDurableClientEscrows()
    {
        this.RetryDurableClientEscrows(force: true);
    }

    public void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (Context.IsMainPlayer || !Context.IsWorldReady || !e.IsMultipleOf(30))
            return;

        this.RetryDurableClientEscrows(force: false);
    }

    private void RetryDurableClientEscrows(bool force)
    {
        if (this.pendingTerminalEscrowRequests.Count == 0 && this.pendingStructuralEscrowRequests.Count == 0)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        var hostMod = host?.HasSmapi == true ? host.GetMod(this.modManifest.UniqueID) : null;
        if (host is null
            || hostMod is null
            || !string.Equals(hostMod.Version.ToString(), this.modManifest.Version.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var pair in this.pendingTerminalEscrowRequests.ToList())
            this.RetryClientEscrowRequest(pair.Key, pair.Value, MultiplayerMessageTypes.TerminalActionRequest, host.PlayerID, force);

        foreach (var pair in this.pendingStructuralEscrowRequests.ToList())
            this.RetryClientEscrowRequest(pair.Key, pair.Value, MultiplayerMessageTypes.StructuralActionRequest, host.PlayerID, force);
    }

    private void RetryClientEscrowRequest<TRequest>(Guid transactionId, TRequest request, string messageType, long hostPlayerId, bool force)
    {
        if (!this.clientEscrowRetryStates.TryGetValue(transactionId, out var retry))
        {
            retry = ClientEscrowRetryState.CreateReadyForRetry();
            this.clientEscrowRetryStates[transactionId] = retry;
        }

        var tick = Game1.ticks;
        if (force)
        {
            retry.RetryCount = 0;
            retry.RetryLimitNotified = false;
            retry.LastSentTick = tick - ClientEscrowResponseTimeoutTicks;
        }

        var elapsed = tick >= retry.LastSentTick
            ? tick - retry.LastSentTick
            : ClientEscrowResponseTimeoutTicks;
        if (elapsed < ClientEscrowResponseTimeoutTicks)
            return;

        if (retry.RetryCount >= ClientEscrowRetryLimit)
        {
            if (!retry.RetryLimitNotified)
            {
                retry.RetryLimitNotified = true;
                Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.escrowReconcilePending"), HUDMessage.error_type));
            }
            return;
        }

        try
        {
            this.multiplayerHelper.SendMessage(
                request,
                messageType,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { hostPlayerId });
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to retry SVSAP client action escrow {transactionId:N}: {ex.Message}", LogLevel.Trace);
        }
        finally
        {
            retry.LastSentTick = tick;
            retry.RetryCount++;
        }
    }

    private void MarkClientEscrowSent(Guid transactionId)
    {
        if (transactionId == Guid.Empty)
            return;

        this.clientEscrowRetryStates[transactionId] = new ClientEscrowRetryState
        {
            LastSentTick = Game1.ticks
        };
    }

    private void FinalizeClientActionEscrow(Guid transactionId)
    {
        if (transactionId == Guid.Empty)
            return;

        this.pendingTerminalDepositItems.Remove(transactionId);
        this.pendingStructuralHeldItems.Remove(transactionId);
        this.pendingTerminalEscrowRequests.Remove(transactionId);
        this.pendingStructuralEscrowRequests.Remove(transactionId);
        this.clientEscrowRetryStates.Remove(transactionId);
        this.RemoveDurableClientEscrow(transactionId);
    }

    private void RemoveDurableClientEscrow(Guid transactionId)
    {
        var player = Game1.player;
        if (player is null || transactionId == Guid.Empty)
            return;

        try
        {
            if (!this.TryReadDurableClientEscrows(player, out var existing))
                return;

            var records = existing.Where(record => record.TransactionId != transactionId).ToList();
            this.WriteDurableClientEscrows(player, records);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to clear SVSAP client action escrow {transactionId:N}: {ex.Message}", LogLevel.Trace);
        }
    }

    private bool TryReadDurableClientEscrows(Farmer player, out List<DurableClientActionEscrowRecord> records)
    {
        records = new List<DurableClientActionEscrowRecord>();
        if (!player.modData.TryGetValue(DurableClientActionEscrowModDataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            records = JsonSerializer.Deserialize<List<DurableClientActionEscrowRecord>>(raw) ?? new List<DurableClientActionEscrowRecord>();
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not parse durable SVSAP client action escrows; payload remains quarantined: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private void WriteDurableClientEscrows(Farmer player, IReadOnlyCollection<DurableClientActionEscrowRecord> records)
    {
        if (records.Count == 0)
        {
            player.modData.Remove(DurableClientActionEscrowModDataKey);
            return;
        }

        player.modData[DurableClientActionEscrowModDataKey] = JsonSerializer.Serialize(records);
    }

    private void RestorePendingStructuralHeldItems()
    {
        foreach (var pair in this.pendingStructuralHeldItems.ToList())
        {
            this.RestoreEscrowedStructuralItem(pair.Value, Game1.currentLocation);
            this.FinalizeClientActionEscrow(pair.Key);
        }

        this.pendingStructuralHeldItems.Clear();
    }

    private void RestorePendingStructuralHeldItem(Guid transactionId)
    {
        if (transactionId == Guid.Empty || !this.pendingStructuralHeldItems.Remove(transactionId, out var pending))
            return;

        this.RestoreEscrowedStructuralItem(pending, Game1.currentLocation);
        this.FinalizeClientActionEscrow(transactionId);
    }

    private void RestorePendingTerminalDepositItems()
    {
        foreach (var pair in this.pendingTerminalDepositItems.ToList())
        {
            this.RestoreTerminalDepositPayloads(pair.Value, Game1.currentLocation);
            this.FinalizeClientActionEscrow(pair.Key);
        }

        this.pendingTerminalDepositItems.Clear();
    }

    private void RestorePendingTerminalDepositItems(Guid transactionId, IReadOnlyList<TerminalItemPayloadMessage> fallbackPayloads)
    {
        if (transactionId != Guid.Empty && this.pendingTerminalDepositItems.Remove(transactionId, out var pending))
        {
            this.RestoreTerminalDepositPayloads(pending, Game1.currentLocation);
            this.FinalizeClientActionEscrow(transactionId);
            return;
        }

        this.RestoreTerminalDepositPayloads(fallbackPayloads, Game1.currentLocation);
        this.FinalizeClientActionEscrow(transactionId);
    }

    private void RestoreEscrowedStructuralItem(PendingStructuralHeldItem pending, GameLocation? location)
    {
        var item = pending.EscrowedItem;
        if (item is null)
            return;

        this.RestoreItemsToPlayer(new[] { item }, location);
    }

    private bool DeliverRemoteTerminalWithdrawal(TerminalActionResponseMessage response)
    {
        if (string.IsNullOrWhiteSpace(response.ReturnedSerializedItem) || response.ReturnedCount <= 0)
            return true;

        try
        {
            var item = SerializedItemCodec.CreateItem(response.ReturnedSerializedItem, response.ReturnedCount);
            this.RestoreItemsToPlayer(new[] { item }, Game1.currentLocation);
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not deliver remote terminal withdrawal item: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private void RestoreTerminalDepositPayloads(IEnumerable<TerminalItemPayloadMessage> payloads, GameLocation? location)
    {
        foreach (var payload in payloads)
        {
            if (string.IsNullOrWhiteSpace(payload.SerializedItem) || payload.Count <= 0)
                continue;

            try
            {
                var item = SerializedItemCodec.CreateItem(payload.SerializedItem, payload.Count);
                this.RestoreItemsToPlayer(new[] { item }, location);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Could not restore remote terminal escrow payload: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    private void RestoreItemsToPlayer(IEnumerable<Item> items, GameLocation? location)
    {
        if (Game1.player is not null)
            this.RestoreItemsToFarmer(Game1.player, items, location);
    }

    private void RestoreItemsToFarmer(Farmer player, IEnumerable<Item> items, GameLocation? location)
    {
        var dropLocation = player.currentLocation ?? location ?? Game1.currentLocation;
        foreach (var item in items)
        {
            if (item.Stack <= 0)
                continue;

            if (player.addItemToInventoryBool(item))
                continue;

            if (Context.IsWorldReady && dropLocation is not null)
                Game1.createItemDebris(item, player.getStandingPosition(), -1, dropLocation);
        }
    }

    private static string LocalizeRemoteResponse(bool success, string? responseMessage, string successKey, string successFallback, string failureKey, string failureFallback)
    {
        if (!success && !string.IsNullOrWhiteSpace(responseMessage))
            return responseMessage;

        return success
            ? ModText.Get(successKey, successFallback)
            : ModText.Get(failureKey, failureFallback);
    }

    private static List<TerminalItemPayloadMessage> CloneTerminalPayloads(IEnumerable<TerminalItemPayloadMessage> payloads)
    {
        return payloads
            .Where(payload => !string.IsNullOrWhiteSpace(payload.SerializedItem) && payload.Count > 0)
            .Select(payload => new TerminalItemPayloadMessage
            {
                SerializedItem = payload.SerializedItem,
                Count = payload.Count
            })
            .ToList();
    }

    internal static int NormalizeTerminalSnapshotEntryLimit(int requestedLimit)
    {
        if (requestedLimit <= 0)
            return RemoteTerminalSnapshotDefaultEntryLimit;

        return Math.Clamp(requestedLimit, 1, RemoteTerminalSnapshotMaxEntryLimit);
    }

    private static TerminalItemPayloadMessage CreateTerminalPayload(Item item)
    {
        var prototype = item.getOne();
        prototype.Stack = 1;
        return new TerminalItemPayloadMessage
        {
            SerializedItem = SerializedItemCodec.SerializePrototype(prototype),
            Count = item.Stack
        };
    }

    private bool TryGetPeerActionBlock(long playerId, out string message)
    {
        return this.blockedPeerActionMessages.TryGetValue(playerId, out message!);
    }

    private bool MarkClientTransactionReconciled(Guid transactionId, HashSet<Guid> seen, Queue<Guid> order)
    {
        if (transactionId == Guid.Empty)
            return true;

        if (!seen.Add(transactionId))
            return false;

        order.Enqueue(transactionId);
        while (order.Count > ClientReconcileCacheLimit)
            seen.Remove(order.Dequeue());

        return true;
    }

    private bool IsRemoteDeliveryReconciled(Guid deliveryId)
    {
        return deliveryId != Guid.Empty
            && (this.reconciledRemoteDeliveries.Contains(deliveryId) || HasPersistedReconciledRemoteDelivery(deliveryId));
    }

    private void MarkRemoteDeliveryReconciled(Guid deliveryId)
    {
        if (deliveryId == Guid.Empty)
            return;

        if (this.reconciledRemoteDeliveries.Add(deliveryId))
        {
            this.reconciledRemoteDeliveryOrder.Enqueue(deliveryId);
            while (this.reconciledRemoteDeliveryOrder.Count > ClientReconcileCacheLimit)
                this.reconciledRemoteDeliveries.Remove(this.reconciledRemoteDeliveryOrder.Dequeue());
        }

        PersistReconciledRemoteDelivery(deliveryId);
    }

    private static bool HasPersistedReconciledRemoteDelivery(Guid deliveryId)
    {
        return ReadPersistedReconciledRemoteDeliveries(Game1.player).Contains(deliveryId);
    }

    private static bool PlayerHasPersistedReconciledRemoteDelivery(long playerId, Guid deliveryId)
    {
        return ReadPersistedReconciledRemoteDeliveries(Game1.GetPlayer(playerId, onlyOnline: false)).Contains(deliveryId);
    }

    private static void PersistReconciledRemoteDelivery(Guid deliveryId)
    {
        var player = Game1.player;
        if (player is null || deliveryId == Guid.Empty)
            return;

        var deliveries = ReadPersistedReconciledRemoteDeliveries(player);
        if (deliveries.Contains(deliveryId))
            return;

        deliveries.Add(deliveryId);
        while (deliveries.Count > ClientReconcileCacheLimit)
            deliveries.RemoveAt(0);

        player.modData[ReconciledRemoteDeliveryModDataKey] = string.Join("|", deliveries.Select(id => id.ToString("N")));
    }

    private static List<Guid> ReadPersistedReconciledRemoteDeliveries(Farmer? player)
    {
        if (player is null
            || !player.modData.TryGetValue(ReconciledRemoteDeliveryModDataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return new List<Guid>();
        }

        var deliveries = new List<Guid>();
        foreach (var piece in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(piece.Trim(), out var parsed))
                deliveries.Add(parsed);
        }

        return deliveries;
    }

    private static List<DurableRemoteDeliveryRecord> ReadDurableRemoteDeliveryRecords(Farmer? player)
    {
        if (player is null
            || !player.modData.TryGetValue(DurableRemoteDeliveryModDataKey, out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return new List<DurableRemoteDeliveryRecord>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<DurableRemoteDeliveryRecord>>(raw) ?? new List<DurableRemoteDeliveryRecord>();
        }
        catch
        {
            return new List<DurableRemoteDeliveryRecord>();
        }
    }

    private static void WriteDurableRemoteDeliveryRecords(Farmer player, IReadOnlyCollection<DurableRemoteDeliveryRecord> records)
    {
        if (records.Count == 0)
        {
            player.modData.Remove(DurableRemoteDeliveryModDataKey);
            return;
        }

        player.modData[DurableRemoteDeliveryModDataKey] = JsonSerializer.Serialize(records);
    }

    private void MoveEndpointStateToNetwork(Guid? previousNetworkId, Guid selectedNetworkId, Guid endpointId)
    {
        if (!previousNetworkId.HasValue || previousNetworkId.Value == selectedNetworkId)
            return;

        if (!this.repository.TryGetNetwork(previousNetworkId.Value, out var previousNetwork))
            return;

        var selectedNetwork = this.repository.GetOrCreateNetwork(selectedNetworkId);
        previousNetwork.Endpoints.RemoveAll(endpoint => endpoint.EndpointId == endpointId);

        if (previousNetwork.TransferBuses.Remove(endpointId, out var bus))
        {
            bus.EndpointId = endpointId;
            selectedNetwork.TransferBuses[endpointId] = bus;
        }

        if (previousNetwork.PatternProviders.Remove(endpointId, out var provider))
        {
            provider.EndpointId = endpointId;
            selectedNetwork.PatternProviders[endpointId] = provider;
        }

        if (previousNetwork.StorageDrives.Remove(endpointId, out var drive))
        {
            drive.EndpointId = endpointId;
            selectedNetwork.StorageDrives[endpointId] = drive;
        }
    }

    public void RefreshEndpointConnectivity()
    {
        var changed = false;
        foreach (var network in this.repository.Data.Networks.Values)
        {
            foreach (var endpoint in network.Endpoints)
            {
                var active = this.IsEndpointConnected(network, endpoint, out _);
                if (endpoint.Active == active)
                    continue;

                endpoint.Active = active;
                changed = true;
            }
        }

        if (changed)
            this.repository.Save();
    }

    private void OpenTerminal(SObject terminal, bool crafting)
    {
        if (!this.TryGetActiveLinkedNetwork(terminal, ModText.Get("network.label.terminal"), out var network, out var endpointId, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            return;
        }

        var guard = this.CreateLocalEndpointGuard(network, endpointId, EndpointType.NetworkTerminal);
        Game1.activeClickableMenu = crafting
            ? new CraftingTerminalMenu(network, this.inventoryScanner, this.craftingRecipeService, guard)
            : new NetworkTerminalMenu(network, this.inventoryScanner, this.transactionService, guard);
        this.LogGameplay($"action=open_terminal result=success player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} endpoint={ShortId(endpointId)} terminal={(crafting ? "crafting" : "storage")}");
    }

    private void OpenStorageDriveMenu(SObject drive, GameLocation location, Vector2 tile)
    {
        Game1.activeClickableMenu = new StorageDriveMenu(drive, location, tile, this.storageDriveService);
    }

    private void OpenPatternProviderMenu(SObject provider)
    {
        Game1.activeClickableMenu = new PatternProviderMenu(provider, this.patternProviderService);
    }

    private void OpenTransferBusMenu(SObject bus)
    {
        Game1.activeClickableMenu = new TransferBusMenu(bus, this.transferBusService);
    }

    private void OpenCraftingMonitor(SObject monitorObject)
    {
        if (!this.TryGetActiveLinkedNetwork(monitorObject, ModText.Get("network.label.craftingMonitor"), out var network, out var endpointId, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            return;
        }

        this.patternExecutionService.TryHandleCraftingMonitorAction(
            monitorObject,
            this.CreateLocalEndpointGuard(network, endpointId, EndpointType.CraftingMonitor));
        this.LogGameplay($"action=open_crafting_monitor result=success player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} endpoint={ShortId(endpointId)}");
    }

    private void RequestRemoteTerminalSnapshot(SObject terminal, bool crafting)
    {
        if (crafting)
        {
            this.RequestRemoteCraftingSnapshot(terminal);
            return;
        }

        if (!this.TryReadLinkedEndpointIds(terminal, ModText.Get("network.label.terminal"), out var networkId, out var endpointId, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            return;
        }

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAPTerminal"), HUDMessage.error_type));
            return;
        }

        this.SendRemoteTerminalSnapshotRequest(networkId, endpointId, entryOffset: 0, entryLimit: RemoteTerminalSnapshotDefaultEntryLimit);
    }

    private void SendRemoteTerminalSnapshotRequest(Guid networkId, Guid endpointId, int entryOffset, int entryLimit)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAPTerminal"), HUDMessage.error_type));
            return;
        }

        var request = new TerminalSnapshotRequestMessage
        {
            NetworkId = networkId,
            EndpointId = endpointId,
            Crafting = false,
            EntryOffset = Math.Max(0, entryOffset),
            EntryLimit = NormalizeTerminalSnapshotEntryLimit(entryLimit)
        };

        this.multiplayerHelper.SendMessage(
            request,
            MultiplayerMessageTypes.TerminalSnapshotRequest,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { host.PlayerID });

        Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.snapshotRequested"), HUDMessage.newQuest_type));
    }

    private void RequestRemoteCraftingSnapshot(SObject terminal)
    {
        if (!this.TryReadLinkedEndpointIds(terminal, ModText.Get("network.label.terminal"), out var networkId, out var endpointId, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            return;
        }

        this.SendRemoteCraftingSnapshotRequest(networkId, endpointId, batches: 1, MaterialQualityStrategy.LowQualityFirst);
    }

    private void RequestRemoteCraftingMonitorSnapshot(SObject monitorObject)
    {
        if (!this.TryReadLinkedEndpointIds(monitorObject, ModText.Get("network.label.craftingMonitor"), out var networkId, out var endpointId, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            return;
        }

        this.SendRemoteCraftingMonitorSnapshotRequest(networkId, endpointId);
    }

    private void SendRemoteCraftingSnapshotRequest(Guid networkId, int batches, MaterialQualityStrategy qualityStrategy)
    {
        this.SendRemoteCraftingSnapshotRequest(networkId, Guid.Empty, batches, qualityStrategy);
    }

    private void SendRemoteCraftingSnapshotRequest(Guid networkId, Guid endpointId, int batches, MaterialQualityStrategy qualityStrategy)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAPCrafting"), HUDMessage.error_type));
            return;
        }

        this.multiplayerHelper.SendMessage(
            new CraftingSnapshotRequestMessage
            {
                NetworkId = networkId,
                EndpointId = endpointId,
                Batches = Math.Max(1, batches),
                QualityStrategy = qualityStrategy
            },
            MultiplayerMessageTypes.CraftingSnapshotRequest,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { host.PlayerID });

        Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteCrafting.snapshotRequested"), HUDMessage.newQuest_type));
    }

    private void SendRemoteCraftingMonitorSnapshotRequest(Guid networkId)
    {
        this.SendRemoteCraftingMonitorSnapshotRequest(networkId, Guid.Empty);
    }

    private void SendRemoteCraftingMonitorSnapshotRequest(Guid networkId, Guid endpointId)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAPMonitor"), HUDMessage.error_type));
            return;
        }

        this.multiplayerHelper.SendMessage(
            new CraftingMonitorSnapshotRequestMessage
            {
                NetworkId = networkId,
                EndpointId = endpointId,
                HeldPattern = GetHeldPattern(),
                HeldCaskItemPrototype = this.GetHeldCaskPipelineItemPrototype()
            },
            MultiplayerMessageTypes.CraftingMonitorSnapshotRequest,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { host.PlayerID });

        Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteMonitor.snapshotRequested"), HUDMessage.newQuest_type));
    }

    private void HandleTerminalSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<TerminalSnapshotRequestMessage>();
        if (request.Crafting)
        {
            var craftingResponse = this.CreateCraftingSnapshotResponse(
                new CraftingSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId },
                e.FromPlayerID);

            this.multiplayerHelper.SendMessage(
                craftingResponse,
                MultiplayerMessageTypes.CraftingSnapshotResponse,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { e.FromPlayerID });
            return;
        }

        var response = this.CreateTerminalSnapshotResponse(request);

        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.TerminalSnapshotResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { e.FromPlayerID });
    }

    private void HandleTerminalSnapshotResponse(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
            return;

        var response = e.ReadAs<TerminalSnapshotResponseMessage>();
        if (!response.Success)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.snapshotFailed", "SVSAP terminal snapshot failed; please retry."), HUDMessage.error_type));
            return;
        }

        if (response.PushUpdate)
        {
            if (Game1.activeClickableMenu is RemoteNetworkTerminalMenu pushMenu && pushMenu.MatchesNetwork(response.NetworkId))
                pushMenu.ApplyPushUpdate(response);

            return;
        }

        if (Game1.activeClickableMenu is RemoteNetworkTerminalMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
            remoteMenu.ApplySnapshot(response);
        else
            Game1.activeClickableMenu = this.CreateRemoteTerminalMenu(response);

        Game1.playSound("bigSelect");
    }

    private RemoteNetworkTerminalMenu CreateRemoteTerminalMenu(TerminalSnapshotResponseMessage snapshot)
    {
        return new RemoteNetworkTerminalMenu(
            snapshot,
            this.SendRemoteTerminalActionRequest,
            this.SendRemoteTerminalSnapshotRequest);
    }

    private bool SendRemoteTerminalActionRequest(TerminalActionRequestMessage request, TerminalSnapshotResponseMessage snapshot)
    {
        if (Context.IsMainPlayer)
            return false;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            this.RestoreTerminalDepositPayloads(request.DepositItems, Game1.currentLocation);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAPTerminal"), HUDMessage.error_type));
            return false;
        }

        if (request.TransactionId != Guid.Empty && request.DepositItems.Count > 0)
        {
            this.pendingTerminalDepositItems[request.TransactionId] = CloneTerminalPayloads(request.DepositItems);
            this.pendingTerminalEscrowRequests[request.TransactionId] = request;
            if (!this.TrackDurableTerminalEscrow(request))
            {
                this.pendingTerminalEscrowRequests.Remove(request.TransactionId);
                this.RestorePendingTerminalDepositItems(request.TransactionId, request.DepositItems);
                Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.sendFailed"), HUDMessage.error_type));
                return false;
            }
        }

        try
        {
            this.multiplayerHelper.SendMessage(
                request,
                MultiplayerMessageTypes.TerminalActionRequest,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
            if (this.pendingTerminalEscrowRequests.ContainsKey(request.TransactionId))
                this.MarkClientEscrowSent(request.TransactionId);
        }
        catch (Exception ex)
        {
            this.RestorePendingTerminalDepositItems(request.TransactionId, request.DepositItems);
            this.monitor.Log($"Failed to send SVSAP terminal request {request.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.sendFailed"), HUDMessage.error_type));
            return false;
        }

        this.LogGameplay($"action=remote_terminal_request result=sent player={DescribePlayer(Game1.player)} host={host.PlayerID} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} requestAction={request.Action} amount={request.Amount:N0} tx={ShortId(request.TransactionId)}");
        Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.requestSent"), HUDMessage.newQuest_type));
        return true;
    }

    private void HandleTerminalActionRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        TerminalActionRequestMessage? request = null;
        try
        {
            request = e.ReadAs<TerminalActionRequestMessage>();
            if (this.TryGetPeerActionBlock(e.FromPlayerID, out var blockMessage))
            {
                var blocked = CreateTerminalFailureResponse(request, blockMessage);
                this.RememberTerminalActionResponse(e.FromPlayerID, request.TransactionId, blocked);
                this.SendTerminalActionResponse(blocked, e.FromPlayerID);
                return;
            }

            if (this.TryGetCachedTerminalActionResponse(e.FromPlayerID, request.TransactionId, out var cached))
            {
                this.SendTerminalActionResponse(cached, e.FromPlayerID);
                return;
            }

            if (this.TryGetPendingRemoteDelivery(e.FromPlayerID, request.TransactionId, RemoteDeliveryKind.TerminalWithdraw, out var pendingDelivery))
            {
                var pendingResponse = this.CreateTerminalResponse(pendingDelivery);
                this.RememberTerminalActionResponse(e.FromPlayerID, request.TransactionId, pendingResponse);
                this.SendTerminalActionResponse(pendingResponse, e.FromPlayerID);
                return;
            }

            var response = this.ExecuteTerminalActionRequest(request, e.FromPlayerID);
            this.RememberTerminalActionResponse(e.FromPlayerID, request.TransactionId, response);

            this.SendTerminalActionResponse(response, e.FromPlayerID);
            if (response.Success && response.Snapshot is not null && response.Snapshot.Success)
                this.BroadcastTerminalSnapshotUpdate(response.Snapshot, e.FromPlayerID);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to execute SVSAP terminal request from {e.FromPlayerID}: {ex.Message}", LogLevel.Warn);
            if (request is null || request.TransactionId == Guid.Empty)
                return;

            var response = CreateTerminalFailureResponse(request);
            this.RememberTerminalActionResponse(e.FromPlayerID, request.TransactionId, response);

            try
            {
                this.SendTerminalActionResponse(response, e.FromPlayerID);
            }
            catch (Exception sendEx)
            {
                this.monitor.Log($"Failed to send SVSAP terminal failure response to {e.FromPlayerID}: {sendEx.Message}", LogLevel.Warn);
            }
        }
    }

    private static TerminalActionResponseMessage CreateTerminalFailureResponse(TerminalActionRequestMessage request, string? message = null)
    {
        return new TerminalActionResponseMessage
        {
            TransactionId = request.TransactionId,
            NetworkId = request.NetworkId,
            Message = message ?? ModText.Get("remoteTerminal.actionFailed"),
            ReturnedDepositItems = CloneTerminalPayloads(request.DepositItems)
        };
    }

    private void SendTerminalActionResponse(TerminalActionResponseMessage response, long playerId)
    {
        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.TerminalActionResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { playerId });
    }

    private void BroadcastTerminalSnapshotUpdate(TerminalSnapshotResponseMessage snapshot, long exceptPlayerId)
    {
        var playerIds = this.GetRemotePlayerIdsForPush(exceptPlayerId);
        if (playerIds.Length == 0)
            return;

        var push = new TerminalSnapshotResponseMessage
        {
            NetworkId = snapshot.NetworkId,
            EndpointId = snapshot.EndpointId,
            Crafting = snapshot.Crafting,
            PushUpdate = true,
            Success = snapshot.Success,
            Message = snapshot.Message,
            NetworkName = snapshot.NetworkName,
            SourceCount = snapshot.SourceCount,
            TotalEntryCount = snapshot.TotalEntryCount,
            StorageSummary = snapshot.StorageSummary,
            LockedQualifiedItemIds = snapshot.LockedQualifiedItemIds
        };

        try
        {
            this.multiplayerHelper.SendMessage(
                push,
                MultiplayerMessageTypes.TerminalSnapshotResponse,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: playerIds);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to broadcast SVSAP terminal snapshot update: {ex.Message}", LogLevel.Warn);
        }
    }

    private bool TryGetCachedTerminalActionResponse(long playerId, Guid transactionId, out TerminalActionResponseMessage response)
    {
        response = null!;
        return transactionId != Guid.Empty
            && this.terminalActionResponseCache.TryGetValue((playerId, transactionId), out response!);
    }

    private TerminalActionResponseMessage CreateTerminalResponse(PendingRemoteDelivery delivery)
    {
        var response = new TerminalActionResponseMessage
        {
            TransactionId = delivery.TransactionId,
            NetworkId = delivery.NetworkId,
            Success = true,
            Message = delivery.Message,
            DeliveryId = delivery.DeliveryId,
            ReturnedSerializedItem = delivery.ReturnedSerializedItem,
            ReturnedCount = delivery.ReturnedCount
        };

        if (this.repository.TryGetNetwork(delivery.NetworkId, out _))
        {
            response.Snapshot = this.CreateTerminalSnapshotResponse(new TerminalSnapshotRequestMessage
            {
                NetworkId = delivery.NetworkId,
                EndpointId = delivery.EndpointId
            });
        }

        return response;
    }

    private bool TryGetPendingRemoteDelivery(long playerId, Guid transactionId, RemoteDeliveryKind kind, out PendingRemoteDelivery delivery)
    {
        delivery = null!;
        if (playerId <= 0 || transactionId == Guid.Empty)
            return false;

        delivery = this.repository.Data.PendingRemoteDeliveries.FirstOrDefault(pending =>
            pending.PlayerId == playerId
            && pending.TransactionId == transactionId
            && pending.Kind == kind)!;
        return delivery is not null;
    }

    private bool RegisterTerminalRemoteDelivery(long playerId, TerminalActionRequestMessage request, TerminalActionResponseMessage response)
    {
        if (!response.Success
            || string.IsNullOrWhiteSpace(response.ReturnedSerializedItem)
            || response.ReturnedCount <= 0
            || playerId <= 0
            || request.TransactionId == Guid.Empty)
        {
            return false;
        }

        var delivery = this.GetOrCreatePendingRemoteDelivery(playerId, request.TransactionId, RemoteDeliveryKind.TerminalWithdraw);
        delivery.TerminalAction = request.Action;
        delivery.NetworkId = request.NetworkId;
        delivery.EndpointId = request.EndpointId;
        delivery.Message = response.Message;
        delivery.ReturnedSerializedItem = response.ReturnedSerializedItem;
        delivery.ReturnedCount = response.ReturnedCount;
        StampPendingRemoteDelivery(delivery);
        response.DeliveryId = delivery.DeliveryId;
        return true;
    }

    private bool RegisterStructuralRemoteDelivery(long playerId, StructuralActionRequestMessage request, StructuralActionResponseMessage response)
    {
        if (!response.Success
            || string.IsNullOrWhiteSpace(response.ReturnedSerializedItem)
            || playerId <= 0
            || request.TransactionId == Guid.Empty)
        {
            return false;
        }

        var delivery = this.GetOrCreatePendingRemoteDelivery(playerId, request.TransactionId, RemoteDeliveryKind.StructuralReturnedItem);
        delivery.StructuralKind = request.Kind;
        delivery.ResultNetworkId = response.ResultNetworkId;
        delivery.Message = response.Message;
        delivery.ReturnedSerializedItem = response.ReturnedSerializedItem;
        delivery.ReturnedCount = 1;
        StampPendingRemoteDelivery(delivery);
        response.DeliveryId = delivery.DeliveryId;
        return true;
    }

    private PendingRemoteDelivery GetOrCreatePendingRemoteDelivery(long playerId, Guid transactionId, RemoteDeliveryKind kind)
    {
        if (this.TryGetPendingRemoteDelivery(playerId, transactionId, kind, out var existing))
            return existing;

        var delivery = new PendingRemoteDelivery
        {
            DeliveryId = Guid.NewGuid(),
            PlayerId = playerId,
            TransactionId = transactionId,
            Kind = kind
        };
        StampPendingRemoteDelivery(delivery);
        this.repository.Data.PendingRemoteDeliveries.Add(delivery);
        return delivery;
    }

    private static void StampPendingRemoteDelivery(PendingRemoteDelivery delivery)
    {
        if (delivery.CreatedDay <= 0 && Context.IsWorldReady)
            delivery.CreatedDay = Game1.Date.TotalDays;

        if (delivery.CreatedTick <= 0)
            delivery.CreatedTick = Game1.ticks;
    }

    private void RememberTerminalActionResponse(long playerId, Guid transactionId, TerminalActionResponseMessage response)
    {
        if (transactionId == Guid.Empty)
            return;

        var key = (playerId, transactionId);
        if (this.terminalActionResponseCache.ContainsKey(key))
            return;

        this.terminalActionResponseCache[key] = response;
        this.terminalActionResponseOrder.Enqueue(key);
        while (this.terminalActionResponseOrder.Count > ActionResponseCacheLimit)
            this.terminalActionResponseCache.Remove(this.terminalActionResponseOrder.Dequeue());
    }

    private void HandleTerminalActionResponse(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
            return;

        var response = e.ReadAs<TerminalActionResponseMessage>();
        if (response.DeliveryId != Guid.Empty && this.IsRemoteDeliveryReconciled(response.DeliveryId))
        {
            this.SendRemoteDeliveryAck(response.DeliveryId, response.TransactionId);
            this.FinalizeClientActionEscrow(response.TransactionId);
            return;
        }

        if (response.TransactionId != Guid.Empty && !this.MarkClientTransactionReconciled(response.TransactionId, this.reconciledTerminalTx, this.reconciledTerminalTxOrder))
        {
            if (this.IsRemoteDeliveryReconciled(response.DeliveryId))
            {
                this.SendRemoteDeliveryAck(response.DeliveryId, response.TransactionId);
            }
            else if (response.Success && response.DeliveryId != Guid.Empty && this.DeliverRemoteTerminalWithdrawal(response))
            {
                this.MarkRemoteDeliveryReconciled(response.DeliveryId);
                this.SendRemoteDeliveryAck(response.DeliveryId, response.TransactionId);
            }

            this.FinalizeClientActionEscrow(response.TransactionId);
            return;
        }

        this.pendingTerminalDepositItems.TryGetValue(response.TransactionId, out var pendingDepositItems);
        this.pendingTerminalDepositItems.Remove(response.TransactionId);

        if (response.Success)
        {
            var delivered = this.DeliverRemoteTerminalWithdrawal(response);
            if (response.DeliveryId != Guid.Empty && delivered)
            {
                this.MarkRemoteDeliveryReconciled(response.DeliveryId);
                this.SendRemoteDeliveryAck(response.DeliveryId, response.TransactionId);
            }
            this.RestoreTerminalDepositPayloads(response.ReturnedDepositItems, Game1.currentLocation);
        }
        else if (response.ReturnedDepositItems.Count > 0)
        {
            this.RestoreTerminalDepositPayloads(response.ReturnedDepositItems, Game1.currentLocation);
        }
        else if (pendingDepositItems is not null)
        {
            this.RestoreTerminalDepositPayloads(pendingDepositItems, Game1.currentLocation);
        }

        Game1.addHUDMessage(new HUDMessage(
            LocalizeRemoteResponse(response.Success, response.Message, "remoteTerminal.actionSucceeded", "SVSAP terminal action completed.", "remoteTerminal.actionFailed", "SVSAP terminal action failed; please retry."),
            response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        this.FinalizeClientActionEscrow(response.TransactionId);

        if (Game1.activeClickableMenu is RemoteNetworkTerminalMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
        {
            remoteMenu.MarkActionComplete(response.Snapshot);
        }
    }

    private void HandleCraftingSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<CraftingSnapshotRequestMessage>();
        var response = this.CreateCraftingSnapshotResponse(request, e.FromPlayerID);

        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.CraftingSnapshotResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { e.FromPlayerID });
    }

    private void HandleCraftingSnapshotResponse(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
            return;

        var response = e.ReadAs<CraftingSnapshotResponseMessage>();
        if (!response.Success)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteCrafting.snapshotFailed", "SVSAP crafting snapshot failed; please retry."), HUDMessage.error_type));
            return;
        }

        if (response.PushUpdate)
        {
            if (Game1.activeClickableMenu is RemoteCraftingTerminalMenu pushMenu && pushMenu.MatchesNetwork(response.NetworkId))
                pushMenu.ApplyPushUpdate(response);

            return;
        }

        if (Game1.activeClickableMenu is RemoteCraftingTerminalMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
            remoteMenu.ApplySnapshot(response);
        else
            Game1.activeClickableMenu = this.CreateRemoteCraftingMenu(response);

        Game1.playSound("bigSelect");
    }

    private bool SendRemoteCraftingActionRequest(CraftingActionRequestMessage request)
    {
        if (Context.IsMainPlayer)
            return false;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAPCrafting"), HUDMessage.error_type));
            return false;
        }

        try
        {
            this.multiplayerHelper.SendMessage(
                request,
                MultiplayerMessageTypes.CraftingActionRequest,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to send SVSAP crafting request {request.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteCrafting.sendFailed"), HUDMessage.error_type));
            return false;
        }

        this.LogGameplay($"action=remote_crafting_request result=sent player={DescribePlayer(Game1.player)} host={host.PlayerID} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} recipe={Quote(request.RecipeName)} batches={request.Batches:N0} quality={request.QualityStrategy} tx={ShortId(request.TransactionId)}");
        Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteCrafting.requestSent"), HUDMessage.newQuest_type));
        return true;
    }

    private void HandleCraftingActionRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<CraftingActionRequestMessage>();
        if (this.TryGetPeerActionBlock(e.FromPlayerID, out var blockMessage))
        {
            var blocked = new CraftingActionResponseMessage
            {
                TransactionId = request.TransactionId,
                NetworkId = request.NetworkId,
                Message = blockMessage
            };
            this.RememberCraftingActionResponse(e.FromPlayerID, request.TransactionId, blocked);
            this.SendCraftingActionResponse(blocked, e.FromPlayerID);
            return;
        }

        if (this.TryGetCachedCraftingActionResponse(e.FromPlayerID, request.TransactionId, out var cached))
        {
            this.SendCraftingActionResponse(cached, e.FromPlayerID);
            return;
        }

        var response = this.ExecuteCraftingActionRequest(request, e.FromPlayerID);
        this.RememberCraftingActionResponse(e.FromPlayerID, request.TransactionId, response);

        this.SendCraftingActionResponse(response, e.FromPlayerID);
        if (response.Snapshot is not null && response.Snapshot.Success)
            this.BroadcastCraftingSnapshotUpdate(response.Snapshot, e.FromPlayerID);
    }

    private void SendCraftingActionResponse(CraftingActionResponseMessage response, long playerId)
    {
        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.CraftingActionResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { playerId });
    }

    private void BroadcastCraftingSnapshotUpdate(CraftingSnapshotResponseMessage snapshot, long exceptPlayerId)
    {
        var playerIds = this.GetRemotePlayerIdsForPush(exceptPlayerId);
        if (playerIds.Length == 0)
            return;

        var push = new CraftingSnapshotResponseMessage
        {
            NetworkId = snapshot.NetworkId,
            EndpointId = snapshot.EndpointId,
            PushUpdate = true,
            Success = snapshot.Success,
            Message = snapshot.Message,
            NetworkName = snapshot.NetworkName,
            NetworkItemTypes = snapshot.NetworkItemTypes
        };

        try
        {
            this.multiplayerHelper.SendMessage(
                push,
                MultiplayerMessageTypes.CraftingSnapshotResponse,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: playerIds);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to broadcast SVSAP crafting snapshot update: {ex.Message}", LogLevel.Warn);
        }
    }

    private long[] GetRemotePlayerIdsForPush(long exceptPlayerId)
    {
        if (!Context.IsMainPlayer)
            return Array.Empty<long>();

        return this.multiplayerHelper.GetConnectedPlayers()
            .Where(peer => !peer.IsHost
                && peer.PlayerID != exceptPlayerId
                && peer.HasSmapi
                && peer.GetMod(this.modManifest.UniqueID) is not null)
            .Select(peer => peer.PlayerID)
            .Distinct()
            .ToArray();
    }

    private bool TryGetCachedCraftingActionResponse(long playerId, Guid transactionId, out CraftingActionResponseMessage response)
    {
        response = null!;
        return transactionId != Guid.Empty
            && this.craftingActionResponseCache.TryGetValue((playerId, transactionId), out response!);
    }

    private void RememberCraftingActionResponse(long playerId, Guid transactionId, CraftingActionResponseMessage response)
    {
        if (transactionId == Guid.Empty)
            return;

        var key = (playerId, transactionId);
        if (this.craftingActionResponseCache.ContainsKey(key))
            return;

        this.craftingActionResponseCache[key] = response;
        this.craftingActionResponseOrder.Enqueue(key);
        while (this.craftingActionResponseOrder.Count > ActionResponseCacheLimit)
            this.craftingActionResponseCache.Remove(this.craftingActionResponseOrder.Dequeue());
    }

    private void HandleCraftingActionResponse(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
            return;

        var response = e.ReadAs<CraftingActionResponseMessage>();
        if (response.TransactionId != Guid.Empty && !this.MarkClientTransactionReconciled(response.TransactionId, this.reconciledCraftingTx, this.reconciledCraftingTxOrder))
            return;

        Game1.addHUDMessage(new HUDMessage(
            LocalizeRemoteResponse(response.Success, response.Message, "remoteCrafting.actionSucceeded", "SVSAP crafting action completed.", "remoteCrafting.actionFailed", "SVSAP crafting action failed; please retry."),
            response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        if (Game1.activeClickableMenu is RemoteCraftingTerminalMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
        {
            remoteMenu.MarkActionComplete(response.Snapshot);
        }
    }

    private RemoteCraftingTerminalMenu CreateRemoteCraftingMenu(CraftingSnapshotResponseMessage snapshot)
    {
        return new RemoteCraftingTerminalMenu(
            snapshot,
            this.SendRemoteCraftingActionRequest,
            (batches, qualityStrategy) => this.SendRemoteCraftingSnapshotRequest(snapshot.NetworkId, snapshot.EndpointId, batches, qualityStrategy));
    }

    private void HandleCraftingMonitorSnapshotRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<CraftingMonitorSnapshotRequestMessage>();
        var response = this.CreateCraftingMonitorSnapshotResponse(request);

        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.CraftingMonitorSnapshotResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { e.FromPlayerID });
    }

    private void HandleCraftingMonitorSnapshotResponse(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
            return;

        var response = e.ReadAs<CraftingMonitorSnapshotResponseMessage>();
        if (!response.Success)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteMonitor.snapshotFailed", "SVSAP monitor snapshot failed; please retry."), HUDMessage.error_type));
            return;
        }

        if (Game1.activeClickableMenu is RemoteCraftingMonitorMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
            remoteMenu.ApplySnapshot(response);
        else
            Game1.activeClickableMenu = this.CreateRemoteCraftingMonitorMenu(response);

        Game1.playSound("bigSelect");
    }

    private bool SendRemoteCraftingMonitorActionRequest(CraftingMonitorActionRequestMessage request)
    {
        if (Context.IsMainPlayer)
            return false;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.hostNeedsSVSAPMonitor"), HUDMessage.error_type));
            return false;
        }

        try
        {
            this.multiplayerHelper.SendMessage(
                request,
                MultiplayerMessageTypes.CraftingMonitorActionRequest,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Failed to send SVSAP monitor request {request.TransactionId:N}: {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteMonitor.sendFailed"), HUDMessage.error_type));
            return false;
        }

        this.LogGameplay($"action=remote_monitor_request result=sent player={DescribePlayer(Game1.player)} host={host.PlayerID} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} requestAction={request.Action} job={(request.JobId.HasValue ? ShortId(request.JobId.Value) : "none")} batches={request.Batches:N0} tx={ShortId(request.TransactionId)}");
        Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteMonitor.requestSent"), HUDMessage.newQuest_type));
        return true;
    }

    private void HandleCraftingMonitorActionRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<CraftingMonitorActionRequestMessage>();
        if (this.TryGetPeerActionBlock(e.FromPlayerID, out var blockMessage))
        {
            var blocked = new CraftingMonitorActionResponseMessage
            {
                TransactionId = request.TransactionId,
                NetworkId = request.NetworkId,
                Message = blockMessage
            };
            this.RememberCraftingMonitorActionResponse(e.FromPlayerID, request.TransactionId, blocked);
            this.SendCraftingMonitorActionResponse(blocked, e.FromPlayerID);
            return;
        }

        if (this.TryGetCachedCraftingMonitorActionResponse(e.FromPlayerID, request.TransactionId, out var cached))
        {
            this.SendCraftingMonitorActionResponse(cached, e.FromPlayerID);
            return;
        }

        var response = this.ExecuteCraftingMonitorActionRequest(request, e.FromPlayerID);
        this.RememberCraftingMonitorActionResponse(e.FromPlayerID, request.TransactionId, response);

        this.SendCraftingMonitorActionResponse(response, e.FromPlayerID);
    }

    private void SendCraftingMonitorActionResponse(CraftingMonitorActionResponseMessage response, long playerId)
    {
        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.CraftingMonitorActionResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { playerId });
    }

    private bool TryGetCachedCraftingMonitorActionResponse(long playerId, Guid transactionId, out CraftingMonitorActionResponseMessage response)
    {
        response = null!;
        return transactionId != Guid.Empty
            && this.craftingMonitorActionResponseCache.TryGetValue((playerId, transactionId), out response!);
    }

    private void RememberCraftingMonitorActionResponse(long playerId, Guid transactionId, CraftingMonitorActionResponseMessage response)
    {
        if (transactionId == Guid.Empty)
            return;

        var key = (playerId, transactionId);
        if (this.craftingMonitorActionResponseCache.ContainsKey(key))
            return;

        this.craftingMonitorActionResponseCache[key] = response;
        this.craftingMonitorActionResponseOrder.Enqueue(key);
        while (this.craftingMonitorActionResponseOrder.Count > ActionResponseCacheLimit)
            this.craftingMonitorActionResponseCache.Remove(this.craftingMonitorActionResponseOrder.Dequeue());
    }

    private void HandleCraftingMonitorActionResponse(ModMessageReceivedEventArgs e)
    {
        if (Context.IsMainPlayer)
            return;

        var response = e.ReadAs<CraftingMonitorActionResponseMessage>();
        if (response.TransactionId != Guid.Empty && !this.MarkClientTransactionReconciled(response.TransactionId, this.reconciledCraftingMonitorTx, this.reconciledCraftingMonitorTxOrder))
            return;

        Game1.addHUDMessage(new HUDMessage(
            LocalizeRemoteResponse(response.Success, response.Message, "remoteMonitor.actionSucceeded", "SVSAP monitor action completed.", "remoteMonitor.actionFailed", "SVSAP monitor action failed; please retry."),
            response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        if (Game1.activeClickableMenu is RemoteCraftingMonitorMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
        {
            remoteMenu.MarkActionComplete(response.Snapshot);
            remoteMenu.ApplyActionResult(response);
        }
    }

    private RemoteCraftingMonitorMenu CreateRemoteCraftingMonitorMenu(CraftingMonitorSnapshotResponseMessage snapshot)
    {
        return new RemoteCraftingMonitorMenu(
            snapshot,
            this.SendRemoteCraftingMonitorActionRequest,
            this.SendRemoteCraftingMonitorSnapshotRequest);
    }

    private TerminalActionResponseMessage ExecuteTerminalActionRequest(TerminalActionRequestMessage request, long fromPlayerId)
    {
        var response = new TerminalActionResponseMessage
        {
            TransactionId = request.TransactionId,
            NetworkId = request.NetworkId,
            ReturnedDepositItems = CloneTerminalPayloads(request.DepositItems)
        };

        if (!this.repository.TryGetNetwork(request.NetworkId, out var network))
        {
            response.Success = false;
            response.Message = ModText.Get("network.error.terminalNotLinked");
            return response;
        }

        var player = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        if (player is null)
        {
            response.Success = false;
            response.Message = ModText.Get("multiplayer.requestPlayerOffline");
            response.Snapshot = this.CreateTerminalSnapshotResponse(new TerminalSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId });
            return response;
        }

        if (!this.IsRequestedEndpointActive(network, request.EndpointId, EndpointType.NetworkTerminal, out var endpointMessage))
        {
            response.Success = false;
            response.Message = endpointMessage;
            response.Snapshot = this.CreateTerminalSnapshotResponse(new TerminalSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId });
            return response;
        }

        var actionMessage = string.Empty;
        var success = false;
        switch (request.Action)
        {
            case TerminalActionKind.Withdraw when request.ItemKey is not null:
                success = this.TryExecuteRemoteTerminalWithdraw(network, request, response, out actionMessage);
                break;

            case TerminalActionKind.DepositSlot:
                success = this.TryExecuteRemoteTerminalDepositPayloads(network, request, response, out actionMessage);
                break;

            case TerminalActionKind.DepositSame:
                success = this.TryExecuteRemoteTerminalDepositPayloads(network, request, response, out actionMessage);
                break;

            case TerminalActionKind.DepositAll:
                success = this.TryExecuteRemoteTerminalDepositPayloads(network, request, response, out actionMessage);
                break;

            case TerminalActionKind.ToggleHeldItemLock:
                success = this.TryToggleHeldItemLock(network, player, request, out actionMessage);
                break;

            default:
                success = Fail(ModText.Get("remoteTerminal.unsupportedRequest"), out actionMessage);
                break;
        }

        response.Success = success;
        response.Message = actionMessage;
        if (success)
            this.RegisterTerminalRemoteDelivery(fromPlayerId, request, response);

        if (success)
            this.transactionService.SaveNetworkState();

        this.LogGameplay($"action=terminal_action result={(success ? "success" : "fail")} player={DescribePlayer(player)} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} requestAction={request.Action} amount={request.Amount:N0} tx={ShortId(request.TransactionId)} message={Quote(actionMessage)}");
        response.Snapshot = this.CreateTerminalSnapshotResponse(new TerminalSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId });
        return response;
    }

    private bool TryExecuteRemoteTerminalWithdraw(
        NetworkData network,
        TerminalActionRequestMessage request,
        TerminalActionResponseMessage response,
        out string message)
    {
        if (request.ItemKey is null)
            return Fail(ModText.Get("remoteTerminal.unsupportedRequest"), out message);

        if (!this.transactionService.TryExtractForTerminal(network, request.ItemKey, request.Amount, out var extracted, out message)
            || extracted is null)
        {
            return false;
        }

        response.ReturnedSerializedItem = SerializedItemCodec.SerializePrototype(extracted);
        response.ReturnedCount = extracted.Stack;
        return true;
    }

    private bool TryExecuteRemoteTerminalDepositPayloads(
        NetworkData network,
        TerminalActionRequestMessage request,
        TerminalActionResponseMessage response,
        out string message)
    {
        response.ReturnedDepositItems = new List<TerminalItemPayloadMessage>();
        if (request.DepositItems.Count == 0)
            return Fail(ModText.Get(request.Action == TerminalActionKind.DepositSame ? "inventory.noSameToDeposit" : "inventory.noItemsToDeposit"), out message);

        if (request.DepositItems.Count > 48)
        {
            response.ReturnedDepositItems = CloneTerminalPayloads(request.DepositItems);
            return Fail(ModText.Get("remoteTerminal.invalidPayload"), out message);
        }

        var movedTotal = 0;
        string? blockedReason = null;
        foreach (var payload in request.DepositItems)
        {
            if (string.IsNullOrWhiteSpace(payload.SerializedItem) || payload.Count <= 0 || payload.Count > 999)
            {
                response.ReturnedDepositItems.Add(payload);
                blockedReason ??= ModText.Get("remoteTerminal.invalidPayload");
                continue;
            }

            Item item;
            try
            {
                item = SerializedItemCodec.CreateItem(payload.SerializedItem, payload.Count);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Rejected malformed remote terminal deposit payload: {ex.Message}", LogLevel.Warn);
                response.ReturnedDepositItems.Add(payload);
                blockedReason ??= ModText.Get("remoteTerminal.invalidPayload");
                continue;
            }

            if (!this.transactionService.CanDepositPlayerItem(network, item, out var depositMessage))
            {
                response.ReturnedDepositItems.Add(CreateTerminalPayload(item));
                blockedReason ??= depositMessage;
                continue;
            }

            var before = item.Stack;
            if (!this.transactionService.TryDepositItem(network, item, out var moved) || moved <= 0)
            {
                response.ReturnedDepositItems.Add(CreateTerminalPayload(item));
                blockedReason ??= ModText.Get("terminal.depositBlocked.capacity");
                continue;
            }

            movedTotal += moved;
            if (item.Stack > 0)
                response.ReturnedDepositItems.Add(CreateTerminalPayload(item));

            if (moved < before)
                blockedReason ??= ModText.Get("terminal.depositBlocked.capacity");
        }

        if (movedTotal <= 0)
        {
            if (response.ReturnedDepositItems.Count == 0)
                response.ReturnedDepositItems = CloneTerminalPayloads(request.DepositItems);

            return Fail(blockedReason ?? ModText.Get("terminal.depositBlocked.capacity"), out message);
        }

        message = request.Action switch
        {
            TerminalActionKind.DepositSame => ModText.Format("inventory.depositSameSuccess", movedTotal),
            TerminalActionKind.DepositAll => ModText.Format("inventory.depositAllSuccess", movedTotal),
            _ => ModText.Format("inventory.depositSlotSuccess", movedTotal)
        };
        return true;
    }

    private bool TryToggleHeldItemLock(NetworkData network, Farmer player, TerminalActionRequestMessage request, out string message)
    {
        var id = request.HeldQualifiedItemId;
        if (string.IsNullOrWhiteSpace(id))
        {
            message = ModText.Get("terminal.lockHoldItem");
            return false;
        }

        var displayName = string.IsNullOrWhiteSpace(request.HeldDisplayName)
            ? id
            : request.HeldDisplayName;
        var removed = network.LockedQualifiedItemIds.RemoveAll(candidate => string.Equals(candidate, id, StringComparison.Ordinal));
        if (removed > 0)
        {
            this.repository.Save();
            message = ModText.Format("terminal.unlocked", displayName);
            this.LogGameplay($"action=toggle_item_lock result=success player={DescribePlayer(player)} network={ShortId(network.NetworkId)} mode=unlock item={Quote(displayName)} itemId={Quote(id)}");
            return true;
        }

        network.LockedQualifiedItemIds.Add(id);
        network.LockedQualifiedItemIds.Sort(StringComparer.Ordinal);
        this.repository.Save();
        message = ModText.Format("terminal.locked", displayName);
        this.LogGameplay($"action=toggle_item_lock result=success player={DescribePlayer(player)} network={ShortId(network.NetworkId)} mode=lock item={Quote(displayName)} itemId={Quote(id)}");
        return true;
    }

    private CraftingActionResponseMessage ExecuteCraftingActionRequest(CraftingActionRequestMessage request, long fromPlayerId)
    {
        var response = new CraftingActionResponseMessage
        {
            TransactionId = request.TransactionId,
            NetworkId = request.NetworkId
        };

        if (!this.repository.TryGetNetwork(request.NetworkId, out var network))
        {
            response.Success = false;
            response.Message = ModText.Get("network.error.terminalNotLinked");
            return response;
        }

        var player = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        if (player is null)
        {
            response.Success = false;
            response.Message = ModText.Get("multiplayer.requestPlayerOffline");
            response.Snapshot = this.CreateCraftingSnapshotResponse(
                new CraftingSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId, Batches = request.Batches, QualityStrategy = request.QualityStrategy },
                fromPlayerId);
            return response;
        }

        if (!this.IsRequestedEndpointActive(network, request.EndpointId, EndpointType.NetworkTerminal, out var endpointMessage))
        {
            response.Success = false;
            response.Message = endpointMessage;
            response.Snapshot = this.CreateCraftingSnapshotResponse(
                new CraftingSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId, Batches = request.Batches, QualityStrategy = request.QualityStrategy },
                fromPlayerId);
            return response;
        }

        var recipe = this.craftingRecipeService
            .GetKnownRecipesForPlayer(player)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, request.RecipeName, StringComparison.Ordinal));

        if (recipe is null)
        {
            response.Success = false;
            response.Message = ModText.Get("remoteCrafting.recipeUnknown");
            response.Snapshot = this.CreateCraftingSnapshotResponse(
                new CraftingSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId, Batches = request.Batches, QualityStrategy = request.QualityStrategy },
                fromPlayerId);
            return response;
        }

        response.Success = this.craftingRecipeService.TryCraftForPlayer(
            network,
            player,
            recipe,
            Math.Max(1, request.Batches),
            request.QualityStrategy,
            out var actionMessage);
        response.Message = actionMessage;
        response.Snapshot = this.CreateCraftingSnapshotResponse(
            new CraftingSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId, Batches = request.Batches, QualityStrategy = request.QualityStrategy },
            fromPlayerId);
        this.LogGameplay($"action=crafting_terminal_action result={(response.Success ? "success" : "fail")} player={DescribePlayer(player)} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} recipe={Quote(request.RecipeName)} batches={request.Batches:N0} quality={request.QualityStrategy} tx={ShortId(request.TransactionId)} message={Quote(actionMessage)}");
        return response;
    }

    private CraftingMonitorActionResponseMessage ExecuteCraftingMonitorActionRequest(CraftingMonitorActionRequestMessage request, long fromPlayerId)
    {
        var response = new CraftingMonitorActionResponseMessage
        {
            TransactionId = request.TransactionId,
            NetworkId = request.NetworkId
        };

        if (!this.repository.TryGetNetwork(request.NetworkId, out var network))
        {
            response.Success = false;
            response.Message = ModText.Get("network.error.monitorNotLinked");
            return response;
        }

        if (!this.IsRequestedEndpointActive(network, request.EndpointId, EndpointType.CraftingMonitor, out var endpointMessage))
        {
            response.Success = false;
            response.Message = endpointMessage;
            response.Snapshot = this.CreateCraftingMonitorSnapshotResponse(new CraftingMonitorSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId });
            return response;
        }

        var actionMessage = string.Empty;
        var success = false;
        switch (request.Action)
        {
            case CraftingMonitorActionKind.CancelJob when request.JobId.HasValue:
                success = this.patternExecutionService.TryCancelJob(network, request.JobId.Value, out actionMessage);
                break;

            case CraftingMonitorActionKind.UpdatePipeline when request.PipelineId.HasValue && !string.IsNullOrWhiteSpace(request.PipelineAction):
                success = this.patternExecutionService.TryUpdatePipeline(network, request.PipelineId.Value, request.PipelineAction, out actionMessage);
                break;

            case CraftingMonitorActionKind.PreviewQueueJob when request.QueuePattern is not null:
                success = this.patternExecutionService.TryPreviewQueuePatternJob(
                    network,
                    request.QueuePattern,
                    Math.Max(1, request.Batches),
                    out var previewLines,
                    out var requiresConfirmation,
                    out actionMessage);
                response.RequiresConfirmation = success && requiresConfirmation;
                response.PreviewPattern = request.QueuePattern;
                response.PreviewBatches = Math.Max(1, request.Batches);
                response.PreviewLines = previewLines;
                break;

            case CraftingMonitorActionKind.QueueJob when request.QueuePattern is not null:
                var batches = Math.Max(1, request.Batches);
                if (this.patternExecutionService.NeedsLongJobConfirmation(network, request.QueuePattern, batches)
                    && !request.ConfirmLongJob)
                {
                    response.Success = false;
                    response.RequiresConfirmation = true;
                    response.Message = ModText.Get("craftingMonitor.longJob.confirmHud");
                    response.Snapshot = this.CreateCraftingMonitorSnapshotResponse(new CraftingMonitorSnapshotRequestMessage
                    {
                        NetworkId = request.NetworkId,
                        EndpointId = request.EndpointId,
                        HeldPattern = request.QueuePattern,
                        HeldCaskItemPrototype = request.CaskPipelineItemPrototype
                    });
                    return response;
                }

                success = this.patternExecutionService.TryQueuePatternJob(network, request.QueuePattern, batches, out actionMessage);
                break;

            case CraftingMonitorActionKind.TogglePipeline when request.QueuePattern is not null:
                success = this.patternExecutionService.TryTogglePipeline(network, request.QueuePattern, out actionMessage);
                break;

            case CraftingMonitorActionKind.ToggleCaskPipeline when !string.IsNullOrWhiteSpace(request.CaskPipelineItemPrototype):
                success = this.TryToggleRemoteCaskPipeline(network, request.CaskPipelineItemPrototype, out actionMessage);
                break;

            default:
                success = Fail(ModText.Get("remoteMonitor.unsupportedRequest"), out actionMessage);
                break;
        }

        response.Success = success;
        response.Message = actionMessage;
        response.Snapshot = this.CreateCraftingMonitorSnapshotResponse(new CraftingMonitorSnapshotRequestMessage
        {
            NetworkId = request.NetworkId,
            EndpointId = request.EndpointId,
            HeldPattern = request.QueuePattern,
            HeldCaskItemPrototype = request.CaskPipelineItemPrototype
        });
        var player = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        this.LogGameplay($"action=crafting_monitor_action result={(success ? "success" : "fail")} player={DescribePlayer(player, fromPlayerId)} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} requestAction={request.Action} job={(request.JobId.HasValue ? ShortId(request.JobId.Value) : "none")} batches={request.Batches:N0} tx={ShortId(request.TransactionId)} message={Quote(actionMessage)}");
        return response;
    }

    private TerminalSnapshotResponseMessage CreateTerminalSnapshotResponse(TerminalSnapshotRequestMessage request)
    {
        if (!this.repository.TryGetNetwork(request.NetworkId, out var network))
        {
            return new TerminalSnapshotResponseMessage
            {
                NetworkId = request.NetworkId,
                EndpointId = request.EndpointId,
                Success = false,
                Message = ModText.Get("network.error.terminalNotLinked")
            };
        }

        if (!this.IsRequestedEndpointActive(network, request.EndpointId, EndpointType.NetworkTerminal, out var endpointMessage))
        {
            return new TerminalSnapshotResponseMessage
            {
                NetworkId = request.NetworkId,
                EndpointId = request.EndpointId,
                Success = false,
                Message = endpointMessage
            };
        }

        var snapshot = this.inventoryScanner.Scan(network);
        this.transactionService.ApplyReservationOverlay(network, snapshot);
        var totalEntries = snapshot.Entries.Count;
        var entryLimit = NormalizeTerminalSnapshotEntryLimit(request.EntryLimit);
        var entryOffset = Math.Clamp(request.EntryOffset, 0, totalEntries);
        var entries = snapshot.Entries
            .Skip(entryOffset)
            .Take(entryLimit)
            .ToList();

        return new TerminalSnapshotResponseMessage
        {
            NetworkId = request.NetworkId,
            EndpointId = request.EndpointId,
            Success = true,
            NetworkName = network.Name,
            SourceCount = snapshot.SourceCount,
            TotalEntryCount = totalEntries,
            EntryOffset = entryOffset,
            EntryLimit = entryLimit,
            Truncated = entryOffset + entries.Count < totalEntries,
            StorageSummary = snapshot.StorageSummary,
            LockedQualifiedItemIds = network.LockedQualifiedItemIds.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            Entries = entries
                .Select(entry => new RemoteInventoryEntryMessage
                {
                    QualifiedItemId = entry.Key.QualifiedItemId,
                    SerializedItemPrototype = SerializedItemCodec.SerializePrototype(entry.Prototype.getOne()),
                    Key = entry.Key,
                    Name = entry.Prototype.Name,
                    DisplayName = entry.Prototype.DisplayName,
                    Category = TerminalInventoryFilters.GetCategory(entry.Prototype),
                    Quality = entry.Key.Quality,
                    SalePrice = TerminalInventoryFilters.GetSalePrice(entry.Prototype),
                    TotalCount = entry.TotalCount,
                    ReservedCount = entry.ReservedCount,
                    AvailableCount = entry.AvailableCount,
                    LastAddedSequence = entry.LastAddedSequence,
                    StackCount = entry.Locations.Count,
                    Locations = entry.Locations
                        .Take(RemoteTerminalSnapshotLocationLimit)
                        .Select(location => new RemoteItemStackLocationMessage
                        {
                            SourceKind = location.SourceKind,
                            LocationName = location.LocationName,
                            TileX = location.TileX,
                            TileY = location.TileY,
                            SlotIndex = location.SlotIndex,
                            Count = location.Count
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private CraftingSnapshotResponseMessage CreateCraftingSnapshotResponse(CraftingSnapshotRequestMessage request, long fromPlayerId)
    {
        var response = new CraftingSnapshotResponseMessage
        {
            NetworkId = request.NetworkId,
            EndpointId = request.EndpointId,
            Batches = Math.Max(1, request.Batches),
            QualityStrategy = request.QualityStrategy
        };

        if (!this.repository.TryGetNetwork(request.NetworkId, out var network))
        {
            response.Success = false;
            response.Message = ModText.Get("network.error.terminalNotLinked");
            return response;
        }

        if (!this.IsRequestedEndpointActive(network, request.EndpointId, EndpointType.NetworkTerminal, out var endpointMessage))
        {
            response.Success = false;
            response.Message = endpointMessage;
            return response;
        }

        var player = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        if (player is null)
        {
            response.Success = false;
            response.Message = ModText.Get("multiplayer.requestPlayerOffline");
            return response;
        }

        var snapshot = this.inventoryScanner.Scan(network);
        response.Success = true;
        response.NetworkName = network.Name;
        response.NetworkItemTypes = snapshot.Entries.Count;
        response.Recipes = this.craftingRecipeService.GetKnownRecipesForPlayer(player)
            .Select(recipe =>
            {
                var availability = this.craftingRecipeService.GetAvailability(network, recipe, response.Batches, response.QualityStrategy);
                return new RemoteCraftingRecipeMessage
                {
                    Name = recipe.Name,
                    DisplayName = recipe.DisplayName,
                    OutputQualifiedItemId = recipe.OutputPrototype.QualifiedItemId,
                    OutputSerializedItemPrototype = SerializedItemCodec.SerializePrototype(recipe.OutputPrototype.getOne()),
                    OutputCount = recipe.OutputCount,
                    CanCraft = availability.CanCraft,
                    Ingredients = availability.Ingredients,
                    IngredientLines = availability.IngredientLines,
                    MissingLines = availability.MissingLines,
                    MissingIngredients = availability.MissingIngredients
                };
            })
            .ToList();
        return response;
    }

    private CraftingMonitorSnapshotResponseMessage CreateCraftingMonitorSnapshotResponse(CraftingMonitorSnapshotRequestMessage request)
    {
        if (!this.repository.TryGetNetwork(request.NetworkId, out var network))
        {
            return new CraftingMonitorSnapshotResponseMessage
            {
                NetworkId = request.NetworkId,
                EndpointId = request.EndpointId,
                Success = false,
                Message = ModText.Get("network.error.monitorNotLinked")
            };
        }

        if (!this.IsRequestedEndpointActive(network, request.EndpointId, EndpointType.CraftingMonitor, out var endpointMessage))
        {
            return new CraftingMonitorSnapshotResponseMessage
            {
                NetworkId = request.NetworkId,
                EndpointId = request.EndpointId,
                Success = false,
                Message = endpointMessage
            };
        }

        var caskItem = this.TryCreateCaskPipelineItem(request.HeldCaskItemPrototype);
        return new CraftingMonitorSnapshotResponseMessage
        {
            NetworkId = request.NetworkId,
            EndpointId = request.EndpointId,
            Success = true,
            NetworkName = network.Name,
            QueuePattern = request.HeldPattern is not null && !string.IsNullOrWhiteSpace(request.HeldPattern.DisplayName)
                ? request.HeldPattern
                : null,
            CaskPipelineItemPrototype = caskItem is not null
                ? SerializedItemCodec.SerializePrototype(caskItem.getOne())
                : string.Empty,
            CaskPipelineItemDisplayName = caskItem?.DisplayName ?? string.Empty,
            Jobs = this.patternExecutionService.GetVisibleJobs(network)
                .Select(job =>
                {
                    var totalReservations = job.Reservations.Sum(reservation => Math.Max(0, reservation.Count));
                    var remainingReservations = job.Reservations.Sum(reservation => Math.Max(0, reservation.Count - reservation.ConsumedCount));
                    return new RemoteCraftingJobMessage
                    {
                        JobId = job.JobId,
                        Pattern = job.Pattern,
                        DisplayName = PatternDisplayNames.Get(job.Pattern),
                        State = job.State,
                        RequestedCount = job.RequestedCount,
                        CompletedCount = job.CompletedCount,
                        NodeCount = job.NodeCount,
                        CpuSlotLabel = job.AssignedCpuEndpointId.HasValue
                            ? job.AssignedCpuEndpointId.Value.ToString("N").Substring(0, 8)
                            : string.Empty,
                        ReservedCount = totalReservations,
                        RemainingReservedCount = remainingReservations,
                        StatusMessage = job.StatusMessage,
                        CanCancel = IsCancellableJobState(job.State)
                    };
                })
                .ToList(),
            Pipelines = this.patternExecutionService.GetVisiblePipelines(network)
                .Select(pipeline => new RemoteProductionPipelineMessage
                {
                    PipelineId = pipeline.PipelineId,
                    Enabled = pipeline.Enabled,
                    Priority = pipeline.Priority,
                    Mode = pipeline.Mode,
                    Pattern = pipeline.Pattern,
                    DisplayName = PatternDisplayNames.Get(pipeline.Pattern),
                    TargetKeep = pipeline.TargetKeep,
                    ItemsPerCycle = pipeline.ItemsPerCycle,
                    StatusMessage = pipeline.StatusMessage
                })
                .ToList()
        };
    }

    private bool TryToggleRemoteCaskPipeline(NetworkData network, string serializedItem, out string message)
    {
        var item = this.TryCreateCaskPipelineItem(serializedItem);
        if (item is null)
        {
            message = ModText.Get("caskPipeline.needAgeable");
            return false;
        }

        return this.patternExecutionService.TryToggleCaskPipeline(network, item, out message);
    }

    private static bool Fail(string message, out string output)
    {
        output = message;
        return false;
    }

    private static bool IsCancellableJobState(CraftingJobState state)
    {
        return state is CraftingJobState.Planning
            or CraftingJobState.MissingItems
            or CraftingJobState.Reserved
            or CraftingJobState.Running
            or CraftingJobState.WaitingForMachine
            or CraftingJobState.WaitingForOutput;
    }

    private bool IsHoldingLinkTool()
    {
        return Game1.player?.CurrentItem?.QualifiedItemId == "(O)" + ModItemCatalog.LinkTool;
    }

    private static PatternData? GetHeldPattern()
    {
        var held = Game1.player.CurrentItem;
        if (!PatternCodec.IsPatternItem(held))
            return null;

        return PatternCodec.TryRead(held!, out var pattern)
            ? pattern
            : null;
    }

    private string GetHeldCaskPipelineItemPrototype()
    {
        var held = Game1.player.CurrentItem;
        if (held is null || !this.patternExecutionService.CanToggleCaskPipeline(held))
            return string.Empty;

        return SerializedItemCodec.SerializePrototype(held.getOne());
    }

    private Item? TryCreateCaskPipelineItem(string serializedItem)
    {
        if (string.IsNullOrWhiteSpace(serializedItem))
            return null;

        try
        {
            var item = SerializedItemCodec.CreateItem(serializedItem, 1);
            return this.patternExecutionService.CanToggleCaskPipeline(item)
                ? item
                : null;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not read remote cask pipeline item: {ex.Message}", LogLevel.Trace);
            return null;
        }
    }

    private bool IsHostOnlySVSAPInteraction(SObject target)
    {
        return false;
    }

    private bool IsRemoteTerminalInteraction(SObject target, out bool crafting)
    {
        crafting = target.QualifiedItemId == "(BC)" + ModItemCatalog.CraftingTerminal;
        return crafting || target.QualifiedItemId == "(BC)" + ModItemCatalog.NetworkTerminal;
    }

    private bool IsEndpointConnected(NetworkData network, NetworkEndpoint endpoint, out string message)
    {
        if (!this.IsEndpointPhysicallyPresent(network, endpoint))
        {
            message = ModText.Get("network.error.endpointMissing");
            return false;
        }

        if (endpoint.Type == EndpointType.NetworkCore)
        {
            message = string.Empty;
            return true;
        }

        if (!this.TryGetCoreEndpoint(network, out var core))
        {
            message = ModText.Get("network.error.noCore");
            return false;
        }

        if (!this.IsEndpointPhysicallyPresent(network, core))
        {
            message = ModText.Get("network.error.coreMissing");
            return false;
        }

        if (this.getConfig().RequireCables)
            return this.HasCablePathToCore(core, endpoint, out message);

        if (!this.getConfig().EnableSimpleWirelessWithinFarm)
        {
            message = ModText.Get("network.error.simpleWirelessDisabled");
            return false;
        }

        if (!string.Equals(core.LocationName, endpoint.LocationName, StringComparison.Ordinal))
        {
            message = ModText.Get("network.error.simpleWirelessSameLocation");
            return false;
        }

        message = string.Empty;
        return true;
    }

    private bool TryGetActiveLinkedNetwork(SObject endpointObject, string label, out NetworkData network, out Guid endpointId, out string message)
    {
        network = null!;
        endpointId = Guid.Empty;
        if (!this.TryReadLinkedEndpointIds(endpointObject, label, out var networkId, out endpointId, out message))
            return false;

        if (!this.repository.TryGetNetwork(networkId, out network!))
        {
            message = ModText.Format("network.error.labelNotLinked", label);
            return false;
        }

        return this.IsRequestedEndpointActive(network, endpointId, null, out message);
    }

    private Func<string?> CreateLocalEndpointGuard(NetworkData network, Guid endpointId, EndpointType expectedType)
    {
        return () => this.IsRequestedEndpointActive(network, endpointId, expectedType, out var message)
            ? null
            : message;
    }

    private bool TryReadLinkedEndpointIds(SObject endpointObject, string label, out Guid networkId, out Guid endpointId, out string message)
    {
        networkId = Guid.Empty;
        endpointId = Guid.Empty;

        if (!Guid.TryParse(endpointObject.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out networkId))
        {
            message = ModText.Format("network.error.labelNotLinked", label);
            return false;
        }

        if (!Guid.TryParse(endpointObject.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out endpointId))
        {
            message = ModText.Format("network.error.labelMissingEndpointId", label);
            return false;
        }

        message = string.Empty;
        return true;
    }

    private bool IsRequestedEndpointActive(NetworkData network, Guid endpointId, EndpointType? expectedType, out string message)
    {
        if (endpointId == Guid.Empty)
        {
            message = string.Empty;
            return true;
        }

        var endpoint = network.Endpoints.FirstOrDefault(candidate => candidate.EndpointId == endpointId);
        if (endpoint is null)
        {
            message = ModText.Get("network.error.endpointNotRegistered");
            return false;
        }

        if (expectedType.HasValue && endpoint.Type != expectedType.Value)
        {
            message = ModText.Get("network.error.endpointTypeMismatch");
            return false;
        }

        var connected = this.IsEndpointConnected(network, endpoint, out message);
        if (endpoint.Active != connected)
        {
            endpoint.Active = connected;
            this.repository.Save();
        }

        return connected;
    }

    private bool IsEndpointPhysicallyPresent(NetworkData network, NetworkEndpoint endpoint)
    {
        var location = Game1.getLocationFromName(endpoint.LocationName);
        if (location is null)
            return false;

        var tile = new Vector2(endpoint.TileX, endpoint.TileY);
        if (!location.objects.TryGetValue(tile, out SObject? placedObject))
            return false;

        return Guid.TryParse(placedObject.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var placedNetworkId)
            && placedNetworkId == network.NetworkId
            && this.GetEndpointType(placedObject) == endpoint.Type
            && Guid.TryParse(placedObject.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out var placedEndpointId)
            && placedEndpointId == endpoint.EndpointId;
    }

    private bool TryGetCoreEndpoint(NetworkData network, out NetworkEndpoint core)
    {
        core = network.Endpoints.FirstOrDefault(endpoint => endpoint.Type == EndpointType.NetworkCore)!;
        return core is not null;
    }

    private bool HasCablePathToCore(NetworkEndpoint core, NetworkEndpoint endpoint, out string message)
    {
        if (!string.Equals(core.LocationName, endpoint.LocationName, StringComparison.Ordinal))
        {
            message = ModText.Get("network.error.cableSameLocation");
            return false;
        }

        var location = Game1.getLocationFromName(endpoint.LocationName);
        if (location is null)
        {
            message = ModText.Get("network.error.endpointLocationMissing");
            return false;
        }

        var coreTile = new Vector2(core.TileX, core.TileY);
        var endpointTile = new Vector2(endpoint.TileX, endpoint.TileY);
        if (IsAdjacentOrSame(endpointTile, coreTile))
        {
            message = string.Empty;
            return true;
        }

        var visited = new HashSet<Vector2>();
        var queue = new Queue<Vector2>();
        foreach (var neighbor in GetAdjacentTiles(endpointTile))
        {
            if (neighbor == coreTile)
            {
                message = string.Empty;
                return true;
            }

            if (this.IsNetworkCableAt(location, neighbor) && visited.Add(neighbor))
                queue.Enqueue(neighbor);
        }

        while (queue.Count > 0)
        {
            var tile = queue.Dequeue();
            foreach (var neighbor in GetAdjacentTiles(tile))
            {
                if (neighbor == coreTile)
                {
                    message = string.Empty;
                    return true;
                }

                if (visited.Contains(neighbor) || !this.IsNetworkCableAt(location, neighbor))
                    continue;

                visited.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        message = ModText.Get("network.error.cablePathMissing");
        return false;
    }

    private bool IsNetworkCableAt(GameLocation location, Vector2 tile)
    {
        if (location.objects.TryGetValue(tile, out SObject? obj)
            && obj.QualifiedItemId == "(O)" + ModItemCatalog.NetworkCable)
        {
            return true;
        }

        if (location.terrainFeatures.TryGetValue(tile, out var terrainFeature)
            && terrainFeature is StardewValley.TerrainFeatures.Flooring flooring)
        {
            var data = flooring.GetData();
            return data?.ItemId == ModItemCatalog.NetworkCable
                || data?.Id == ModItemCatalog.NetworkCable;
        }

        return false;
    }

    private static bool IsAdjacentOrSame(Vector2 a, Vector2 b)
    {
        if (a == b)
            return true;

        return GetAdjacentTiles(a).Any(tile => tile == b);
    }

    private static IEnumerable<Vector2> GetAdjacentTiles(Vector2 tile)
    {
        foreach (var offset in AdjacentOffsets)
            yield return tile + offset;
    }

    private EndpointType? GetEndpointType(SObject target)
    {
        if (target is Chest chest)
            return IsSupportedNetworkChest(chest) ? EndpointType.Chest : null;

        return target.QualifiedItemId switch
        {
            "(BC)" + ModItemCatalog.NetworkCore => EndpointType.NetworkCore,
            "(BC)" + ModItemCatalog.NetworkTerminal => EndpointType.NetworkTerminal,
            "(BC)" + ModItemCatalog.CraftingTerminal => EndpointType.NetworkTerminal,
            "(BC)" + ModItemCatalog.StorageInterface => EndpointType.StorageInterface,
            "(BC)" + ModItemCatalog.StorageDrive => EndpointType.StorageDrive,
            "(BC)" + ModItemCatalog.Importer => EndpointType.Importer,
            "(BC)" + ModItemCatalog.Exporter => EndpointType.Exporter,
            "(BC)" + ModItemCatalog.MachineInterface => EndpointType.MachineInterface,
            "(BC)" + ModItemCatalog.PatternTerminal => EndpointType.PatternTerminal,
            "(BC)" + ModItemCatalog.PatternProvider => EndpointType.PatternProvider,
            "(BC)" + ModItemCatalog.MolecularAssembler => EndpointType.MolecularAssembler,
            "(BC)" + ModItemCatalog.CraftingCpuCore => EndpointType.CraftingCpuCore,
            "(BC)" + ModItemCatalog.CraftingMatrix1K => EndpointType.CraftingMatrix1K,
            "(BC)" + ModItemCatalog.CraftingMatrix4K => EndpointType.CraftingMatrix4K,
            "(BC)" + ModItemCatalog.CraftingMatrix16K => EndpointType.CraftingMatrix16K,
            "(BC)" + ModItemCatalog.CraftingMatrix64K => EndpointType.CraftingMatrix64K,
            "(BC)" + ModItemCatalog.CoProcessor => EndpointType.CoProcessor,
            "(BC)" + ModItemCatalog.CraftingMonitor => EndpointType.CraftingMonitor,
            _ => target.bigCraftable.Value ? EndpointType.Machine : null
        };
    }

    private static bool IsSupportedNetworkChest(Chest chest)
    {
        return chest.SpecialChestType == Chest.SpecialChestTypes.None;
    }

    private NetworkEndpoint CreateEndpoint(Guid endpointId, GameLocation location, Vector2 tile, EndpointType type)
    {
        return new NetworkEndpoint
        {
            EndpointId = endpointId,
            LocationName = location.NameOrUniqueName,
            TileX = tile.X,
            TileY = tile.Y,
            Type = type,
            Priority = 0,
            Active = true
        };
    }

    private void Suppress(ButtonPressedEventArgs e)
    {
        try
        {
            this.inputHelper.Suppress(e.Button);
        }
        catch (InvalidOperationException ex)
        {
            this.monitor.Log($"Could not suppress input after SVSAP interaction: {ex.Message}", LogLevel.Trace);
        }
    }

    private void LogGameplay(string message)
    {
        if (this.getConfig().DetailedGameplayLogs)
            this.monitor.Log("SVSAP_GAMELOG " + message, LogLevel.Info);
    }

    private static string DescribePlayer(Farmer? player, long fallbackId = 0)
    {
        if (player is null)
            return fallbackId == 0 ? "\"unknown\"#0" : $"\"unknown\"#{fallbackId}";

        return $"{Quote(player.Name)}#{player.UniqueMultiplayerID}";
    }

    private static string ShortId(Guid id)
    {
        var raw = id.ToString("N");
        return raw.Length <= 8 ? raw : raw[..8];
    }

    private static string FormatTile(Vector2 tile)
    {
        return $"({tile.X:0},{tile.Y:0})";
    }

    private static string Quote(string? value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private sealed class DurableRemoteDeliveryRecord
    {
        public Guid DeliveryId { get; set; }
        public Guid TransactionId { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ReturnedSerializedItem { get; set; } = string.Empty;
        public int ReturnedCount { get; set; }
        public int CreatedDay { get; set; }
    }

    private sealed class DurableClientActionEscrowRecord
    {
        public const string TerminalKind = "terminal";
        public const string StructuralKind = "structural";

        public Guid TransactionId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public TerminalActionRequestMessage? TerminalRequest { get; set; }
        public StructuralActionRequestMessage? StructuralRequest { get; set; }
    }

    private sealed class ClientEscrowRetryState
    {
        public int LastSentTick { get; set; }
        public int RetryCount { get; set; }
        public bool RetryLimitNotified { get; set; }

        public static ClientEscrowRetryState CreateReadyForRetry()
        {
            return new ClientEscrowRetryState
            {
                LastSentTick = Game1.ticks - ClientEscrowResponseTimeoutTicks
            };
        }
    }

    private sealed record PendingStructuralHeldItem(Item ActingItem, Item? EscrowedItem);
}
