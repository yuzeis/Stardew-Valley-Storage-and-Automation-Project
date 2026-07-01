using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Models;
using SVSAP.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class NetworkInteractionService
{
    internal const string SelectedNetworkIdKey = ModItemCatalog.UniqueId + "/SelectedNetworkId";
    private const int ActionResponseCacheLimit = 256;

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
    private readonly HashSet<Guid> reconciledStructuralTx = new();
    private readonly Dictionary<Guid, PendingStructuralHeldItem> pendingStructuralHeldItems = new();

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

        if (this.IsHoldingLinkTool())
        {
            this.HandleLinkToolUse(target, location, tile);
            this.Suppress(e);
            return;
        }

        if (target.QualifiedItemId == "(BC)" + ModItemCatalog.StorageDrive)
        {
            if (Context.IsMainPlayer)
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
                Game1.addHUDMessage(new HUDMessage(this.transferBusService.DescribeConfiguration(target), HUDMessage.newQuest_type));
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
            if (Context.IsMainPlayer)
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
        this.terminalActionResponseCache.Clear();
        this.terminalActionResponseOrder.Clear();
        this.craftingActionResponseCache.Clear();
        this.craftingActionResponseOrder.Clear();
        this.craftingMonitorActionResponseCache.Clear();
        this.craftingMonitorActionResponseOrder.Clear();
        this.structuralResponseCache.Clear();
        this.structuralResponseOrder.Clear();
        this.reconciledStructuralTx.Clear();
        this.RestorePendingStructuralHeldItems();
    }

    public void ClearActionResponseCaches(long playerId)
    {
        ClearCachedResponsesForPlayer(this.terminalActionResponseCache, this.terminalActionResponseOrder, playerId);
        ClearCachedResponsesForPlayer(this.craftingActionResponseCache, this.craftingActionResponseOrder, playerId);
        ClearCachedResponsesForPlayer(this.craftingMonitorActionResponseCache, this.craftingMonitorActionResponseOrder, playerId);
        ClearCachedResponsesForPlayer(this.structuralResponseCache, this.structuralResponseOrder, playerId);
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
            else if (e.Type == MultiplayerMessageTypes.StructuralActionRequest)
                this.HandleStructuralActionRequest(e);
            else if (e.Type == MultiplayerMessageTypes.StructuralActionResponse)
                this.HandleStructuralActionResponse(e);
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
            Game1.addHUDMessage(new HUDMessage("请先选择一个网络核心。", HUDMessage.error_type));
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
            Game1.addHUDMessage(new HUDMessage("请先选择一个网络核心。", HUDMessage.error_type));
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
            Game1.addHUDMessage(new HUDMessage("主机必须安装 Stardew Valley Storage and Automation Project。", HUDMessage.error_type));
            return;
        }

        PendingStructuralHeldItem? pending = null;
        if (!this.TryCaptureStructuralHeldItem(request, out pending, out var captureMessage))
        {
            Game1.addHUDMessage(new HUDMessage(captureMessage, HUDMessage.error_type));
            return;
        }

        if (pending is not null)
            this.pendingStructuralHeldItems[request.TransactionId] = pending;

        try
        {
            this.multiplayerHelper.SendMessage(
                request,
                MultiplayerMessageTypes.StructuralActionRequest,
                modIDs: new[] { this.modManifest.UniqueID },
                playerIDs: new[] { host.PlayerID });
        }
        catch
        {
            if (pending is not null)
            {
                this.pendingStructuralHeldItems.Remove(request.TransactionId);
                this.RestoreEscrowedStructuralItem(pending, Game1.currentLocation);
            }

            throw;
        }

        this.LogGameplay($"action=structural_request result=sent player={DescribePlayer(Game1.player)} host={host.PlayerID} kind={request.Kind} location={Quote(request.LocationName)} tile=({request.TileX:0},{request.TileY:0}) tx={ShortId(request.TransactionId)}");
    }

    private bool TryCaptureStructuralHeldItem(
        StructuralActionRequestMessage request,
        out PendingStructuralHeldItem? pending,
        out string message)
    {
        pending = null;
        message = string.Empty;
        if (Context.IsMainPlayer || request.TransactionId == Guid.Empty)
            return true;

        var held = Game1.player.CurrentItem;
        if (held is null)
            return true;

        Item? escrowed = null;
        if (ShouldEscrowStructuralHeldItem(request.Kind, held))
        {
            if (Game1.player.Items.IndexOf(held) < 0 || held.Stack <= 0)
            {
                message = "手持物品已变化，请重试。";
                return false;
            }

            escrowed = held.getOne();
            escrowed.Stack = 1;
            held.Stack -= 1;
            if (held.Stack <= 0)
                Game1.player.removeItemFromInventory(held);
        }

        pending = new PendingStructuralHeldItem(held, escrowed);
        return true;
    }

    private static bool ShouldEscrowStructuralHeldItem(StructuralActionKind kind, Item held)
    {
        return kind switch
        {
            StructuralActionKind.StorageDriveInteract
                => ModItemCatalog.TryGetStorageCellTier(held.QualifiedItemId, out _),
            StructuralActionKind.PatternProviderInteract
                => PatternCodec.IsPatternItem(held),
            StructuralActionKind.TransferBusConfigure
                => held.QualifiedItemId is "(O)" + ModItemCatalog.FilterCard
                    or "(O)" + ModItemCatalog.SpeedCard
                    or "(O)" + ModItemCatalog.CapacityCard
                    or "(O)" + ModItemCatalog.QualityCard,
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

    private void HandleStructuralActionRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        StructuralActionRequestMessage? request = null;
        try
        {
            request = e.ReadAs<StructuralActionRequestMessage>();
            if (this.TryGetCachedStructuralActionResponse(e.FromPlayerID, request.TransactionId, out var cached))
            {
                this.SendStructuralResponse(cached, e.FromPlayerID);
                return;
            }

            var response = this.ExecuteStructuralAction(request);
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

    private static StructuralActionResponseMessage CreateStructuralFailureResponse(StructuralActionRequestMessage request)
    {
        return new StructuralActionResponseMessage
        {
            TransactionId = request.TransactionId,
            Kind = request.Kind,
            Message = "结构操作失败，已返还手持物品，请重试。"
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

    private bool TryGetCachedStructuralActionResponse(long playerId, Guid transactionId, out StructuralActionResponseMessage response)
    {
        response = null!;
        return transactionId != Guid.Empty
            && this.structuralResponseCache.TryGetValue((playerId, transactionId), out response!);
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
        this.StructuralActionResponseReceived?.Invoke(response);

        if (response.TransactionId != Guid.Empty && !this.reconciledStructuralTx.Add(response.TransactionId))
            return;

        Game1.addHUDMessage(new HUDMessage(response.Message, response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        this.pendingStructuralHeldItems.TryGetValue(response.TransactionId, out var pending);
        this.pendingStructuralHeldItems.Remove(response.TransactionId);

        if (!response.Success)
        {
            if (pending is not null)
                this.RestoreEscrowedStructuralItem(pending, Game1.currentLocation);

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
        this.ReconcileStructuralResult(
            response.Kind,
            result,
            Game1.currentLocation,
            showMessage: false,
            actingItem: pending?.ActingItem,
            consumeHeldAlreadyApplied: pending?.EscrowedItem is not null && response.ConsumeHeldOne);
    }

    private StructuralActionResponseMessage ExecuteStructuralAction(StructuralActionRequestMessage request)
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
            response.Message = "目标物体不存在。";
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
            request.HeldSerializedItem);

        response.Success = result.Success;
        response.Message = result.Message;
        response.ConsumeHeldOne = result.ConsumeHeldOne;
        response.ReturnedSerializedItem = result.ReturnedSerializedItem;
        response.ResultNetworkId = result.ResultNetworkId;
        return response;
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
        string heldSerializedItem)
    {
        return kind switch
        {
            StructuralActionKind.LinkSelectCore => this.ApplyLinkSelectCore(target, location, tile),
            StructuralActionKind.LinkBindEndpoint => this.ApplyLinkBindEndpoint(target, location, tile, selectedNetworkId),
            StructuralActionKind.StorageDriveInteract => this.storageDriveService.ApplyInteract(target, location, tile, heldQualifiedItemId, heldSerializedItem),
            StructuralActionKind.PatternProviderInteract => this.patternProviderService.ApplyInteract(target, heldQualifiedItemId, heldSerializedItem),
            StructuralActionKind.TransferBusConfigure => this.transferBusService.ApplyConfigure(target, heldQualifiedItemId, heldDisplayName, heldStack),
            _ => StructuralActionResult.Fail("未知操作。")
        };
    }

    private StructuralActionResult ApplyLinkSelectCore(SObject target, GameLocation location, Vector2 tile)
    {
        if (target.QualifiedItemId != "(BC)" + ModItemCatalog.NetworkCore)
            return StructuralActionResult.Fail("请先选择一个网络核心。");

        var networkId = this.endpointIdentityService.EnsureNetworkId(target);
        var endpointId = this.endpointIdentityService.EnsureEndpointId(target);
        target.modData[EndpointIdentityService.NetworkIdKey] = networkId.ToString("N");
        this.repository.UpsertEndpoint(networkId, this.CreateEndpoint(endpointId, location, tile, EndpointType.NetworkCore));

        return new StructuralActionResult
        {
            Success = true,
            Message = "已选择 SVSAP 网络。",
            ResultNetworkId = networkId
        };
    }

    private StructuralActionResult ApplyLinkBindEndpoint(SObject target, GameLocation location, Vector2 tile, Guid selectedNetworkId)
    {
        if (selectedNetworkId == Guid.Empty)
            return StructuralActionResult.Fail("请先选择一个网络核心。");

        var endpointType = this.GetEndpointType(target);
        if (endpointType is null)
            return StructuralActionResult.Fail("这个物体暂时不能加入 SVSAP 网络。");

        var boundEndpointId = this.endpointIdentityService.EnsureEndpointId(target);
        var hadPreviousNetworkId = target.modData.TryGetValue(EndpointIdentityService.NetworkIdKey, out var previousNetworkIdRaw);
        var previousNetworkId = Guid.TryParse(previousNetworkIdRaw, out var parsedPreviousNetworkId)
            ? parsedPreviousNetworkId
            : (Guid?)null;
        var selectedNetwork = this.repository.GetOrCreateNetwork(selectedNetworkId);
        if (selectedNetwork.Endpoints.All(endpoint => endpoint.EndpointId != boundEndpointId)
            && selectedNetwork.Endpoints.Count >= Math.Max(1, this.getConfig().MaxEndpointsPerNetwork))
        {
            return StructuralActionResult.Fail("SVSAP 网络端点数量已达上限。");
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
            Message = "物体已连接到 SVSAP 网络。"
        };
    }

    private void ReconcileStructuralResult(
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
            return;

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
            return;

        try
        {
            var item = SerializedItemCodec.CreateItem(result.ReturnedSerializedItem, 1);
            if (!Game1.player.addItemToInventoryBool(item))
                Game1.createItemDebris(item, Game1.player.getStandingPosition(), -1, location ?? Game1.currentLocation);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not reconcile structural returned item: {ex.Message}", LogLevel.Warn);
        }
    }

    private static string SerializeHeldItem(Item? held)
    {
        return held is null
            ? string.Empty
            : SerializedItemCodec.SerializePrototype(held);
    }

    private void RestorePendingStructuralHeldItems()
    {
        foreach (var pending in this.pendingStructuralHeldItems.Values.ToList())
            this.RestoreEscrowedStructuralItem(pending, Game1.currentLocation);

        this.pendingStructuralHeldItems.Clear();
    }

    private void RestoreEscrowedStructuralItem(PendingStructuralHeldItem pending, GameLocation? location)
    {
        var item = pending.EscrowedItem;
        if (item is null)
            return;

        if (Game1.player is not null && Game1.player.addItemToInventoryBool(item))
            return;

        if (Context.IsWorldReady && Game1.player is not null)
            Game1.createItemDebris(item, Game1.player.getStandingPosition(), -1, location ?? Game1.currentLocation);
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
        if (!this.TryGetActiveLinkedNetwork(terminal, "终端", out var network, out var endpointId, out var message))
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

    private void OpenCraftingMonitor(SObject monitorObject)
    {
        if (!this.TryGetActiveLinkedNetwork(monitorObject, "合成监视器", out var network, out var endpointId, out var message))
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

        if (!this.TryReadLinkedEndpointIds(terminal, "终端", out var networkId, out var endpointId, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            return;
        }

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage("主机必须安装 Stardew Valley Storage and Automation Project 才能使用网络终端。", HUDMessage.error_type));
            return;
        }

        var request = new TerminalSnapshotRequestMessage
        {
            NetworkId = networkId,
            EndpointId = endpointId,
            Crafting = crafting
        };

        this.multiplayerHelper.SendMessage(
            request,
            MultiplayerMessageTypes.TerminalSnapshotRequest,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { host.PlayerID });

        Game1.addHUDMessage(new HUDMessage("已请求 SVSAP 终端快照。", HUDMessage.newQuest_type));
    }

    private void RequestRemoteCraftingSnapshot(SObject terminal)
    {
        if (!this.TryReadLinkedEndpointIds(terminal, "终端", out var networkId, out var endpointId, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            return;
        }

        this.SendRemoteCraftingSnapshotRequest(networkId, endpointId, batches: 1, MaterialQualityStrategy.LowQualityFirst);
    }

    private void RequestRemoteCraftingMonitorSnapshot(SObject monitorObject)
    {
        if (!this.TryReadLinkedEndpointIds(monitorObject, "合成监视器", out var networkId, out var endpointId, out var message))
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
            Game1.addHUDMessage(new HUDMessage("主机必须安装 Stardew Valley Storage and Automation Project 才能使用合成终端。", HUDMessage.error_type));
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

        Game1.addHUDMessage(new HUDMessage("已请求 SVSAP 合成快照。", HUDMessage.newQuest_type));
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
            Game1.addHUDMessage(new HUDMessage("主机必须安装 Stardew Valley Storage and Automation Project 才能使用合成监视器。", HUDMessage.error_type));
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

        Game1.addHUDMessage(new HUDMessage("已请求 SVSAP 监视器快照。", HUDMessage.newQuest_type));
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
            Game1.addHUDMessage(new HUDMessage(response.Message, HUDMessage.error_type));
            return;
        }

        Game1.activeClickableMenu = new RemoteNetworkTerminalMenu(response, this.SendRemoteTerminalActionRequest);
        Game1.playSound("bigSelect");
    }

    private void SendRemoteTerminalActionRequest(TerminalActionRequestMessage request)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage("主机必须安装 Stardew Valley Storage and Automation Project 才能使用网络终端。", HUDMessage.error_type));
            return;
        }

        this.multiplayerHelper.SendMessage(
            request,
            MultiplayerMessageTypes.TerminalActionRequest,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { host.PlayerID });

        this.LogGameplay($"action=remote_terminal_request result=sent player={DescribePlayer(Game1.player)} host={host.PlayerID} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} requestAction={request.Action} amount={request.Amount:N0} tx={ShortId(request.TransactionId)}");
        Game1.addHUDMessage(new HUDMessage("SVSAP 终端请求已发送。", HUDMessage.newQuest_type));
    }

    private void HandleTerminalActionRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<TerminalActionRequestMessage>();
        if (this.TryGetCachedTerminalActionResponse(e.FromPlayerID, request.TransactionId, out var cached))
        {
            this.SendTerminalActionResponse(cached, e.FromPlayerID);
            return;
        }

        var response = this.ExecuteTerminalActionRequest(request, e.FromPlayerID);
        this.RememberTerminalActionResponse(e.FromPlayerID, request.TransactionId, response);

        this.SendTerminalActionResponse(response, e.FromPlayerID);
    }

    private void SendTerminalActionResponse(TerminalActionResponseMessage response, long playerId)
    {
        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.TerminalActionResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { playerId });
    }

    private bool TryGetCachedTerminalActionResponse(long playerId, Guid transactionId, out TerminalActionResponseMessage response)
    {
        response = null!;
        return transactionId != Guid.Empty
            && this.terminalActionResponseCache.TryGetValue((playerId, transactionId), out response!);
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
        Game1.addHUDMessage(new HUDMessage(response.Message, response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        if (response.Snapshot is null || !response.Snapshot.Success)
            return;

        if (Game1.activeClickableMenu is RemoteNetworkTerminalMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
            remoteMenu.ApplySnapshot(response.Snapshot);
        else
            Game1.activeClickableMenu = new RemoteNetworkTerminalMenu(response.Snapshot, this.SendRemoteTerminalActionRequest);
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
            Game1.addHUDMessage(new HUDMessage(response.Message, HUDMessage.error_type));
            return;
        }

        if (Game1.activeClickableMenu is RemoteCraftingTerminalMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
            remoteMenu.ApplySnapshot(response);
        else
            Game1.activeClickableMenu = this.CreateRemoteCraftingMenu(response);

        Game1.playSound("bigSelect");
    }

    private void SendRemoteCraftingActionRequest(CraftingActionRequestMessage request)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage("主机必须安装 Stardew Valley Storage and Automation Project 才能使用合成终端。", HUDMessage.error_type));
            return;
        }

        this.multiplayerHelper.SendMessage(
            request,
            MultiplayerMessageTypes.CraftingActionRequest,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { host.PlayerID });

        this.LogGameplay($"action=remote_crafting_request result=sent player={DescribePlayer(Game1.player)} host={host.PlayerID} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} recipe={Quote(request.RecipeName)} batches={request.Batches:N0} quality={request.QualityStrategy} tx={ShortId(request.TransactionId)}");
        Game1.addHUDMessage(new HUDMessage("SVSAP 合成请求已发送。", HUDMessage.newQuest_type));
    }

    private void HandleCraftingActionRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<CraftingActionRequestMessage>();
        if (this.TryGetCachedCraftingActionResponse(e.FromPlayerID, request.TransactionId, out var cached))
        {
            this.SendCraftingActionResponse(cached, e.FromPlayerID);
            return;
        }

        var response = this.ExecuteCraftingActionRequest(request, e.FromPlayerID);
        this.RememberCraftingActionResponse(e.FromPlayerID, request.TransactionId, response);

        this.SendCraftingActionResponse(response, e.FromPlayerID);
    }

    private void SendCraftingActionResponse(CraftingActionResponseMessage response, long playerId)
    {
        this.multiplayerHelper.SendMessage(
            response,
            MultiplayerMessageTypes.CraftingActionResponse,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { playerId });
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
        Game1.addHUDMessage(new HUDMessage(response.Message, response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        if (response.Snapshot is null || !response.Snapshot.Success)
            return;

        if (Game1.activeClickableMenu is RemoteCraftingTerminalMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
            remoteMenu.ApplySnapshot(response.Snapshot);
        else
            Game1.activeClickableMenu = this.CreateRemoteCraftingMenu(response.Snapshot);
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
            Game1.addHUDMessage(new HUDMessage(response.Message, HUDMessage.error_type));
            return;
        }

        if (Game1.activeClickableMenu is RemoteCraftingMonitorMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
            remoteMenu.ApplySnapshot(response);
        else
            Game1.activeClickableMenu = this.CreateRemoteCraftingMonitorMenu(response);

        Game1.playSound("bigSelect");
    }

    private void SendRemoteCraftingMonitorActionRequest(CraftingMonitorActionRequestMessage request)
    {
        if (Context.IsMainPlayer)
            return;

        var host = this.multiplayerHelper.GetConnectedPlayers().FirstOrDefault(peer => peer.IsHost);
        if (host is null || !host.HasSmapi || host.GetMod(this.modManifest.UniqueID) is null)
        {
            Game1.addHUDMessage(new HUDMessage("主机必须安装 Stardew Valley Storage and Automation Project 才能使用合成监视器。", HUDMessage.error_type));
            return;
        }

        this.multiplayerHelper.SendMessage(
            request,
            MultiplayerMessageTypes.CraftingMonitorActionRequest,
            modIDs: new[] { this.modManifest.UniqueID },
            playerIDs: new[] { host.PlayerID });

        this.LogGameplay($"action=remote_monitor_request result=sent player={DescribePlayer(Game1.player)} host={host.PlayerID} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} requestAction={request.Action} job={(request.JobId.HasValue ? ShortId(request.JobId.Value) : "none")} batches={request.Batches:N0} tx={ShortId(request.TransactionId)}");
        Game1.addHUDMessage(new HUDMessage("SVSAP 监视器请求已发送。", HUDMessage.newQuest_type));
    }

    private void HandleCraftingMonitorActionRequest(ModMessageReceivedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var request = e.ReadAs<CraftingMonitorActionRequestMessage>();
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
        Game1.addHUDMessage(new HUDMessage(response.Message, response.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));

        if (response.Snapshot is null || !response.Snapshot.Success)
            return;

        if (Game1.activeClickableMenu is RemoteCraftingMonitorMenu remoteMenu && remoteMenu.MatchesNetwork(response.NetworkId))
        {
            remoteMenu.ApplySnapshot(response.Snapshot);
            remoteMenu.ApplyActionResult(response);
        }
        else
        {
            var newMenu = this.CreateRemoteCraftingMonitorMenu(response.Snapshot);
            newMenu.ApplyActionResult(response);
            Game1.activeClickableMenu = newMenu;
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
            NetworkId = request.NetworkId
        };

        if (!this.repository.TryGetNetwork(request.NetworkId, out var network))
        {
            response.Success = false;
            response.Message = "终端没有连接到网络。";
            return response;
        }

        var player = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        if (player is null)
        {
            response.Success = false;
            response.Message = "发起请求的玩家已不在线。";
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
        var success = request.Action switch
        {
            TerminalActionKind.Withdraw when request.ItemKey is not null
                => this.transactionService.TryWithdrawForPlayer(network, player, request.ItemKey, request.Amount, out actionMessage),
            TerminalActionKind.DepositSame
                => this.transactionService.TryDepositFromPlayer(network, player, sameOnly: true, out actionMessage),
            TerminalActionKind.DepositAll
                => this.transactionService.TryDepositFromPlayer(network, player, sameOnly: false, out actionMessage),
            TerminalActionKind.ToggleHeldItemLock
                => this.TryToggleHeldItemLock(network, player, request, out actionMessage),
            _ => Fail("不支持的终端请求。", out actionMessage)
        };

        if (success)
            this.transactionService.SaveNetworkState();

        this.LogGameplay($"action=terminal_action result={(success ? "success" : "fail")} player={DescribePlayer(player)} network={ShortId(request.NetworkId)} endpoint={ShortId(request.EndpointId)} requestAction={request.Action} amount={request.Amount:N0} tx={ShortId(request.TransactionId)} message={Quote(actionMessage)}");
        response.Success = success;
        response.Message = actionMessage;
        response.Snapshot = this.CreateTerminalSnapshotResponse(new TerminalSnapshotRequestMessage { NetworkId = request.NetworkId, EndpointId = request.EndpointId });
        return response;
    }

    private bool TryToggleHeldItemLock(NetworkData network, Farmer player, TerminalActionRequestMessage request, out string message)
    {
        var id = request.HeldQualifiedItemId;
        if (string.IsNullOrWhiteSpace(id))
        {
            message = "请先手持要锁定或解锁的物品。";
            return false;
        }

        var displayName = string.IsNullOrWhiteSpace(request.HeldDisplayName)
            ? id
            : request.HeldDisplayName;
        var removed = network.LockedQualifiedItemIds.RemoveAll(candidate => string.Equals(candidate, id, StringComparison.Ordinal));
        if (removed > 0)
        {
            this.repository.Save();
            message = $"SVSAP 已解除锁定：{displayName}。";
            this.LogGameplay($"action=toggle_item_lock result=success player={DescribePlayer(player)} network={ShortId(network.NetworkId)} mode=unlock item={Quote(displayName)} itemId={Quote(id)}");
            return true;
        }

        network.LockedQualifiedItemIds.Add(id);
        network.LockedQualifiedItemIds.Sort(StringComparer.Ordinal);
        this.repository.Save();
        message = $"SVSAP 已锁定：{displayName}。";
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
            response.Message = "终端没有连接到网络。";
            return response;
        }

        var player = Game1.GetPlayer(fromPlayerId, onlyOnline: true);
        if (player is null)
        {
            response.Success = false;
            response.Message = "发起请求的玩家已不在线。";
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
            response.Message = "该玩家尚未掌握这个合成配方。";
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
            response.Message = "合成监视器没有连接到网络。";
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

            case CraftingMonitorActionKind.QueueJob when request.QueuePattern is not null:
                var batches = Math.Max(1, request.Batches);
                if (this.patternExecutionService.NeedsLongJobConfirmation(network, request.QueuePattern, batches)
                    && !request.ConfirmLongJob)
                {
                    response.Success = false;
                    response.RequiresConfirmation = true;
                    response.Message = "长耗时 CPU 作业会占用一个 CPU 槽位。再点一次排队确认。";
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
                success = Fail("不支持的合成监视器请求。", out actionMessage);
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
                Message = "终端没有连接到网络。"
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
        return new TerminalSnapshotResponseMessage
        {
            NetworkId = request.NetworkId,
            EndpointId = request.EndpointId,
            Success = true,
            NetworkName = network.Name,
            SourceCount = snapshot.SourceCount,
            StorageSummary = snapshot.StorageSummary,
            LockedQualifiedItemIds = network.LockedQualifiedItemIds.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            Entries = snapshot.Entries
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
            response.Message = "终端没有连接到网络。";
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
            response.Message = "发起请求的玩家已不在线。";
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
                    MissingLines = availability.MissingLines
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
                Message = "合成监视器没有连接到网络。"
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
                        DisplayName = job.Pattern.DisplayName,
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
                    DisplayName = pipeline.Pattern.DisplayName,
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
            message = "请先手持可陈酿的物品：酒、啤酒、淡啤酒、蜂蜜酒、奶酪或山羊奶酪。";
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
            message = "SVSAP 端点物体已不在保存的位置。";
            return false;
        }

        if (endpoint.Type == EndpointType.NetworkCore)
        {
            message = string.Empty;
            return true;
        }

        if (!this.TryGetCoreEndpoint(network, out var core))
        {
            message = "请先选择或放置 SVSAP 网络核心。";
            return false;
        }

        if (!this.IsEndpointPhysicallyPresent(network, core))
        {
            message = "SVSAP 网络核心已不在保存的位置。";
            return false;
        }

        if (this.getConfig().RequireCables)
            return this.HasCablePathToCore(core, endpoint, out message);

        if (!this.getConfig().EnableSimpleWirelessWithinFarm)
        {
            message = "SVSAP 简易无线已禁用；请启用它，或改用线缆连接。";
            return false;
        }

        if (!string.Equals(core.LocationName, endpoint.LocationName, StringComparison.Ordinal))
        {
            message = "SVSAP 简易无线只能连接与核心同地图的物体。";
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
            message = $"{label}没有连接到网络。";
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
            message = $"{label}没有连接到网络。";
            return false;
        }

        if (!Guid.TryParse(endpointObject.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out endpointId))
        {
            message = $"{label}缺少端点身份信息。";
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
            message = "SVSAP 端点未注册到这个网络。";
            return false;
        }

        if (expectedType.HasValue && endpoint.Type != expectedType.Value)
        {
            message = "SVSAP 端点类型与本次请求不匹配。";
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
            message = "线缆模式要求端点和核心在同一张地图。";
            return false;
        }

        var location = Game1.getLocationFromName(endpoint.LocationName);
        if (location is null)
        {
            message = "SVSAP 找不到这个端点所在的地图。";
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

        message = "线缆模式需要一条连接到核心的 SVSAP 线缆路径。";
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
        if (target is Chest)
            return EndpointType.Chest;

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

    private sealed record PendingStructuralHeldItem(Item ActingItem, Item? EscrowedItem);
}
