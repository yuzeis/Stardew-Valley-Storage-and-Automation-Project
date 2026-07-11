#if DEBUG
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using SVSAP.Content;
using SVSAP.Models;
using SVSAP.UI;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

/// <summary>Debug-only visual gate which renders every SVSAP menu and saves the actual backbuffer.</summary>
internal sealed class GuiScreenshotE2EService
{
    internal const string EnabledEnv = "STARDEW_SVSAP_GUI_CAPTURE";
    internal const string OutputDirEnv = "STARDEW_SVSAP_GUI_CAPTURE_OUTPUT";
    internal const string SvsapCompleteFileName = "svsap-gui-capture-complete.json";

    private const string FullMatrixEnabledEnv = "STARDEW_SVSAPME_FULL_E2E";
    private const string FullMatrixOutputDirEnv = "STARDEW_SVSAPME_FULL_E2E_OUTPUT";
    private const string FullMatrixCompleteFileName = "full-matrix-complete.json";
    private const int StartupDelayTicks = 180;
    private const int RenderFramesBeforeCapture = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly InventoryScanner scanner;
    private readonly InventoryTransactionService transactionService;
    private readonly CraftingRecipeService craftingRecipeService;
    private readonly PatternEncodingService patternEncodingService;
    private readonly StorageDriveService storageDriveService;
    private readonly TransferBusService transferBusService;
    private readonly PatternProviderService patternProviderService;
    private readonly PatternExecutionService patternExecutionService;
    private readonly string outputDir;
    private readonly List<GuiCaptureResult> results = new();

    private List<GuiCaptureCase>? cases;
    private IClickableMenu? currentMenu;
    private int currentIndex;
    private int worldReadyTicks;
    private int renderedFrames;
    private bool currentCaptured;
    private bool started;
    private bool stopped;

