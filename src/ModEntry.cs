using SVSAP.Content;
using SVSAP.Integrations;
using SVSAP.Models;
using SVSAP.Services;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAP;

public sealed class ModEntry : Mod
{
    private ModConfig config = new();
    private EndpointIdentityService endpointIdentityService = null!;
    private StorageCellInitializer storageCellInitializer = null!;
    private NetworkRepository networkRepository = null!;
    private StorageDriveService storageDriveService = null!;
    private NetworkInteractionService networkInteractionService = null!;
    private TransferBusService transferBusService = null!;
    private PatternProviderService patternProviderService = null!;
    private PatternExecutionService patternExecutionService = null!;
    private RuntimeSelfTestService runtimeSelfTestService = null!;
    private RouteSVSAPE2EService routeSVSAPE2EService = null!;
    private readonly HashSet<long> warnedMissingSVSAPPeers = new();

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        this.config.Language = ModText.NormalizeLanguage(this.config.Language);
        ModText.Load(helper, this.config.Language, this.Monitor);

        var contentInjector = new ContentInjector(() => this.config, this.Monitor);
        this.endpointIdentityService = new EndpointIdentityService(this.Monitor);
        this.storageCellInitializer = new StorageCellInitializer(this.Monitor);
        this.networkRepository = new NetworkRepository(helper, this.Monitor);
        var inventoryScanner = new InventoryScanner(() => this.config, this.Monitor);
        var inventoryTransactionService = new InventoryTransactionService(this.networkRepository, inventoryScanner, () => this.config, this.Monitor);
        var craftingRecipeService = new CraftingRecipeService(inventoryTransactionService, () => this.config, this.Monitor);
        var patternEncodingService = new PatternEncodingService(craftingRecipeService, () => this.config);
        var tickOperationBudget = new TickOperationBudget();
        this.patternProviderService = new PatternProviderService(this.networkRepository, this.endpointIdentityService);
        this.transferBusService = new TransferBusService(this.networkRepository, inventoryTransactionService, () => this.config, tickOperationBudget, this.Monitor);
        this.patternExecutionService = new PatternExecutionService(this.networkRepository, inventoryTransactionService, () => this.config, tickOperationBudget, this.Monitor);
        this.storageDriveService = new StorageDriveService(
            this.networkRepository,
            this.endpointIdentityService,
            this.storageCellInitializer,
            () => this.config,
            this.Monitor);
        this.networkInteractionService = new NetworkInteractionService(
            this.networkRepository,
            this.endpointIdentityService,
            inventoryScanner,
            inventoryTransactionService,
            craftingRecipeService,
            this.storageDriveService,
            this.transferBusService,
            patternEncodingService,
            this.patternProviderService,
            this.patternExecutionService,
            () => this.config,
            helper.Input,
            helper.Multiplayer,
            this.ModManifest,
            this.Monitor);
        this.runtimeSelfTestService = new RuntimeSelfTestService(
            this.networkRepository,
            inventoryTransactionService,
            this.storageCellInitializer,
            this.storageDriveService,
            this.transferBusService,
            this.patternExecutionService,
            this.networkInteractionService,
            () => this.config,
            this.Monitor);
        this.routeSVSAPE2EService = new RouteSVSAPE2EService(
            helper,
            this.Monitor,
            this.networkRepository,
            this.networkInteractionService,
            this.storageCellInitializer);

        helper.Events.Content.AssetRequested += contentInjector.OnAssetRequested;
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += this.OnDayStarted;
        helper.Events.GameLoop.Saving += this.OnSaving;
        helper.Events.GameLoop.UpdateTicked += this.transferBusService.OnUpdateTicked;
        helper.Events.GameLoop.UpdateTicked += this.patternExecutionService.OnUpdateTicked;
        helper.Events.Player.InventoryChanged += this.OnInventoryChanged;
        helper.Events.Input.ButtonPressed += this.networkInteractionService.OnButtonPressed;
        helper.Events.Multiplayer.PeerContextReceived += this.OnPeerContextReceived;
        helper.Events.Multiplayer.PeerDisconnected += this.OnPeerDisconnected;
        helper.Events.Multiplayer.ModMessageReceived += this.networkInteractionService.OnModMessageReceived;
        helper.Events.World.ObjectListChanged += this.OnObjectListChanged;
        helper.Events.World.TerrainFeatureListChanged += this.OnTerrainFeatureListChanged;

