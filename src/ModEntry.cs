using System.Globalization;
using Koizumi.SVSAP.Api;
using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Api;
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
#if DEBUG
    private RuntimeSelfTestService runtimeSelfTestService = null!;
    private RouteSVSAPE2EService routeSVSAPE2EService = null!;
#endif
    private SvsapApi? api;
    private readonly HashSet<long> warnedMissingSVSAPPeers = new();

    public override void Entry(IModHelper helper)
    {
        var configPath = Path.Combine(helper.DirectoryPath, "config.json");
        var configExists = File.Exists(configPath);
        this.config = helper.ReadConfig<ModConfig>();
        this.NormalizeConfig();
        this.config.Language = configExists
            ? ModText.NormalizeLanguage(this.config.Language)
            : GetDefaultLanguageForLocale(helper);
        if (!configExists)
            helper.WriteConfig(this.config);

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
        this.api = new SvsapApi(
            this.networkRepository,
            inventoryTransactionService,
            () => this.config);
#if DEBUG
        this.runtimeSelfTestService = new RuntimeSelfTestService(
            this.networkRepository,
            inventoryTransactionService,
            this.storageCellInitializer,
            this.storageDriveService,
            this.transferBusService,
            this.patternExecutionService,
            this.networkInteractionService,
            craftingRecipeService,
            () => this.config,
            this.Monitor);
        this.routeSVSAPE2EService = new RouteSVSAPE2EService(
            helper,
            this.Monitor,
            this.networkRepository,
            this.networkInteractionService,
            this.storageCellInitializer);
#endif

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
            "List Stardew Valley Storage and Automation Project M1 item IDs and storage cell byte capacities.",
            this.CommandListM1Ids);
#if DEBUG
        helper.ConsoleCommands.Add(
            "svsap_selftest",
            "Run Stardew Valley Storage and Automation Project runtime self-tests.",
            this.runtimeSelfTestService.RunCommand);
#endif
        helper.ConsoleCommands.Add(
            "svsap_api_dump",
            "Print the SVSAP API version, modData keys, and config snapshot used by SVSAPME.",
            this.CommandApiDump);
        helper.ConsoleCommands.Add(
            "svsap_endpoint_probe",
            "Probe the SVSAP network endpoint binding at the facing tile or at x y.",
            this.CommandEndpointProbe);
        helper.ConsoleCommands.Add(
            "svsap_api_selftest",
            "Run the SVSAP API contract self-test used by SVSAPME; optional x y validates a linked endpoint.",
            this.CommandApiSelfTest);

#if DEBUG
        this.routeSVSAPE2EService.Start();