    public GuiScreenshotE2EService(
        IModHelper helper,
        IMonitor monitor,
        InventoryScanner scanner,
        InventoryTransactionService transactionService,
        CraftingRecipeService craftingRecipeService,
        PatternEncodingService patternEncodingService,
        StorageDriveService storageDriveService,
        TransferBusService transferBusService,
        PatternProviderService patternProviderService,
        PatternExecutionService patternExecutionService)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.scanner = scanner;
        this.transactionService = transactionService;
        this.craftingRecipeService = craftingRecipeService;
        this.patternEncodingService = patternEncodingService;
        this.storageDriveService = storageDriveService;
        this.transferBusService = transferBusService;
        this.patternProviderService = patternProviderService;
        this.patternExecutionService = patternExecutionService;
        this.outputDir = Environment.GetEnvironmentVariable(OutputDirEnv) ?? string.Empty;
    }

    private bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnabledEnv), "1", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(this.outputDir);

    public void Start()
    {
        if (!this.IsEnabled || this.started)
            return;

        this.started = true;
        Directory.CreateDirectory(this.GetScreenshotDirectory());
        this.helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        this.monitor.Log($"SVSAP_GUI_CAPTURE started output=\"{this.outputDir}\"", LogLevel.Info);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (this.stopped || !Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        this.worldReadyTicks++;
        if (this.worldReadyTicks < StartupDelayTicks)
            return;

        var fullMatrixOutputDir = Environment.GetEnvironmentVariable(FullMatrixOutputDirEnv);
        if (string.Equals(Environment.GetEnvironmentVariable(FullMatrixEnabledEnv), "1", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(fullMatrixOutputDir)
            && !File.Exists(Path.Combine(fullMatrixOutputDir, FullMatrixCompleteFileName)))
        {
            return;
        }

        if (this.cases is null)
        {
            try
            {
                this.cases = this.CreateCases();
                this.WriteProgress();
            }
            catch (Exception ex)
            {
                this.results.Add(GuiCaptureResult.Failed("setup", ex));
                this.Finish();
                return;
            }
        }

        if (this.currentMenu is not null && !this.currentCaptured)
        {
            this.renderedFrames++;
            if (this.renderedFrames < RenderFramesBeforeCapture)
                return;

            var captureCase = this.cases[this.currentIndex];
            try
            {
                this.results.Add(this.Capture(captureCase));
            }
            catch (Exception ex)
            {
                this.results.Add(GuiCaptureResult.Failed(captureCase.Name, ex));
            }

            this.currentCaptured = true;
            this.WriteProgress();
            return;
        }

        if (this.currentCaptured)
        {
            this.CloseCurrentMenu();
            this.currentIndex++;
            this.currentCaptured = false;
            this.renderedFrames = 0;
        }

        if (this.currentIndex >= this.cases.Count)
        {
            this.Finish();
            return;
        }

        if (this.currentMenu is not null || Game1.activeClickableMenu is not null)
            return;

        var currentCase = this.cases[this.currentIndex];
        try
        {
            this.currentMenu = currentCase.CreateMenu();
            Game1.activeClickableMenu = this.currentMenu;
            this.renderedFrames = 0;
            this.monitor.Log($"SVSAP_GUI_CAPTURE showing {currentCase.Name}", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            this.results.Add(GuiCaptureResult.Failed(currentCase.Name, ex));
            this.currentIndex++;
            this.WriteProgress();
        }
    }

    private GuiCaptureResult Capture(GuiCaptureCase captureCase)
    {
        var name = captureCase.Name;
        var graphicsDevice = Game1.graphics.GraphicsDevice;
        var previousTargets = graphicsDevice.GetRenderTargets();
        var width = Math.Max(1, Game1.uiViewport.Width);
        var height = Math.Max(1, Game1.uiViewport.Height);
        var pixels = new Color[checked(width * height)];
        using var target = new RenderTarget2D(
            graphicsDevice,
            width,
            height,
            false,
            SurfaceFormat.Color,
            DepthFormat.None,
            0,
            RenderTargetUsage.PreserveContents);
        using var spriteBatch = new SpriteBatch(graphicsDevice);
        graphicsDevice.SetRenderTarget(target);
        graphicsDevice.Clear(Color.Black);
        SVSAPMenuWidgets.ResetSlotGeometryDiagnostics();
        var batchBegun = false;
        try
        {
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            batchBegun = true;
            this.currentMenu!.draw(spriteBatch);
            spriteBatch.End();
            batchBegun = false;
        }
        finally
        {
            if (batchBegun)
            {
                try
                {
                    spriteBatch.End();
                }
                catch
                {
                    // Preserve the original render exception while restoring the graphics state.
                }
            }

            graphicsDevice.SetRenderTargets(previousTargets);
        }
        target.GetData(pixels);

        var topDownPixels = pixels;
        var sampledColors = new HashSet<uint>();
        var stride = Math.Max(1, topDownPixels.Length / 8192);
        for (var i = 0; i < topDownPixels.Length; i += stride)
            sampledColors.Add(topDownPixels[i].PackedValue);

        var path = Path.Combine(this.GetScreenshotDirectory(), name + ".png");
        using (var texture = new Texture2D(graphicsDevice, width, height, false, SurfaceFormat.Color))
        {
            texture.SetData(topDownPixels);
            using var stream = File.Create(path);
            texture.SaveAsPng(stream, width, height);
        }

        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var panelCoveragePermille = CalculatePanelCoveragePermille(topDownPixels, width, height, this.currentMenu!);
        var blackPixelPermille = CalculatePanelBlackPixelPermille(topDownPixels, width, height, this.currentMenu!);
        var slotGeometryErrors = SVSAPMenuWidgets.GetSlotGeometryViolations();
        var qualityOverlayCount = SVSAPMenuWidgets.GetQualityOverlayDrawCount();
        var pass = info.Length > 1024
            && sampledColors.Count >= 8
            && panelCoveragePermille >= 550
            && blackPixelPermille <= 50
            && slotGeometryErrors.Count == 0
            && qualityOverlayCount >= captureCase.MinimumQualityOverlays;
        this.monitor.Log(
            $"SVSAP_GUI_CAPTURE {(pass ? "PASS" : "FAIL")} {name} {width}x{height} bytes={info.Length} sampledColors={sampledColors.Count} panelCoverage={panelCoveragePermille}/1000 blackPixels={blackPixelPermille}/1000 slotGeometryErrors={slotGeometryErrors.Count} qualityOverlays={qualityOverlayCount}/{captureCase.MinimumQualityOverlays}",
            pass ? LogLevel.Info : LogLevel.Error);
        return new GuiCaptureResult(
            name,
            pass,
            path,
            width,
            height,
            info.Length,
            sampledColors.Count,
            panelCoveragePermille,
            blackPixelPermille,
            slotGeometryErrors.Count,
            slotGeometryErrors,
            qualityOverlayCount,
            captureCase.MinimumQualityOverlays,
            hash,
            pass ? string.Empty : "Screenshot was blank, missing its menu panel, contained black render holes, was too small, omitted required quality stars, or drew item/count/quality content outside its slot.");
    }

    private List<GuiCaptureCase> CreateCases()
    {
        this.ValidateStorageScopeAndTransferRuntime(Game1.currentLocation);
        var network = this.CreateRepresentativeNetwork();
        var drive = CreateBigCraftable(ModItemCatalog.StorageDrive);
        var importer = CreateBigCraftable(ModItemCatalog.Importer);
        var exporter = CreateBigCraftable(ModItemCatalog.Exporter);
        var provider = CreateBigCraftable(ModItemCatalog.PatternProvider);
        var location = Game1.currentLocation;
        var driveTile = this.PlaceFixtureObject(location, drive);
        this.PlaceFixtureObject(location, importer);
        this.PlaceFixtureObject(location, exporter);
        this.PlaceFixtureObject(location, provider);
        this.PopulateCraftingMonitor(network);

        var parent = new SVSAPStatusMenu(
            "GUI Capture Parent",
            () => new[] { "The confirmation menu must restore its parent without side effects." });

        return new List<GuiCaptureCase>
        {
            new("01-network-terminal-local", () => new NetworkTerminalMenu(network, this.scanner, this.transactionService)),
            new("02-crafting-terminal-local", () => new CraftingTerminalMenu(network, this.scanner, this.craftingRecipeService)),
            new("03-storage-drive-local", () => new StorageDriveMenu(drive, location, driveTile, this.storageDriveService)),
            new("04-transfer-bus-local-importer", () => new TransferBusMenu(importer, this.transferBusService)),
            new("05-transfer-bus-local-exporter", () => new TransferBusMenu(exporter, this.transferBusService)),
            new("06-pattern-terminal-local", () => new PatternTerminalMenu(this.patternEncodingService)),
            new("07-pattern-provider-local", () => new PatternProviderMenu(provider, this.patternProviderService)),
            new("08-crafting-monitor-local", () => new CraftingMonitorMenu(network, this.patternExecutionService, CreateRepresentativePattern(), ItemRegistry.Create("(O)348"))),
            new("09-crafting-confirmation", () => new CraftingConfirmationMenu(parent, "Iridium component x10", CreateConfirmationSummary(), CreateConfirmationIngredients(), () => { })),
            new("10-svsap-status", () => new SVSAPStatusMenu("SVSAP Network Diagnostics", CreateStatusLines, CreateStatusActions())),
            new("11-network-terminal-remote", () => new RemoteNetworkTerminalMenu(CreateRemoteTerminalSnapshot(), (_, _) => true, (_, _, _, _) => true), MinimumQualityOverlays: 2),
            new("12-crafting-terminal-remote", () => new RemoteCraftingTerminalMenu(CreateRemoteCraftingSnapshot(), _ => true, (_, _) => true)),
            new("13-storage-drive-remote", () => new RemoteStorageDriveMenu(CreateRemoteStorageDriveSnapshot(), (_, _, _) => true, () => true)),
            new("14-transfer-bus-remote-importer", () => new RemoteTransferBusMenu(CreateRemoteTransferSnapshot(false), (_, _, _, _, _) => true, () => true)),
            new("15-transfer-bus-remote-exporter", () => new RemoteTransferBusMenu(CreateRemoteTransferSnapshot(true), (_, _, _, _, _) => true, () => true)),
            new("16-crafting-monitor-remote", () => new RemoteCraftingMonitorMenu(CreateRemoteCraftingMonitorSnapshot(), _ => true, (_, _) => true)),
            new("17-pattern-provider-remote", () => new RemotePatternProviderMenu(CreateRemotePatternProviderSnapshot(), (_, _, _, _) => true, () => true))
        };
    }

    private NetworkData CreateRepresentativeNetwork()
    {
        var location = Game1.currentLocation;
        var clearOrigin = this.FindClearFixtureBlock(location, 3, 3);
        var interfaceTile = clearOrigin + new Vector2(1, 1);
        var chestTile = clearOrigin + new Vector2(2, 1);
        var storageInterface = CreateBigCraftable(ModItemCatalog.StorageInterface);
        storageInterface.TileLocation = interfaceTile;
        location.objects[interfaceTile] = storageInterface;
        var chest = new Chest(Enumerable.Repeat<Item?>(null, 36).ToList(), chestTile, false, 0, false);
        chest.Items[0] = CreateStack("(O)388", 999);
        chest.Items[1] = CreateStack("(O)388", 999);
        chest.Items[2] = CreateStack("(O)390", 999);
        chest.Items[3] = CreateStack("(O)382", 500);
        chest.Items[4] = CreateStack("(O)335", 128);
        chest.Items[5] = CreateStack("(O)454", 64);
        location.objects[chestTile] = chest;

        var endpointId = Guid.NewGuid();
        return new NetworkData
        {
            NetworkId = Guid.NewGuid(),
            Name = "GUI Capture Network",
            Endpoints =
            {
                new NetworkEndpoint
                {
                    EndpointId = endpointId,
                    LocationName = location.NameOrUniqueName,
                    TileX = interfaceTile.X,
                    TileY = interfaceTile.Y,
                    Type = EndpointType.StorageInterface,
                    Active = true
                }
            }
        };
    }

    private void ValidateStorageScopeAndTransferRuntime(GameLocation location)
    {
        var origin = this.FindClearFixtureBlock(location, 11, 4);
        var interfaceTile = origin + new Vector2(1, 1);
        var storageChestTile = origin + new Vector2(2, 1);
        var importerTile = origin + new Vector2(4, 1);
        var importerSourceTile = origin + new Vector2(5, 1);
        var exporterTile = origin + new Vector2(4, 2);
        var exporterTargetTile = origin + new Vector2(5, 2);
        var legacyDirectChestTile = origin + new Vector2(10, 3);

        var storageInterface = CreateBigCraftable(ModItemCatalog.StorageInterface);
        storageInterface.TileLocation = interfaceTile;
        location.objects[interfaceTile] = storageInterface;
        var storageChest = new Chest(Enumerable.Repeat<Item?>(null, 36).ToList(), storageChestTile, false, 0, false);
        storageChest.Items[0] = CreateStack("(O)388", 11);
        location.objects[storageChestTile] = storageChest;

        var legacyDirectChest = new Chest(Enumerable.Repeat<Item?>(null, 36).ToList(), legacyDirectChestTile, false, 0, false);
        legacyDirectChest.Items[0] = CreateStack("(O)337", 777);
        location.objects[legacyDirectChestTile] = legacyDirectChest;

        var importer = CreateBigCraftable(ModItemCatalog.Importer);
        importer.TileLocation = importerTile;
        location.objects[importerTile] = importer;
        var importerSource = new Chest(Enumerable.Repeat<Item?>(null, 36).ToList(), importerSourceTile, false, 0, false);
        importerSource.Items[0] = CreateStack("(O)390", 20);
        importerSource.Items[1] = CreateStack("(O)388", 7);
        location.objects[importerSourceTile] = importerSource;

        var exporter = CreateBigCraftable(ModItemCatalog.Exporter);
        exporter.TileLocation = exporterTile;
        location.objects[exporterTile] = exporter;
        var exporterTarget = new Chest(Enumerable.Repeat<Item?>(null, 36).ToList(), exporterTargetTile, false, 0, false);
        location.objects[exporterTargetTile] = exporterTarget;

        var network = new NetworkData { NetworkId = Guid.NewGuid(), Name = "SVSAP runtime preflight" };
        var interfaceEndpoint = new NetworkEndpoint
        {
            EndpointId = Guid.NewGuid(),
            LocationName = location.NameOrUniqueName,
            TileX = interfaceTile.X,
            TileY = interfaceTile.Y,
            Type = EndpointType.StorageInterface,
            Active = true
        };
        var legacyChestEndpoint = new NetworkEndpoint
        {
            EndpointId = Guid.NewGuid(),
            LocationName = location.NameOrUniqueName,
            TileX = legacyDirectChestTile.X,
            TileY = legacyDirectChestTile.Y,
            Type = EndpointType.Chest,
            Active = true
        };
        var importerEndpoint = new NetworkEndpoint
        {
            EndpointId = Guid.NewGuid(),
            LocationName = location.NameOrUniqueName,
            TileX = importerTile.X,
            TileY = importerTile.Y,
            Type = EndpointType.Importer,
            Active = true
        };
        var exporterEndpoint = new NetworkEndpoint
        {
            EndpointId = Guid.NewGuid(),
            LocationName = location.NameOrUniqueName,
            TileX = exporterTile.X,
            TileY = exporterTile.Y,
            Type = EndpointType.Exporter,
            Active = true
        };
        network.Endpoints.AddRange(new[] { interfaceEndpoint, legacyChestEndpoint, importerEndpoint, exporterEndpoint });

        var importerFilterConfigured = this.transferBusService.TrySetFilterSlot(importer, 0, "(O)390", out var importerFilterMessage);
        var importerDirectionConfigured = this.transferBusService.TrySetFacingDirection(importer, 1, out var importerDirectionMessage);
        if (!importerFilterConfigured || !importerDirectionConfigured)
        {
            throw new InvalidOperationException($"Could not configure importer preflight: {importerFilterMessage}; {importerDirectionMessage}");
        }

        var before = this.scanner.Scan(network);
        if (before.Entries.All(entry => entry.Key.QualifiedItemId != "(O)388")
            || before.Entries.Any(entry => entry.Key.QualifiedItemId == "(O)337"))
        {
            throw new InvalidOperationException("Storage scope preflight failed: scan must include only chests adjacent to a Storage Interface and ignore direct legacy chest endpoints.");
        }

        if (!this.transferBusService.RunEndpointForE2E(network, importerEndpoint))
            throw new InvalidOperationException("Importer runtime preflight failed to move a filtered item from the configured right-side chest.");
        if (CountChestItem(importerSource, "(O)390") != 0 || CountChestItem(importerSource, "(O)388") != 7)
            throw new InvalidOperationException("Importer runtime preflight violated its direction/filter contract.");

        var exporterFilterConfigured = this.transferBusService.TrySetFilterSlot(exporter, 0, "(O)390", out var exporterFilterMessage);
        var exporterDirectionConfigured = this.transferBusService.TrySetFacingDirection(exporter, 1, out var exporterDirectionMessage);
        if (!exporterFilterConfigured || !exporterDirectionConfigured)
        {
            throw new InvalidOperationException($"Could not configure exporter preflight: {exporterFilterMessage}; {exporterDirectionMessage}");
        }

        if (!this.transferBusService.RunEndpointForE2E(network, exporterEndpoint))
            throw new InvalidOperationException("Exporter runtime preflight failed to move a filtered network item into the configured right-side chest.");
        var exported = CountChestItem(exporterTarget, "(O)390");
        if (exported != 20)
            throw new InvalidOperationException($"Exporter runtime preflight moved {exported} items instead of 20.");

        var after = this.scanner.Scan(network);
        if (after.Entries.Any(entry => entry.Key.QualifiedItemId == "(O)337"))
            throw new InvalidOperationException("Legacy direct chest content entered the network after transfer routing.");

        this.WriteJson("svsap-storage-transfer-preflight.json", new
        {
            pass = true,
            storageInterfaceScope = "adjacent-only",
            legacyDirectChestVisible = false,
            importerMoved = 20,
            exporterMoved = exported,
            direction = "right",
            filter = "(O)390"
        });
    }

    private void PopulateCraftingMonitor(NetworkData network)
    {
        var pattern = CreateRepresentativePattern();
        network.Jobs.Add(new CraftingJob
        {
            JobId = Guid.NewGuid(),
            TargetQualifiedItemId = "(O)335",
            RequestedCount = 100,
            CompletedCount = 42,
            State = CraftingJobState.Running,
            Pattern = pattern,
            NodeCount = 3,
            StatusMessage = "Processing on molecular assembler"
        });
        var pipelineId = Guid.NewGuid();
        network.ProductionPipelines[pipelineId] = new ProductionPipelineData
        {
            PipelineId = pipelineId,
            Enabled = true,
            Priority = 10,
            Pattern = pattern,
            ItemsPerCycle = 8,
            TargetKeep = 256,
            StatusMessage = "Online"
        };
    }

    private static PatternData CreateRepresentativePattern()
    {
        return new PatternData
        {
            Kind = PatternKind.Processing,
            DisplayName = "Iron processing pattern",
            MachineQualifiedItemId = "(BC)13",
            ProcessingMinutes = 120,
            Inputs = { new NetworkItemRequest { QualifiedItemId = "(O)380", Count = 5 } },
            Outputs = { new NetworkItemRequest { QualifiedItemId = "(O)335", Count = 1 } }
        };
    }

    private static IReadOnlyList<string> CreateConfirmationSummary()
    {
        return new[]
        {
            "Output: Iridium component x10",
            "Estimated energy: 12.50 kWh",
            "Estimated duration: 2 days 4 hours"
        };
    }

    private static IReadOnlyList<CraftingIngredientAvailability> CreateConfirmationIngredients()
    {
        return new[]
        {
            new CraftingIngredientAvailability
            {
                Request = new NetworkItemRequest { QualifiedItemId = "(O)337", Count = 10 },
                AvailableCount = 40,
                RequiredCount = 10
            },
            new CraftingIngredientAvailability
            {
                Request = new NetworkItemRequest { QualifiedItemId = "(O)74", Count = 2 },
                AvailableCount = 1,
                RequiredCount = 2
            }
        };
    }

    private static IReadOnlyList<string> CreateStatusLines()
    {
        return new[]
        {
            "Network: GUI Capture Network",
            "Status: online",
            "Storage: 54,200 / 5,120,000 bytes",
            "Types: 6 / 315",
            "Endpoints: 12 active, 1 offline",
            "Last transaction: committed"
        };
    }

    private static IEnumerable<SVSAPMenuAction> CreateStatusActions()
    {
        return new[]
        {
            new SVSAPMenuAction("Refresh", () => "Refreshed"),
            new SVSAPMenuAction("Diagnostics", () => "Diagnostics ready"),
            new SVSAPMenuAction("Rebuild", () => "Rebuild queued", () => false)
        };
    }

    private static TerminalSnapshotResponseMessage CreateRemoteTerminalSnapshot()
    {
        var sessionId = Guid.NewGuid();
        return new TerminalSnapshotResponseMessage
        {
            MenuSessionId = sessionId,
            RequestSequence = 1,
            NetworkId = Guid.NewGuid(),
            EndpointId = Guid.NewGuid(),
            Success = true,
            NetworkName = "Remote GUI Network",
            SourceCount = 3,
            TotalEntryCount = 6,
            EntryLimit = 48,
            StorageSummary = new NetworkStorageSummary
            {
                CellCount = 5,
                CapacityUsed = 54_200,
                CapacityMax = 5_120_000,
                TypeSlotsUsed = 6,
                TypeSlotsMax = 315
            },
            Entries =
            {
                CreateRemoteEntry("(O)388", 57, 0),
                CreateRemoteEntry("(O)390", 999, 0),
                CreateRemoteEntry("(O)382", 1_500, 0),
                CreateRemoteEntry("(O)335", 15_400, 0),
                CreateRemoteEntry("(O)337", 2_400_000, 2),
                CreateRemoteEntry("(O)454", 18, 4)
            }
        };
    }

    private static RemoteInventoryEntryMessage CreateRemoteEntry(string qualifiedItemId, long count, int quality)
    {
        var item = ItemRegistry.Create(qualifiedItemId);
        if (item is SObject obj)
            obj.Quality = quality;
        return new RemoteInventoryEntryMessage
        {
            Key = new ItemKey { QualifiedItemId = qualifiedItemId, Quality = quality },
            QualifiedItemId = qualifiedItemId,
            SerializedItemPrototype = SerializedItemCodec.SerializePrototype(item.getOne()),
            Name = item.Name,
            DisplayName = item.DisplayName,
            Category = TerminalInventoryFilters.GetCategory(item),
            Quality = quality,
            SalePrice = item.salePrice(),
            TotalCount = count,
            AvailableCount = count,
            StackCount = 1
        };
    }

    private static CraftingSnapshotResponseMessage CreateRemoteCraftingSnapshot()
    {
        var recipe = new RemoteCraftingRecipeMessage
        {
            Name = "gui-capture-iridium",
            DisplayName = ItemRegistry.GetData("(O)337").DisplayName,
            OutputQualifiedItemId = "(O)337",
            OutputSerializedItemPrototype = SerializedItemCodec.SerializePrototype(ItemRegistry.Create("(O)337")),
            OutputCount = 5,
            CanCraft = false,
            Ingredients = CreateConfirmationIngredients().ToList(),
            IngredientLines = { "Gold Bar: 40 / 10", "Iridium Ore: 1 / 2" },
            MissingLines = { "Iridium Ore: 1 / 2" },
            MissingIngredients =
            {
                new CraftingMissingIngredient
                {
                    Request = new NetworkItemRequest { QualifiedItemId = "(O)74", Count = 2 },
                    AvailableCount = 1,
                    RequiredCount = 2
                }
            }
        };
        return new CraftingSnapshotResponseMessage
        {
            MenuSessionId = Guid.NewGuid(),
            RequestSequence = 1,
            NetworkId = Guid.NewGuid(),
            EndpointId = Guid.NewGuid(),
            Success = true,
            NetworkName = "Remote Crafting Network",
            NetworkItemTypes = 48,
            Batches = 10,
            Recipes = { recipe }
        };
    }

    private static StructuralSnapshotResponseMessage CreateRemoteStorageDriveSnapshot()
    {
        var slots = Enumerable.Range(0, 10)
            .Select(index => new RemoteStorageDriveSlotMessage
            {
                SlotIndex = index,
                Occupied = index < 4,
                QualifiedItemId = index < 4 ? "(O)" + ModItemCatalog.StorageCell64K : string.Empty,
                DisplayName = index < 4 ? "64K Item Storage Cell" : string.Empty,
                CapacityUsed = index < 4 ? (index + 1) * 8_192 : 0,
                CapacityMax = index < 4 ? 65_536 : 0,
                TypesUsed = index < 4 ? index + 2 : 0,
                TypesMax = index < 4 ? 63 : 0
            })
            .ToList();
        return new StructuralSnapshotResponseMessage
        {
            MenuSessionId = Guid.NewGuid(),
            RequestSequence = 1,
            Kind = StructuralSnapshotKind.StorageDrive,
            Success = true,
            DisplayName = "Remote Storage Drive",
            LocationName = "Farm",
            TileX = 10,
            TileY = 10,
            StorageDrive = new RemoteStorageDriveSnapshotMessage
            {
                Slots = slots,
                SummaryLines =
                {
                    "Cells: 4 / 10",
                    "Bytes: 81,920 / 262,144",
                    "Types: 14 / 252",
                    "Network: online"
                }
            }
        };
    }

    private static StructuralSnapshotResponseMessage CreateRemoteTransferSnapshot(bool exporter)
    {
        return new StructuralSnapshotResponseMessage
        {
            MenuSessionId = Guid.NewGuid(),
            RequestSequence = 1,
            Kind = StructuralSnapshotKind.TransferBus,
            Success = true,
            DisplayName = exporter ? "Remote Exporter" : "Remote Importer",
            LocationName = "Farm",
            TileX = exporter ? 12 : 11,
            TileY = 10,
            TransferBus = new RemoteTransferBusSnapshotMessage
            {
                IsExporter = exporter,
                FilterBlacklist = exporter,
                OreDictionaryMode = true,
                QualityStrategy = MaterialQualityStrategy.LowQualityFirst,
                FacingDirection = exporter ? 1 : 3,
                UpgradeSlotCapacity = 4,
                FilterSlots = Enumerable.Range(0, 9).Select(index => new RemoteTransferFilterSlotMessage
                {
                    SlotIndex = index,
                    Occupied = index < 3,
                    QualifiedItemId = index switch { 0 => "(O)378", 1 => "(O)380", 2 => "(O)384", _ => string.Empty },
                    DisplayName = index switch { 0 => "Copper Ore", 1 => "Iron Ore", 2 => "Gold Ore", _ => string.Empty },
                    OreGroups = index < 3 ? new List<string> { "ore_item" } : new List<string>()
                }).ToList(),
                UpgradeSlots = Enumerable.Range(0, 4).Select(index => new RemoteTransferUpgradeSlotMessage
                {
                    SlotIndex = index,
                    Occupied = index < 3,
                    QualifiedItemId = index switch
                    {
                        0 => "(O)" + ModItemCatalog.SpeedCard,
                        1 => "(O)" + ModItemCatalog.CapacityCard,
                        2 => "(O)" + ModItemCatalog.OreDictionaryCard,
                        _ => string.Empty
                    },
                    DisplayName = index switch { 0 => "Speed Card", 1 => "Capacity Card", 2 => "Ore Dictionary Card", _ => string.Empty }
                }).ToList(),
                ConfigurationLines =
                {
                    exporter ? "Direction: network -> container" : "Direction: container -> network",
                    "Transfer: 64 items / 30 ticks",
                    "Ore dictionary: enabled",
                    "Facing: east"
                }
            }
        };
    }

    private static CraftingMonitorSnapshotResponseMessage CreateRemoteCraftingMonitorSnapshot()
    {
        var pattern = CreateRepresentativePattern();
        return new CraftingMonitorSnapshotResponseMessage
        {
            MenuSessionId = Guid.NewGuid(),
            RequestSequence = 1,
            NetworkId = Guid.NewGuid(),
            EndpointId = Guid.NewGuid(),
            Success = true,
            NetworkName = "Remote Crafting Network",
            QueuePattern = pattern,
            CaskPipelineItemPrototype = SerializedItemCodec.SerializePrototype(ItemRegistry.Create("(O)348")),
            CaskPipelineItemDisplayName = ItemRegistry.GetData("(O)348").DisplayName,
            Jobs =
            {
                new RemoteCraftingJobMessage
                {
                    JobId = Guid.NewGuid(),
                    Pattern = pattern,
                    DisplayName = pattern.DisplayName,
                    State = CraftingJobState.Running,
                    RequestedCount = 100,
                    CompletedCount = 42,
                    NodeCount = 3,
                    CpuSlotLabel = "CPU 1",
                    ReservedCount = 500,
                    RemainingReservedCount = 290,
                    StatusMessage = "Running",
                    CanCancel = true
                }
            },
            Pipelines =
            {
                new RemoteProductionPipelineMessage
                {
                    PipelineId = Guid.NewGuid(),
                    Enabled = true,
                    Priority = 10,
                    Pattern = pattern,
                    DisplayName = pattern.DisplayName,
                    TargetKeep = 256,
                    ItemsPerCycle = 8,
                    StatusMessage = "Online"
                }
            }
        };
    }

    private static StructuralSnapshotResponseMessage CreateRemotePatternProviderSnapshot()
    {
        var patternItem = ItemRegistry.Create("(O)" + ModItemCatalog.ProcessingPattern);
        return new StructuralSnapshotResponseMessage
        {
            MenuSessionId = Guid.NewGuid(),
            RequestSequence = 1,
            Kind = StructuralSnapshotKind.PatternProvider,
            Success = true,
            DisplayName = "Remote Pattern Provider",
            LocationName = "Farm",
            TileX = 13,
            TileY = 10,
            PatternProvider = new RemotePatternProviderSnapshotMessage
            {
                Priority = 10,
                Slots = Enumerable.Range(0, 9).Select(index => new RemotePatternProviderSlotMessage
                {
                    SlotIndex = index,
                    SerializedItem = index < 3 ? SerializedItemCodec.SerializePrototype(patternItem) : string.Empty,
                    DisplayName = index < 3 ? "Processing Pattern" : string.Empty
                }).ToList()
            }
        };
    }

    private static SObject CreateBigCraftable(string itemId)
    {
        return (SObject)ItemRegistry.Create("(BC)" + itemId);
    }

    private static Item CreateStack(string qualifiedItemId, int stack)
    {
        var item = ItemRegistry.Create(qualifiedItemId, 1);
        item.Stack = stack;
        return item;
    }

    private static int CountChestItem(Chest chest, string qualifiedItemId)
    {
        return chest.Items
            .Where(item => item is not null && item.QualifiedItemId == qualifiedItemId)
            .Sum(item => item!.Stack);
    }

    private Vector2 PlaceFixtureObject(GameLocation location, SObject obj)
    {
        var tile = this.FindFixtureTile(location);
        location.objects[tile] = obj;
        return tile;
    }

    private Vector2 FindFixtureTile(GameLocation location)
    {
        for (var y = 2; y < 80; y++)
        {
            for (var x = 2; x < 80; x++)
            {
                var tile = new Vector2(x, y);
                if (!location.objects.ContainsKey(tile))
                    return tile;
            }
        }

        return new Vector2(100 + location.objects.Count(), 100);
    }

    private Vector2 FindClearFixtureBlock(GameLocation location, int width, int height)
    {
        for (var y = 2; y <= 80 - height; y++)
        {
            for (var x = 2; x <= 80 - width; x++)
            {
                var origin = new Vector2(x, y);
                var clear = true;
                for (var offsetY = 0; offsetY < height && clear; offsetY++)
                {
                    for (var offsetX = 0; offsetX < width; offsetX++)
                    {
                        if (location.objects.ContainsKey(origin + new Vector2(offsetX, offsetY)))
                        {
                            clear = false;
                            break;
                        }
                    }
                }

                if (clear)
                    return origin;
            }
        }

        return new Vector2(120 + location.objects.Count(), 120);
    }

    private string GetScreenshotDirectory() => Path.Combine(this.outputDir, "screenshots", "SVSAP");

    private static int CalculatePanelCoveragePermille(Color[] pixels, int width, int height, IClickableMenu menu)
    {
        var left = Math.Clamp(menu.xPositionOnScreen, 0, width - 1);
        var top = Math.Clamp(menu.yPositionOnScreen, 0, height - 1);
        var right = Math.Clamp(menu.xPositionOnScreen + menu.width, left + 1, width);
        var bottom = Math.Clamp(menu.yPositionOnScreen + menu.height, top + 1, height);
        var sampled = 0;
        var colored = 0;
        for (var y = top; y < bottom; y += 2)
        {
            for (var x = left; x < right; x += 2)
            {
                var pixel = pixels[y * width + x];
                sampled++;
                if (pixel.A > 32 && pixel.R + pixel.G + pixel.B > 60)
                    colored++;
            }
        }
        return sampled == 0 ? 0 : colored * 1000 / sampled;
    }

    private static int CalculatePanelBlackPixelPermille(Color[] pixels, int width, int height, IClickableMenu menu)
    {
        var left = Math.Clamp(menu.xPositionOnScreen, 0, width - 1);
        var top = Math.Clamp(menu.yPositionOnScreen, 0, height - 1);
        var right = Math.Clamp(menu.xPositionOnScreen + menu.width, left + 1, width);
        var bottom = Math.Clamp(menu.yPositionOnScreen + menu.height, top + 1, height);
        var sampled = 0;
        var black = 0;
        for (var y = top; y < bottom; y += 2)
        {
            for (var x = left; x < right; x += 2)
            {
                var pixel = pixels[y * width + x];
                sampled++;
                if (pixel.A > 240 && pixel.R <= 3 && pixel.G <= 3 && pixel.B <= 3)
                    black++;
            }
        }

        return sampled == 0 ? 1000 : black * 1000 / sampled;
    }

    private void CloseCurrentMenu()
    {
        try
        {
            this.currentMenu?.exitThisMenuNoSound();
        }
        catch (Exception ex)
        {
            this.monitor.Log($"SVSAP_GUI_CAPTURE cleanup warning: {ex.Message}", LogLevel.Warn);
        }

        Game1.activeClickableMenu = null;
        this.currentMenu = null;
    }

    private void WriteProgress()
    {
        this.WriteJson("svsap-gui-capture-progress.json", new GuiCaptureReport(
            "SVSAP",
            this.cases?.Count ?? 0,
            this.results.Count(result => result.Pass),
            this.results.Count(result => !result.Pass),
            false,
            this.results));
    }

    private void Finish()
    {
        if (this.stopped)
            return;

        this.CloseCurrentMenu();
        var expected = this.cases?.Count ?? 0;
        var pass = expected > 0
            && this.results.Count == expected
            && this.results.All(result => result.Pass)
            && this.results.Select(result => result.Sha256).Distinct(StringComparer.Ordinal).Count() == expected;
        var report = new GuiCaptureReport(
            "SVSAP",
            expected,
            this.results.Count(result => result.Pass),
            this.results.Count(result => !result.Pass),
            pass,
            this.results);
        this.WriteJson(SvsapCompleteFileName, report);
        this.monitor.Log($"SVSAP_GUI_CAPTURE {(pass ? "COMPLETE" : "FAIL")} {report.Passed}/{report.Expected}", pass ? LogLevel.Info : LogLevel.Error);

        this.stopped = true;
        this.helper.Events.GameLoop.UpdateTicked -= this.OnUpdateTicked;
    }

    private void WriteJson(string fileName, object payload)
    {
        Directory.CreateDirectory(this.outputDir);
        File.WriteAllText(Path.Combine(this.outputDir, fileName), JsonSerializer.Serialize(payload, JsonOptions));
    }

    private sealed record GuiCaptureCase(string Name, Func<IClickableMenu> CreateMenu, int MinimumQualityOverlays = 0);

    private sealed record GuiCaptureReport(
        string Mod,
        int Expected,
        int Passed,
        int Failed,
        bool Pass,
        IReadOnlyList<GuiCaptureResult> Results);

    private sealed record GuiCaptureResult(
        string Name,
        bool Pass,
        string Path,
        int Width,
        int Height,
        long Bytes,
        int SampledColors,
        int PanelCoveragePermille,
        int BlackPixelPermille,
        int SlotGeometryViolationCount,
        IReadOnlyList<string> SlotGeometryErrors,
        int QualityOverlayCount,
        int MinimumQualityOverlays,
        string Sha256,
        string Error)
    {
        public static GuiCaptureResult Failed(string name, Exception ex)
        {
            return new GuiCaptureResult(name, false, string.Empty, 0, 0, 0, 0, 0, 1000, 1, new[] { ex.Message }, 0, 0, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
#endif