        helper.ConsoleCommands.Add(
            "svsap_m1_ids",
            "列出 Stardew Valley Storage and Automation Project M1 物品 ID 和存储元件字节容量。",
            this.CommandListM1Ids);
        helper.ConsoleCommands.Add(
            "svsap_selftest",
            "运行 Stardew Valley Storage and Automation Project 运行时自测：存储元件、事务恢复、远程缓存、保留来源产物和 CPU 槽位预留。",
            this.runtimeSelfTestService.RunCommand);

        this.routeSVSAPE2EService.Start();
        this.Monitor.Log("Stardew Valley Storage and Automation Project loaded. Storage network, digital cells, transfer buses, processing pipelines, and autocrafting services are active.", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.RegisterGmcm();
        if (string.Equals(Environment.GetEnvironmentVariable("STARDEW_SVSAP_RUN_SELFTEST"), "1", StringComparison.Ordinal))
            this.runtimeSelfTestService.RunCommand("svsap_selftest", Array.Empty<string>());
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.warnedMissingSVSAPPeers.Clear();
        this.networkInteractionService.ClearActionResponseCaches();

        if (Game1.player is not null)
        {
            this.storageCellInitializer.InitializeInventory(Game1.player.Items);
            SyncSVSAPCraftingRecipeUnlocks(Game1.player);
        }

        if (!Context.IsMainPlayer)
        {
            this.Monitor.Log("Stardew Valley Storage and Automation Project network data is host-authoritative; this client will not load or mutate network save data.", LogLevel.Info);
            return;
        }

        this.networkRepository.Load();
        this.networkInteractionService.RecoverPendingTransactions();
        this.networkInteractionService.RebuildPlacedEndpointCache();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (Game1.player is not null)
            SyncSVSAPCraftingRecipeUnlocks(Game1.player);
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        this.networkRepository.Save();
    }