#endif
        this.Monitor.Log("Stardew Valley Storage and Automation Project loaded. Storage network, digital cells, transfer buses, processing pipelines, and autocrafting services are active.", LogLevel.Info);
    }

    public override object? GetApi()
    {
        return this.api;
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        this.RegisterGmcm();
#if DEBUG
        if (string.Equals(Environment.GetEnvironmentVariable("STARDEW_SVSAP_RUN_SELFTEST"), "1", StringComparison.Ordinal))
            this.runtimeSelfTestService.RunCommand("svsap_selftest", Array.Empty<string>());
#endif
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.warnedMissingSVSAPPeers.Clear();
        this.networkInteractionService.ClearActionResponseCachesWithoutRestore();

        if (Game1.player is not null)
        {
            this.storageCellInitializer.InitializeInventory(Game1.player.Items);
            SyncSVSAPCraftingRecipeUnlocks(Game1.player, this.config);
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
            SyncSVSAPCraftingRecipeUnlocks(Game1.player, this.config);
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

    private static void SyncSVSAPCraftingRecipeUnlocks(Farmer player, ModConfig config)
    {
        if (config.IsDebugRecipeCostMode())
        {
            foreach (var recipeName in ModItemCatalog.CraftingRecipes.Keys)
            {
                if (!player.craftingRecipes.ContainsKey(recipeName))
                    player.craftingRecipes.Add(recipeName, 0);
            }

            return;
        }

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
        this.UpdatePeerActionCompatibility(e.Peer);
    }

    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        this.warnedMissingSVSAPPeers.Remove(e.Peer.PlayerID);
        this.networkInteractionService.ClearPeerActionBlock(e.Peer.PlayerID);
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

    private void UpdatePeerActionCompatibility(IMultiplayerPeer peer)
    {
        this.networkInteractionService.ClearPeerActionBlock(peer.PlayerID);

        if (!Context.IsMainPlayer || peer.IsHost)
            return;

        var peerMod = peer.HasSmapi ? peer.GetMod(this.ModManifest.UniqueID) : null;
        if (peerMod is null)
            return;

        var localVersion = this.ModManifest.Version.ToString();
        var peerVersion = peerMod.Version.ToString();
        if (string.Equals(localVersion, peerVersion, StringComparison.OrdinalIgnoreCase))
            return;

        var message = ModText.Format("multiplayer.versionMismatch", localVersion, peerVersion);
        this.networkInteractionService.SetPeerActionBlock(peer.PlayerID, message);
        this.WarnPeerVersionMismatch(peer, message, localVersion, peerVersion);
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
            Game1.addHUDMessage(new HUDMessage(ModText.Get("multiplayer.allPlayersNeedSVSAP"), HUDMessage.error_type));
    }

    private void WarnPeerVersionMismatch(IMultiplayerPeer peer, string message, string localVersion, string peerVersion)
    {
        if (!this.warnedMissingSVSAPPeers.Add(peer.PlayerID))
            return;

        this.Monitor.Log($"{message} (peer {peer.PlayerID} has {peerVersion}; host has {localVersion})", LogLevel.Warn);

        if (Context.IsWorldReady)
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
    }

    private static string GetDefaultLanguageForLocale(IModHelper helper)
    {
        return ModText.NormalizeLanguage(helper.Translation.Locale.ToString());
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
                this.NormalizeConfig();
                this.config.Language = ModText.NormalizeLanguage(this.config.Language);
                ModText.Load(this.Helper, this.config.Language, this.Monitor);
                this.Helper.WriteConfig(this.config);
                this.Helper.GameContent.InvalidateCache("Data/Objects");
                this.Helper.GameContent.InvalidateCache("Data/BigCraftables");
                this.Helper.GameContent.InvalidateCache("Data/CraftingRecipes");
                if (Context.IsWorldReady && Game1.player is not null)
                    SyncSVSAPCraftingRecipeUnlocks(Game1.player, this.config);
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
        gmcm.AddTextOption(
            this.ModManifest,
            () => this.config.GetRecipeCostMode(),
            value =>
            {
                this.config.RecipeCostMode = RecipeCostModes.Normalize(value);
                this.config.CasualRecipeCosts = this.config.RecipeCostMode == RecipeCostModes.Casual;
            },
            () => ModText.Get("gmcm.recipeCostMode.name"),
            () => ModText.Get("gmcm.recipeCostMode.tooltip"),
            allowedValues: RecipeCostModes.All,
            formatAllowedValue: FormatRecipeCostMode);
    }

    private void NormalizeConfig()
    {
        this.config.ReserveFastSlots = Math.Clamp(this.config.ReserveFastSlots, 0, 16);
        this.config.LongJobThresholdMinutes = Math.Clamp(this.config.LongJobThresholdMinutes, 0, 10000);
        this.config.CaskTargetQuality = NormalizeCaskTargetQuality(this.config.CaskTargetQuality);
        this.config.MaxEndpointsPerNetwork = Math.Clamp(this.config.MaxEndpointsPerNetwork, 1, 1000);
        this.config.MaxStorageCellsPerNetwork = Math.Clamp(this.config.MaxStorageCellsPerNetwork, 1, 256);
        this.config.MaxItemTypesPerStorageCell = Math.Clamp(this.config.MaxItemTypesPerStorageCell, 1, 63);
        this.config.MaxOperationsPerTick = Math.Clamp(this.config.MaxOperationsPerTick, 1, 200);
        this.config.NormalizeRecipeCostMode();
    }

    private static string FormatRecipeCostMode(string value)
    {
        return ModText.Get("gmcm.recipeCostMode." + RecipeCostModes.Normalize(value));
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

    private void CommandApiDump(string command, string[] args)
    {
        var api = this.api;
        if (api is null)
        {
            this.Monitor.Log("SVSAP_API_DUMP_FAIL API is not initialized.", LogLevel.Error);
            return;
        }

        var snapshot = api.GetConfigSnapshot();
        this.Monitor.Log(
            $"SVSAP_API_DUMP ApiVersion={api.ApiVersion}; IsHostAuthority={api.IsHostAuthority}; NetworkIdKey={api.NetworkIdModDataKey}; EndpointIdKey={api.EndpointIdModDataKey}; EnableSimpleWirelessWithinFarm={snapshot.EnableSimpleWirelessWithinFarm}; RequireCables={snapshot.RequireCables}; MaxEndpointsPerNetwork={snapshot.MaxEndpointsPerNetwork}; MaxOperationsPerTick={snapshot.MaxOperationsPerTick}; LoadedNetworks={this.networkRepository.Data.Networks.Count:N0}",
            LogLevel.Info);
    }

    private void CommandEndpointProbe(string command, string[] args)
    {
        var api = this.api;
        if (api is null)
        {
            this.Monitor.Log("SVSAP_ENDPOINT_PROBE_FAIL API is not initialized.", LogLevel.Error);
            return;
        }

        if (!TryResolveProbeTile(args, out var tile, out var tileSource, out var resolveMessage))
        {
            this.Monitor.Log($"SVSAP_ENDPOINT_PROBE_FAIL {resolveMessage}", LogLevel.Warn);
            return;
        }

        var location = Game1.currentLocation;
        if (location is null)
        {
            this.Monitor.Log("SVSAP_ENDPOINT_PROBE_FAIL current location is null.", LogLevel.Warn);
            return;
        }

        var target = location.Objects.TryGetValue(tile, out var placedObject)
            ? placedObject.QualifiedItemId
            : "(none)";
        if (api.TryGetLinkedEndpoint(location, tile, out var endpoint, out var code, out var message) && endpoint is not null)
        {
            this.Monitor.Log(
                $"SVSAP_ENDPOINT_PROBE_OK source={tileSource}; location={location.NameOrUniqueName}; tile=({tile.X:0},{tile.Y:0}); target={target}; network={endpoint.NetworkId}; endpoint={endpoint.EndpointId}; type={endpoint.EndpointType}; active={endpoint.Active}",
                LogLevel.Info);
            return;
        }

        this.Monitor.Log(
            $"SVSAP_ENDPOINT_PROBE_MISS source={tileSource}; location={location.NameOrUniqueName}; tile=({tile.X:0},{tile.Y:0}); target={target}; code={code}; message={message}",
            LogLevel.Info);
    }

    private void CommandApiSelfTest(string command, string[] args)
    {
        var api = this.api;
        if (api is null)
        {
            this.Monitor.Log("SVSAP_API_SELFTEST_FAIL API is not initialized.", LogLevel.Error);
            return;
        }

        var passed = new List<string>();
        var skipped = new List<string>();

        try
        {
            Require(api.ApiVersion == 1, $"ApiVersion should be 1, actual {api.ApiVersion}.");
            Require(!string.IsNullOrWhiteSpace(api.NetworkIdModDataKey), "NetworkIdModDataKey must not be empty.");
            Require(!string.IsNullOrWhiteSpace(api.EndpointIdModDataKey), "EndpointIdModDataKey must not be empty.");
            passed.Add("api-shape");

            var snapshot = api.GetConfigSnapshot();
            Require(snapshot.MaxEndpointsPerNetwork > 0, "MaxEndpointsPerNetwork must be positive.");
            Require(snapshot.MaxOperationsPerTick > 0, "MaxOperationsPerTick must be positive.");
            passed.Add("config-snapshot");

            if (api.IsHostAuthority)
            {
                this.TestHostApiMutationGuards(api);
                passed.Add("host-item-lock-guards");
            }
            else
            {
                TestNonHostApiGuards(api);
                passed.Add("non-host-write-guard");
                skipped.Add("host-item-lock-guards: requires main-player world authority");
            }

            if (TryResolveProbeTile(args, out var tile, out var tileSource, out var resolveMessage))
            {
                var location = Game1.currentLocation;
                if (location is null)
                {
                    skipped.Add("endpoint-probe: no current location");
                }
                else if (api.TryGetLinkedEndpoint(location, tile, out var endpoint, out var code, out var message) && endpoint is not null)
                {
                    Require(endpoint.NetworkId != Guid.Empty, "Endpoint network Guid must not be empty.");
                    Require(endpoint.EndpointId != Guid.Empty, "Endpoint Guid must not be empty.");
                    passed.Add($"endpoint-probe:{tileSource}:{endpoint.EndpointType}");
                }
                else
                {
                    skipped.Add($"endpoint-probe:{tileSource}:{code}:{message}");
                }
            }
            else
            {
                skipped.Add($"endpoint-probe:{resolveMessage}");
            }

            this.Monitor.Log(
                $"SVSAP_API_SELFTEST_OK passed={passed.Count:N0} [{string.Join(", ", passed)}]; skipped={skipped.Count:N0} [{string.Join(", ", skipped)}]",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log(
                $"SVSAP_API_SELFTEST_FAIL passed={passed.Count:N0} [{string.Join(", ", passed)}]; error={ex.GetType().Name}: {ex.Message}",
                LogLevel.Error);
        }
    }

    private void TestHostApiMutationGuards(ISvsapApi api)
    {
        var networkId = Guid.NewGuid();
        var network = this.networkRepository.GetOrCreateNetwork(networkId);
        try
        {
            network.LockedQualifiedItemIds.Add("(O)388");

            var source = ItemRegistry.Create("(O)388");
            source.Stack = 1;
            Require(api.GetAvailableCount(networkId, "(O)388", null) == 0, "Locked item available count must return 0.");
            Require(api.GetInsertCapacity(networkId, source, 1) == 0, "Locked item insert capacity must return 0.");
            Require(
                !api.TryInsertItem(networkId, source, out var remainder, out var insertCode, out var insertMessage)
                && insertCode == SvsapApiErrorCode.ItemLocked,
                $"Locked item insert must return ItemLocked, actual {insertCode}: {insertMessage}");
            Require(remainder is not null && remainder.Stack == 1, "Locked item insert failure must preserve the original item remainder.");

            Require(
                !api.TryExtractItem(networkId, "(O)388", null, 1, out var extracted, out var extractCode, out var extractMessage)
                && extractCode == SvsapApiErrorCode.ItemLocked,
                $"Locked item extract must return ItemLocked, actual {extractCode}: {extractMessage}");
            Require(extracted is null, "Locked item extract failure must not return an item.");
        }
        finally
        {
            this.networkRepository.Data.Networks.Remove(networkId);
        }
    }

    private static void TestNonHostApiGuards(ISvsapApi api)
    {
        var networkId = Guid.NewGuid();
        var source = ItemRegistry.Create("(O)388");
        source.Stack = 1;

        Require(
            !api.TryInsertItem(networkId, source, out _, out var insertCode, out var insertMessage)
            && insertCode == SvsapApiErrorCode.NotHost,
            $"Non-host insert must return NotHost, actual {insertCode}: {insertMessage}");

        Require(
            !api.TryExtractItem(networkId, "(O)388", null, 1, out _, out var extractCode, out var extractMessage)
            && extractCode == SvsapApiErrorCode.NotHost,
            $"Non-host extract must return NotHost, actual {extractCode}: {extractMessage}");
    }

    private static bool TryResolveProbeTile(string[] args, out Vector2 tile, out string source, out string message)
    {
        tile = Vector2.Zero;
        source = string.Empty;
        message = string.Empty;

        if (args.Length >= 2)
        {
            if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                message = "Usage: svsap_endpoint_probe [x y]; x/y must be integers.";
                return false;
            }

            tile = new Vector2(x, y);
            source = "args";
            return true;
        }

        if (args.Length == 1)
        {
            message = "Usage: svsap_endpoint_probe [x y]; x and y must be provided together.";
            return false;
        }

        if (!Context.IsWorldReady || Game1.player is null)
        {
            message = "No save is loaded and x y was not provided, so the facing tile cannot be inferred.";
            return false;
        }

        tile = GetFacingTile(Game1.player);
        source = "player-facing";
        return true;
    }

    private static Vector2 GetFacingTile(Farmer player)
    {
        var tile = player.Tile;
        return player.FacingDirection switch
        {
            0 => tile + new Vector2(0, -1),
            1 => tile + new Vector2(1, 0),
            2 => tile + new Vector2(0, 1),
            3 => tile + new Vector2(-1, 0),
            _ => tile
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
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
