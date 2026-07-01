using System.Text.Json;
using Microsoft.Xna.Framework;
using SVSAP.Content;
using SVSAP.Models;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class RouteSVSAPE2EService
{
    private const string RoleEnv = "STARDEW_SVSAP_ROUTEA_E2E_ROLE";
    private const string OutputDirEnv = "STARDEW_SVSAP_ROUTEA_E2E_OUTPUT";
    private const string JoinAddressEnv = "STARDEW_SVSAP_ROUTEA_E2E_JOIN";
    private const string HostFarmNameEnv = "STARDEW_SVSAP_ROUTEA_E2E_FARM";
    private const string FixtureKey = ModItemCatalog.UniqueId + "/RouteSVSAPE2EFixture";
    private const int ResponseTimeoutTicks = 3600;
    private const int StartupTimeoutTicks = 12000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly NetworkRepository repository;
    private readonly NetworkInteractionService networkInteractionService;
    private readonly StorageCellInitializer storageCellInitializer;
    private readonly string role;
    private readonly string outputDir;
    private readonly string joinAddress;
    private readonly string hostFarmName;
    private readonly Dictionary<Guid, StructuralActionResponseMessage> responses = new();

    private int hostStage;
    private int clientStage;
    private int stageTicks;
    private Guid pendingTx;
    private string pendingLabel = string.Empty;
    private RouteAFixture? fixture;
    private Item? linkTool;
    private Item? handSwitchInterferenceItem;
    private string handSwitchInterferenceLabel = string.Empty;
    private int handSwitchInterferenceStack;
    private int verifiedHandSwitches;
    private bool started;
    private bool stopped;

    public RouteSVSAPE2EService(
        IModHelper helper,
        IMonitor monitor,
        NetworkRepository repository,
        NetworkInteractionService networkInteractionService,
        StorageCellInitializer storageCellInitializer)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.repository = repository;
        this.networkInteractionService = networkInteractionService;
        this.storageCellInitializer = storageCellInitializer;
        this.role = (Environment.GetEnvironmentVariable(RoleEnv) ?? string.Empty).Trim().ToLowerInvariant();
        this.outputDir = Environment.GetEnvironmentVariable(OutputDirEnv) ?? string.Empty;
        this.joinAddress = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(JoinAddressEnv))
            ? "127.0.0.1"
            : Environment.GetEnvironmentVariable(JoinAddressEnv)!;
        this.hostFarmName = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostFarmNameEnv))
            ? "SVSAPRouteA"
            : Environment.GetEnvironmentVariable(HostFarmNameEnv)!;
    }

    public bool IsEnabled => this.role is "host" or "client";

    public void Start()
    {
        if (!this.IsEnabled || this.started)
            return;

        this.started = true;
        if (!string.IsNullOrWhiteSpace(this.outputDir))
            Directory.CreateDirectory(this.outputDir);

        this.networkInteractionService.StructuralActionResponseReceived += this.OnStructuralActionResponseReceived;
        this.helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        this.Log($"started role={this.role} output={Quote(this.outputDir)} join={Quote(this.joinAddress)}");
    }

    public void Stop()
    {
        if (!this.started || this.stopped)
            return;

        this.stopped = true;
        this.networkInteractionService.StructuralActionResponseReceived -= this.OnStructuralActionResponseReceived;
        this.helper.Events.GameLoop.UpdateTicked -= this.OnUpdateTicked;
    }

    private void OnStructuralActionResponseReceived(StructuralActionResponseMessage response)
    {
        if (response.TransactionId != Guid.Empty)
            this.responses[response.TransactionId] = response;
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (this.stopped)
            return;

        try
        {
            if (this.role == "host")
                this.TickHost();
            else if (this.role == "client")
                this.TickClient();
        }
        catch (Exception ex)
        {
            this.Fail($"exception stage={this.CurrentStage()} type={ex.GetType().Name} message={ex.Message}");
        }
    }

    private void TickHost()
    {
        if (!Context.IsWorldReady)
        {
            this.TickHostStartup();
            return;
        }

        if (!Context.IsMainPlayer)
            return;

        if (this.hostStage < 10)
        {
            this.hostStage = 10;
            this.stageTicks = 0;
        }

        if (this.hostStage == 10)
        {
            var prepared = this.PrepareHostFixture();
            this.WriteEvidence("host-ready.json", new Dictionary<string, string>
            {
                ["event"] = "host-ready",
                ["role"] = this.role,
                ["player"] = DescribePlayer(),
                ["farm"] = prepared.Location.NameOrUniqueName,
                ["coreTile"] = FormatTile(prepared.Core),
                ["driveTile"] = FormatTile(prepared.Drive),
                ["providerTile"] = FormatTile(prepared.Provider),
                ["importerTile"] = FormatTile(prepared.Importer)
            });
            this.Log($"host-ready farm={Quote(prepared.Location.NameOrUniqueName)} core={FormatTile(prepared.Core)} drive={FormatTile(prepared.Drive)} provider={FormatTile(prepared.Provider)} importer={FormatTile(prepared.Importer)}");
            this.hostStage = 20;
            this.stageTicks = 0;
            return;
        }

        if (this.hostStage == 20)
        {
            this.stageTicks++;
            if (this.outputDir.Length > 0 && File.Exists(Path.Combine(this.outputDir, "client-complete.json")))
            {
                this.WriteEvidence("host-complete.json", new Dictionary<string, string>
                {
                    ["event"] = "host-complete",
                    ["role"] = this.role,
                    ["player"] = DescribePlayer(),
                    ["networks"] = this.repository.Data.Networks.Count.ToString()
                });
                this.Log($"host-complete networks={this.repository.Data.Networks.Count}");
                this.hostStage = 30;
            }
        }
    }

    private void TickHostStartup()
    {
        this.stageTicks++;
        if (this.stageTicks > StartupTimeoutTicks)
        {
            this.Fail($"timeout waiting for host startup stage={this.hostStage}");
            return;
        }

        if (this.hostStage == 0)
        {
            if (Game1.activeClickableMenu is not TitleMenu || TitleMenu.subMenu is not null)
                return;

            this.StartHostFarmCreation();
            this.hostStage = 1;
            this.stageTicks = 0;
            return;
        }

        if (this.hostStage == 1 && TitleMenu.subMenu is CharacterCustomization menu)
        {
            this.CompleteHostFarmCreation(menu);
            this.hostStage = 2;
            this.stageTicks = 0;
        }
    }

    private void TickClient()
    {
        if (this.clientStage < 10)
        {
            this.TickClientJoin();
            return;
        }

        if (!Context.IsWorldReady || Context.IsMainPlayer)
            return;

        this.stageTicks++;
        if (this.pendingTx != Guid.Empty)
        {
            if (!this.TryConsumePendingResponse(out var response))
            {
                if (this.stageTicks > ResponseTimeoutTicks)
                    this.Fail($"timeout waiting for {this.pendingLabel} tx={this.pendingTx:N}");
                return;
            }

            if (!response.Success)
            {
                this.Fail($"structural response failed label={this.pendingLabel} message={Quote(response.Message)}");
                return;
            }

            this.Log($"response-ok label={this.pendingLabel} kind={response.Kind} message={Quote(response.Message)}");
            this.pendingTx = Guid.Empty;
            this.stageTicks = 0;
            this.AdvanceClientAfterResponse(response);
            return;
        }

        switch (this.clientStage)
        {
            case 10:
                this.fixture = this.ReadFixtureFromFarm();
                this.Log("client-world-ready fixture-found");
                this.clientStage = 20;
                break;

            case 20:
                this.linkTool = this.HoldItem("(O)" + ModItemCatalog.LinkTool, 1);
                this.BeginRequest("link-core", StructuralActionKind.LinkSelectCore, this.fixture!.Location, this.fixture.Core);
                this.SwitchToInterferenceItem("link-core");
                break;

            case 30:
                this.HoldExistingItem(this.linkTool);
                if (!this.TryReadSelectedNetworkId(out var driveNetworkId))
                {
                    this.Fail("selected network id missing before drive bind");
                    return;
                }

                this.BeginRequest("bind-drive", StructuralActionKind.LinkBindEndpoint, this.fixture!.Location, this.fixture.Drive, driveNetworkId);
                break;

            case 40:
                this.HoldItem("(O)" + ModItemCatalog.StorageCell1K, 1);
                this.BeginRequest("drive-insert-cell", StructuralActionKind.StorageDriveInteract, this.fixture!.Location, this.fixture.Drive);
                this.SwitchToInterferenceItem("drive-insert-cell");
                break;

            case 50:
                this.HoldExistingItem(null);
                this.BeginRequest("drive-eject-cell", StructuralActionKind.StorageDriveInteract, this.fixture!.Location, this.fixture.Drive);
                break;

            case 60:
                this.HoldExistingItem(this.linkTool);
                if (!this.TryReadSelectedNetworkId(out var providerNetworkId))
                {
                    this.Fail("selected network id missing before provider bind");
                    return;
                }

                this.BeginRequest("bind-provider", StructuralActionKind.LinkBindEndpoint, this.fixture!.Location, this.fixture.Provider, providerNetworkId);
                break;

            case 70:
                this.HoldExistingItem(this.CreateTestPatternItem());
                this.BeginRequest("provider-insert-pattern", StructuralActionKind.PatternProviderInteract, this.fixture!.Location, this.fixture.Provider);
                this.SwitchToInterferenceItem("provider-insert-pattern");
                break;

            case 80:
                this.HoldExistingItem(null);
                this.BeginRequest("provider-eject-pattern", StructuralActionKind.PatternProviderInteract, this.fixture!.Location, this.fixture.Provider);
                break;

            case 90:
                this.HoldExistingItem(this.linkTool);
                if (!this.TryReadSelectedNetworkId(out var importerNetworkId))
                {
                    this.Fail("selected network id missing before importer bind");
                    return;
                }

                this.BeginRequest("bind-importer", StructuralActionKind.LinkBindEndpoint, this.fixture!.Location, this.fixture.Importer, importerNetworkId);
                break;

            case 100:
                this.HoldItem("(O)390", 7);
                this.BeginRequest("importer-filter-stone", StructuralActionKind.TransferBusConfigure, this.fixture!.Location, this.fixture.Importer);
                this.SwitchToInterferenceItem("importer-filter-stone");
                break;
        }
    }

    private void TickClientJoin()
    {
        this.stageTicks++;
        var hostReady = string.IsNullOrWhiteSpace(this.outputDir) || File.Exists(Path.Combine(this.outputDir, "host-ready.json"));

        if (this.clientStage == 0)
        {
            if (!hostReady)
                return;

            if (Game1.activeClickableMenu is TitleMenu)
            {
                var multiplayer = this.helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer").GetValue();
                var client = multiplayer.InitClient(new LidgrenClient(this.joinAddress));
                TitleMenu.subMenu = new FarmhandMenu(client);
                this.Log($"client-join-menu address={Quote(this.joinAddress)}");
                this.clientStage = 1;
                this.stageTicks = 0;
            }
            return;
        }

        if (this.clientStage == 1)
        {
            var menu = this.GetFarmhandMenu();
            if (menu is not null)
            {
                var slot = menu.MenuSlots.OfType<FarmhandMenu.FarmhandSlot>().FirstOrDefault(slot => !slot.BelongsToAnotherPlayer());
                if (slot is not null)
                {
                    slot.Activate();
                    this.Log("client-farmhand-slot-activated");
                    this.clientStage = 2;
                    this.stageTicks = 0;
                    return;
                }
            }

            if (this.stageTicks > ResponseTimeoutTicks)
                this.Fail("timeout waiting for farmhand slot");
            return;
        }

        if (this.clientStage == 2)
        {
            if (Context.IsWorldReady && !Context.IsMainPlayer)
            {
                this.WriteEvidence("client-joined.json", new Dictionary<string, string>
                {
                    ["event"] = "client-joined",
                    ["role"] = this.role,
                    ["player"] = DescribePlayer()
                });
                this.Log("client-joined-world");
                this.clientStage = 10;
                this.stageTicks = 0;
                return;
            }

            if (this.stageTicks > ResponseTimeoutTicks)
                this.Fail("timeout waiting for client world ready");
        }
    }

    private void AdvanceClientAfterResponse(StructuralActionResponseMessage response)
    {
        switch (this.clientStage)
        {
            case 20:
                if (!this.VerifyHandSwitchInterference())
                    return;

                if (!this.TryReadSelectedNetworkId(out var networkId) || networkId == Guid.Empty)
                {
                    this.Fail("link-core succeeded but selected network id was not reconciled to link tool");
                    return;
                }

                this.WriteEvidence("client-link-core.json", new Dictionary<string, string>
                {
                    ["event"] = "client-link-core",
                    ["networkId"] = networkId.ToString("N")
                });
                this.clientStage = 30;
                break;

            case 30:
                this.clientStage = 40;
                break;

            case 40:
                if (!this.VerifyHandSwitchInterference())
                    return;

                if (CountInventory("(O)" + ModItemCatalog.StorageCell1K) != 0)
                {
                    this.Fail("drive insert succeeded but client still has the held storage cell");
                    return;
                }

                this.clientStage = 50;
                break;

            case 50:
                if (CountInventory("(O)" + ModItemCatalog.StorageCell1K) < 1)
                {
                    this.Fail("drive eject succeeded but client did not receive a storage cell");
                    return;
                }

                this.clientStage = 60;
                break;

            case 60:
                this.clientStage = 70;
                break;

            case 70:
                if (!this.VerifyHandSwitchInterference())
                    return;

                if (CountInventory("(O)" + ModItemCatalog.CraftingPattern) != 0)
                {
                    this.Fail("provider insert succeeded but client still has the held pattern");
                    return;
                }

                this.clientStage = 80;
                break;

            case 80:
                if (CountInventory("(O)" + ModItemCatalog.CraftingPattern) < 1)
                {
                    this.Fail("provider eject succeeded but client did not receive a pattern");
                    return;
                }

                this.clientStage = 90;
                break;

            case 90:
                this.clientStage = 100;
                break;

            case 100:
                if (!this.VerifyHandSwitchInterference())
                    return;

                if (CountInventory("(O)390") != 7)
                {
                    this.Fail("transfer bus filter configure unexpectedly consumed the held stone");
                    return;
                }

                this.WriteEvidence("client-complete.json", new Dictionary<string, string>
                {
                    ["event"] = "client-complete",
                    ["role"] = this.role,
                    ["player"] = DescribePlayer(),
                    ["lastMessage"] = response.Message,
                    ["verifiedHandSwitches"] = this.verifiedHandSwitches.ToString()
                });
                this.Log($"client-complete route-a structural e2e passed handSwitches={this.verifiedHandSwitches}");
                this.clientStage = 110;
                this.Stop();
                break;
        }
    }

    private FarmhandMenu? GetFarmhandMenu()
    {
        if (Game1.activeClickableMenu is FarmhandMenu active)
            return active;

        if (Game1.activeClickableMenu is TitleMenu && TitleMenu.subMenu is FarmhandMenu sub)
            return sub;

        return null;
    }

    private void StartHostFarmCreation()
    {
        Game1.resetPlayer();
        this.ApplyHostFarmIdentity();
        Game1.startingCabins = 1;
        Game1.cabinsSeparate = false;
        Game1.whichFarm = 0;
        Game1.options.enableServer = true;
        Game1.player.team.useSeparateWallets.Value = false;
        TitleMenu.subMenu = new CharacterCustomization(CharacterCustomization.Source.HostNewFarm, multiplayerServer: true);
        this.WriteEvidence("host-farm-create-started.json", new Dictionary<string, string>
        {
            ["event"] = "host-farm-create-started",
            ["role"] = this.role,
            ["farmName"] = this.hostFarmName
        });
        this.Log($"host-farm-create-started farm={Quote(this.hostFarmName)}");
    }

    private void CompleteHostFarmCreation(CharacterCustomization menu)
    {
        this.ApplyHostFarmIdentity();
        Game1.startingCabins = 1;
        Game1.cabinsSeparate = false;
        Game1.whichFarm = 0;
        Game1.options.enableServer = true;
        Game1.player.team.useSeparateWallets.Value = false;

        this.helper.Reflection.GetField<TextBox>(menu, "nameBox").GetValue().Text = "SVSAPHost";
        this.helper.Reflection.GetField<TextBox>(menu, "farmnameBox").GetValue().Text = this.hostFarmName;
        this.helper.Reflection.GetField<TextBox>(menu, "favThingBox").GetValue().Text = "SVSAP";
        this.helper.Reflection.GetField<bool>(menu, "skipIntro").SetValue(true);

        var ok = menu.okButton.bounds.Center;
        menu.receiveLeftClick(ok.X, ok.Y);
        this.WriteEvidence("host-farm-create-submitted.json", new Dictionary<string, string>
        {
            ["event"] = "host-farm-create-submitted",
            ["role"] = this.role,
            ["farmName"] = this.hostFarmName
        });
        this.Log($"host-farm-create-submitted farm={Quote(this.hostFarmName)}");
    }

    private void ApplyHostFarmIdentity()
    {
        Game1.player.Name = "SVSAPHost";
        Game1.player.displayName = Game1.player.Name;
        Game1.player.farmName.Value = this.hostFarmName;
        Game1.player.favoriteThing.Value = "SVSAP";
        Game1.player.isCustomized.Value = true;
    }

    private void BeginRequest(
        string label,
        StructuralActionKind kind,
        GameLocation location,
        Vector2 tile,
        Guid selectedNetworkId = default)
    {
        this.pendingLabel = label;
        this.pendingTx = this.networkInteractionService.SendStructuralActionForRouteSVSAPE(kind, location, tile, selectedNetworkId);
        this.stageTicks = 0;
        this.Log($"request-sent label={label} kind={kind} tile={FormatTile(tile)} tx={this.pendingTx:N}");
    }

    private bool TryConsumePendingResponse(out StructuralActionResponseMessage response)
    {
        if (this.responses.Remove(this.pendingTx, out response!))
            return true;

        response = null!;
        return false;
    }

    private RouteAFixture PrepareHostFixture()
    {
        var location = Game1.getLocationFromName("Farm") ?? Game1.currentLocation;
        if (location is null)
            throw new InvalidOperationException("Farm location is not available.");

        var fixture = this.FindFixtureTiles(location);
        this.EnsureFixtureObject(location, fixture.Core, ModItemCatalog.NetworkCore, "core");
        this.EnsureFixtureObject(location, fixture.Drive, ModItemCatalog.StorageDrive, "drive");
        this.EnsureFixtureObject(location, fixture.Provider, ModItemCatalog.PatternProvider, "provider");
        this.EnsureFixtureObject(location, fixture.Importer, ModItemCatalog.Importer, "importer");
        this.fixture = fixture;
        return fixture;
    }

    private RouteAFixture ReadFixtureFromFarm()
    {
        var location = Game1.getLocationFromName("Farm") ?? Game1.currentLocation;
        if (location is null)
            throw new InvalidOperationException("Farm location is not available on client.");

        var fixture = this.TryReadFixtureFromHostReady(location) ?? this.FindFixtureTiles(location);
        this.RequireFixtureObject(location, fixture.Core, ModItemCatalog.NetworkCore);
        this.RequireFixtureObject(location, fixture.Drive, ModItemCatalog.StorageDrive);
        this.RequireFixtureObject(location, fixture.Provider, ModItemCatalog.PatternProvider);
        this.RequireFixtureObject(location, fixture.Importer, ModItemCatalog.Importer);
        return fixture;
    }

    private RouteAFixture? TryReadFixtureFromHostReady(GameLocation location)
    {
        if (string.IsNullOrWhiteSpace(this.outputDir))
            return null;

        var path = Path.Combine(this.outputDir, "host-ready.json");
        if (!File.Exists(path))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new RouteAFixture(
            location,
            ParseTile(document.RootElement.GetProperty("coreTile").GetString()),
            ParseTile(document.RootElement.GetProperty("driveTile").GetString()),
            ParseTile(document.RootElement.GetProperty("providerTile").GetString()),
            ParseTile(document.RootElement.GetProperty("importerTile").GetString()));
    }

    private RouteAFixture FindFixtureTiles(GameLocation location)
    {
        for (var y = 5; y < 60; y++)
        {
            for (var x = 5; x < 80; x++)
            {
                var fixture = new RouteAFixture(
                    location,
                    new Vector2(x, y),
                    new Vector2(x + 1, y),
                    new Vector2(x + 2, y),
                    new Vector2(x + 3, y));

                if (this.CanUseFixtureTile(location, fixture.Core, ModItemCatalog.NetworkCore)
                    && this.CanUseFixtureTile(location, fixture.Drive, ModItemCatalog.StorageDrive)
                    && this.CanUseFixtureTile(location, fixture.Provider, ModItemCatalog.PatternProvider)
                    && this.CanUseFixtureTile(location, fixture.Importer, ModItemCatalog.Importer))
                {
                    return fixture;
                }
            }
        }

        throw new InvalidOperationException("Could not find four contiguous clear farm object tiles for SVSAP E2E fixture.");
    }

    private bool CanUseFixtureTile(GameLocation location, Vector2 tile, string itemId)
    {
        if (!location.objects.TryGetValue(tile, out var obj))
            return true;

        return obj.QualifiedItemId == "(BC)" + itemId && obj.modData.GetValueOrDefault(FixtureKey) is not null;
    }

    private SObject EnsureFixtureObject(GameLocation location, Vector2 tile, string itemId, string label)
    {
        if (location.objects.TryGetValue(tile, out var existing))
        {
            if (existing.QualifiedItemId == "(BC)" + itemId)
            {
                existing.modData[FixtureKey] = label;
                return existing;
            }

            throw new InvalidOperationException($"Fixture tile {FormatTile(tile)} is occupied by {existing.QualifiedItemId}.");
        }

        if (ItemRegistry.Create("(BC)" + itemId) is not SObject obj)
            throw new InvalidOperationException($"Could not create fixture object {(BC(itemId))}.");

        obj.TileLocation = tile;
        obj.modData[FixtureKey] = label;
        location.objects[tile] = obj;
        return obj;
    }

    private void RequireFixtureObject(GameLocation location, Vector2 tile, string itemId)
    {
        if (!location.objects.TryGetValue(tile, out var obj) || obj.QualifiedItemId != "(BC)" + itemId)
            throw new InvalidOperationException($"Missing fixture object {(BC(itemId))} at {FormatTile(tile)}.");
    }

    private Item HoldItem(string qualifiedItemId, int stack)
    {
        var item = ItemRegistry.Create(qualifiedItemId);
        item.Stack = stack;
        this.storageCellInitializer.EnsureCellData(item);
        this.HoldExistingItem(item);
        return item;
    }

    private void HoldExistingItem(Item? item)
    {
        Game1.player.Items[0] = item;
        Game1.player.CurrentToolIndex = 0;
    }

    private void SwitchToInterferenceItem(string label)
    {
        var item = ItemRegistry.Create("(O)388");
        item.Stack = 4;
        Game1.player.Items[1] = item;
        Game1.player.CurrentToolIndex = 1;
        this.handSwitchInterferenceItem = item;
        this.handSwitchInterferenceLabel = label;
        this.handSwitchInterferenceStack = item.Stack;
        this.Log($"client-hand-switch label={label} current={item.QualifiedItemId} stack={item.Stack}");
    }

    private bool VerifyHandSwitchInterference()
    {
        if (this.handSwitchInterferenceItem is null)
            return true;

        var item = this.handSwitchInterferenceItem;
        var label = this.handSwitchInterferenceLabel;
        if (Game1.player.Items.IndexOf(item) < 0)
        {
            this.Fail($"hand-switch interference item missing label={label}");
            return false;
        }

        if (item.Stack != this.handSwitchInterferenceStack)
        {
            this.Fail($"hand-switch interference item stack changed label={label} expected={this.handSwitchInterferenceStack} actual={item.Stack}");
            return false;
        }

        if (item.modData.ContainsKey(NetworkInteractionService.SelectedNetworkIdKey))
        {
            this.Fail($"hand-switch interference item received selected network label={label}");
            return false;
        }

        this.verifiedHandSwitches++;
        this.Log($"client-hand-switch-verified label={label} stack={item.Stack}");
        this.handSwitchInterferenceItem = null;
        this.handSwitchInterferenceLabel = string.Empty;
        this.handSwitchInterferenceStack = 0;
        return true;
    }

    private Item CreateTestPatternItem()
    {
        var pattern = new PatternData
        {
            Kind = PatternKind.Crafting,
            DisplayName = "RouteA E2E pattern",
            Inputs =
            {
                new NetworkItemRequest { QualifiedItemId = "(O)388", Count = 1 }
            },
            Outputs =
            {
                new NetworkItemRequest { QualifiedItemId = "(O)390", Count = 1 }
            }
        };

        return PatternCodec.CreatePatternItem(pattern);
    }

    private bool TryReadSelectedNetworkId(out Guid networkId)
    {
        networkId = Guid.Empty;
        var item = this.linkTool ?? Game1.player.CurrentItem;
        return item is not null
            && Guid.TryParse(item.modData.GetValueOrDefault(NetworkInteractionService.SelectedNetworkIdKey), out networkId);
    }

    private static int CountInventory(string qualifiedItemId)
    {
        return Game1.player.Items
            .Where(item => item is not null && item.QualifiedItemId == qualifiedItemId)
            .Sum(item => item!.Stack);
    }

    private void WriteEvidence(string fileName, Dictionary<string, string> fields)
    {
        if (string.IsNullOrWhiteSpace(this.outputDir))
            return;

        fields["createdUtc"] = DateTime.UtcNow.ToString("O");
        fields["stage"] = this.CurrentStage();
        var path = Path.Combine(this.outputDir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(fields, JsonOptions));
    }

    private void Log(string message)
    {
        this.monitor.Log($"SVSAP_E2E {message}", LogLevel.Info);
    }

    private void Fail(string reason)
    {
        if (this.stopped)
            return;

        this.monitor.Log($"SVSAP_E2E_FAIL {reason}", LogLevel.Error);
        this.WriteEvidence($"{this.role}-fail.json", new Dictionary<string, string>
        {
            ["event"] = "fail",
            ["role"] = this.role,
            ["reason"] = reason,
            ["player"] = DescribePlayer()
        });
        this.Stop();
    }

    private string CurrentStage()
    {
        return this.role == "host" ? this.hostStage.ToString() : this.clientStage.ToString();
    }

    private static string DescribePlayer()
    {
        return Game1.player is null
            ? "none"
            : $"{Quote(Game1.player.Name)}#{Game1.player.UniqueMultiplayerID}";
    }

    private static string FormatTile(Vector2 tile)
    {
        return $"({tile.X:0},{tile.Y:0})";
    }

    private static Vector2 ParseTile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '(' || value[^1] != ')')
            throw new InvalidOperationException($"Invalid fixture tile value: {Quote(value)}");

        var parts = value[1..^1].Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[0], out var x) || !float.TryParse(parts[1], out var y))
            throw new InvalidOperationException($"Invalid fixture tile value: {Quote(value)}");

        return new Vector2(x, y);
    }

    private static string BC(string itemId)
    {
        return "(BC)" + itemId;
    }

    private static string Quote(string? value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private sealed record RouteAFixture(
        GameLocation Location,
        Vector2 Core,
        Vector2 Drive,
        Vector2 Provider,
        Vector2 Importer);
}