    private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e)
    {
        this.storageCellInitializer.InitializeInventory(e.Player.Items);
    }

    private static void SyncSVSAPCraftingRecipeUnlocks(Farmer player)
    {
        foreach (var pair in ModItemCatalog.CraftingRecipeMiningLevels)
        {
            if (player.MiningLevel >= pair.Value)
            {
                if (!player.craftingRecipes.ContainsKey(pair.Key))
                    player.craftingRecipes.Add(pair.Key, 0);
            }
            else
            {
                player.craftingRecipes.Remove(pair.Key);
            }
        }
    }

    private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        var shouldRefreshConnectivity = false;
        var movedEndpointIds = e.Added
            .Select(pair => TryReadEndpointId(pair.Value, out var endpointId) ? endpointId : (Guid?)null)
            .Where(endpointId => endpointId.HasValue)
            .Select(endpointId => endpointId!.Value)
            .ToHashSet();

        foreach (var pair in e.Removed)
        {
            if (TryReadEndpointId(pair.Value, out var removedEndpointId) && movedEndpointIds.Contains(removedEndpointId))
            {
                shouldRefreshConnectivity = true;
                continue;
            }

            if (pair.Value.QualifiedItemId == "(BC)" + ModItemCatalog.StorageDrive)
            {
                this.storageDriveService.HandleStorageDriveRemoved(pair.Value, e.Location, pair.Key);
            }
            else if (pair.Value.QualifiedItemId == "(BC)" + ModItemCatalog.PatternProvider)
            {
                this.patternProviderService.HandlePatternProviderRemoved(pair.Value, e.Location, pair.Key);
            }
            else if (this.networkInteractionService.TryRemovePlacedEndpoint(pair.Value))
            {
                shouldRefreshConnectivity = true;
            }

            if (this.IsConnectivityRelevantObject(pair.Value))
                shouldRefreshConnectivity = true;
        }

        foreach (var pair in e.Added)
        {
            if (this.networkInteractionService.TryRegisterPlacedEndpoint(pair.Value, e.Location, pair.Key))
            {
                shouldRefreshConnectivity = true;
            }
            else if (ModItemCatalog.IsNetworkEndpoint(pair.Value.QualifiedItemId))
            {
                this.endpointIdentityService.EnsureEndpointId(pair.Value);
                shouldRefreshConnectivity = true;
            }
            else if (this.IsConnectivityRelevantObject(pair.Value))
            {
                shouldRefreshConnectivity = true;
            }
        }

        if (shouldRefreshConnectivity)
            this.networkInteractionService.RefreshEndpointConnectivity();
    }

    private static bool TryReadEndpointId(SObject placedObject, out Guid endpointId)
    {
        return Guid.TryParse(placedObject.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out endpointId);
    }

    private void OnTerrainFeatureListChanged(object? sender, TerrainFeatureListChangedEventArgs e)
    {
        if (!Context.IsMainPlayer)
            return;

        if (e.Added.Any(pair => this.IsNetworkCableTerrain(pair.Value))
            || e.Removed.Any(pair => this.IsNetworkCableTerrain(pair.Value)))
        {
            this.networkInteractionService.RefreshEndpointConnectivity();
        }
    }

    private void OnPeerContextReceived(object? sender, PeerContextReceivedEventArgs e)
    {
        this.WarnIfPeerMissingRequiredMod(e.Peer);
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        this.warnedMissingSVSAPPeers.Remove(e.Peer.PlayerID);
        if (!Context.IsMainPlayer && e.Peer.IsHost)
            this.networkInteractionService.ClearActionResponseCaches();
        else
            this.networkInteractionService.ClearActionResponseCaches(e.Peer.PlayerID);
    }

    private void WarnIfPeerMissingRequiredMod(IMultiplayerPeer peer)
    {
        if (Context.IsMainPlayer)
        {
            if (!peer.IsHost && !this.PeerHasThisMod(peer))
                this.WarnMissingRequiredMod(peer, "Stardew Valley Storage and Automation Project requires every player to install this mod. A connected player is missing it, so SVSAP custom machines may display incorrectly for them.");

            return;
        }

        if (peer.IsHost && !this.PeerHasThisMod(peer))
            this.WarnMissingRequiredMod(peer, "Stardew Valley Storage and Automation Project is installed locally, but the host is missing it. SVSAP network features are unavailable in this multiplayer save.");
    }

    private bool PeerHasThisMod(IMultiplayerPeer peer)
    {
        return peer.HasSmapi && peer.GetMod(this.ModManifest.UniqueID) is not null;
    }

    private bool IsConnectivityRelevantObject(SObject obj)
    {
        return ModItemCatalog.IsNetworkEndpoint(obj.QualifiedItemId)
            || obj.QualifiedItemId == "(O)" + ModItemCatalog.NetworkCable;
    }

    private bool IsNetworkCableTerrain(StardewValley.TerrainFeatures.TerrainFeature terrainFeature)
    {
        if (terrainFeature is not StardewValley.TerrainFeatures.Flooring flooring)
            return false;

        var data = flooring.GetData();
        return data?.ItemId == ModItemCatalog.NetworkCable
            || data?.Id == ModItemCatalog.NetworkCable;
    }

    private void WarnMissingRequiredMod(IMultiplayerPeer peer, string message)
    {
        if (!this.warnedMissingSVSAPPeers.Add(peer.PlayerID))
            return;

        var detail = peer.HasSmapi
            ? $"peer {peer.PlayerID} does not have {this.ModManifest.UniqueID} installed"
            : $"peer {peer.PlayerID} is not running SMAPI";

        this.Monitor.Log($"{message} ({detail})", LogLevel.Warn);

        if (Context.IsWorldReady)
            Game1.addHUDMessage(new HUDMessage("Stardew Valley Storage and Automation Project：所有玩家都必须安装此模组。", HUDMessage.error_type));
    }

    private void RegisterGmcm()
    {
        var gmcm = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        if (gmcm is null)
            return;

        gmcm.Register(
            mod: this.ModManifest,
            reset: () => this.config = new ModConfig(),
            save: () =>
            {
                this.config.Language = ModText.NormalizeLanguage(this.config.Language);
                ModText.Load(this.Helper, this.config.Language, this.Monitor);
                this.Helper.WriteConfig(this.config);
                this.Helper.GameContent.InvalidateCache("Data/Objects");
                this.Helper.GameContent.InvalidateCache("Data/BigCraftables");
                this.Helper.GameContent.InvalidateCache("Data/CraftingRecipes");
                if (Context.IsWorldReady && Context.IsMainPlayer)
                    this.networkInteractionService.RefreshEndpointConnectivity();
            });

        gmcm.AddTextOption(
            this.ModManifest,
            () => ModText.NormalizeLanguage(this.config.Language),
            value => this.config.Language = ModText.NormalizeLanguage(value),
            () => ModText.Get("gmcm.language.name"),
            () => ModText.Get("gmcm.language.tooltip"),
            allowedValues: new[] { ModText.Chinese, ModText.English },
            formatAllowedValue: ModText.FormatLanguage);
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableSimpleWirelessWithinFarm, value => this.config.EnableSimpleWirelessWithinFarm = value, () => ModText.Get("gmcm.simpleWireless"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.RequireCables, value => this.config.RequireCables = value, () => ModText.Get("gmcm.requireCables"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableAutocrafting, value => this.config.EnableAutocrafting = value, () => ModText.Get("gmcm.enableAutocrafting"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.EnableProcessingPatterns, value => this.config.EnableProcessingPatterns = value, () => ModText.Get("gmcm.enableProcessingPatterns"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.RequireConfirmForLongCpuJobs, value => this.config.RequireConfirmForLongCpuJobs = value, () => ModText.Get("gmcm.confirmLongJobs"));
        gmcm.AddNumberOption(this.ModManifest, () => this.config.ReserveFastSlots, value => this.config.ReserveFastSlots = value, () => ModText.Get("gmcm.reserveFastSlots"), min: 0, max: 16);
        gmcm.AddNumberOption(this.ModManifest, () => this.config.LongJobThresholdMinutes, value => this.config.LongJobThresholdMinutes = value, () => ModText.Get("gmcm.longJobThreshold"), min: 0, max: 10000);
        gmcm.AddNumberOption(this.ModManifest, () => this.config.CaskTargetQuality, value => this.config.CaskTargetQuality = NormalizeCaskTargetQuality(value), () => ModText.Get("gmcm.caskTargetQuality"), min: 0, max: 4);
        gmcm.AddBoolOption(this.ModManifest, () => this.config.AllowToolsInNetwork, value => this.config.AllowToolsInNetwork = value, () => ModText.Get("gmcm.allowTools"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.AllowWeaponsInNetwork, value => this.config.AllowWeaponsInNetwork = value, () => ModText.Get("gmcm.allowWeapons"));
        gmcm.AddNumberOption(this.ModManifest, () => this.config.MaxEndpointsPerNetwork, value => this.config.MaxEndpointsPerNetwork = value, () => ModText.Get("gmcm.maxEndpoints"), min: 1, max: 1000);
        gmcm.AddNumberOption(this.ModManifest, () => this.config.MaxStorageCellsPerNetwork, value => this.config.MaxStorageCellsPerNetwork = value, () => ModText.Get("gmcm.maxStorageCells"), min: 1, max: 256);
        gmcm.AddNumberOption(this.ModManifest, () => this.config.MaxItemTypesPerStorageCell, value => this.config.MaxItemTypesPerStorageCell = value, () => ModText.Get("gmcm.maxItemTypes"), min: 1, max: 63);
        gmcm.AddBoolOption(this.ModManifest, () => this.config.PreferStorageCellsForDeposits, value => this.config.PreferStorageCellsForDeposits = value, () => ModText.Get("gmcm.preferCells"));
        gmcm.AddNumberOption(this.ModManifest, () => this.config.MaxOperationsPerTick, value => this.config.MaxOperationsPerTick = value, () => ModText.Get("gmcm.maxOps"), min: 1, max: 200);
        gmcm.AddBoolOption(this.ModManifest, () => this.config.DetailedGameplayLogs, value => this.config.DetailedGameplayLogs = value, () => ModText.Get("gmcm.detailedLogs"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.DebugTransactionLogs, value => this.config.DebugTransactionLogs = value, () => ModText.Get("gmcm.debugLogs"));
        gmcm.AddBoolOption(this.ModManifest, () => this.config.CasualRecipeCosts, value => this.config.CasualRecipeCosts = value, () => ModText.Get("gmcm.casualCosts"));
    }

    private void CommandListM1Ids(string command, string[] args)
    {
        foreach (var item in ModItemCatalog.ObjectItems)
            this.Monitor.Log($"{item.DisplayName}: (O){item.Id}", LogLevel.Info);

        foreach (var item in ModItemCatalog.BigCraftables)
            this.Monitor.Log($"{item.DisplayName}: (BC){item.Id}", LogLevel.Info);

        foreach (var tier in StorageCellTierInfo.All)
            this.Monitor.Log($"{tier.DisplayName}: bytes={tier.Capacity:N0}, singleTypeItems={StorageCellTierInfo.GetSingleTypeItemLimit(tier.Tier):N0}, typeLimit=63", LogLevel.Info);
    }

    private static int NormalizeCaskTargetQuality(int value)
    {
        if (value >= 4)
            return 4;

        if (value >= 2)
            return 2;

        if (value >= 1)
            return 1;

        return 0;
    }
}
