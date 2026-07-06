using SVSAP.Content;
using SVSAP.Models;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class StorageDriveService
{
    private const int SlotCount = 10;

    private readonly NetworkRepository repository;
    private readonly EndpointIdentityService endpointIdentityService;
    private readonly StorageCellInitializer storageCellInitializer;
    private readonly Func<ModConfig> getConfig;
    private readonly IMonitor monitor;

    public StorageDriveService(
        NetworkRepository repository,
        EndpointIdentityService endpointIdentityService,
        StorageCellInitializer storageCellInitializer,
        Func<ModConfig> getConfig,
        IMonitor monitor)
    {
        this.repository = repository;
        this.endpointIdentityService = endpointIdentityService;
        this.storageCellInitializer = storageCellInitializer;
        this.getConfig = getConfig;
        this.monitor = monitor;
    }

    public bool TryHandleStorageDriveAction(SObject drive, GameLocation location, Vector2 tile)
    {
        if (drive.QualifiedItemId != "(BC)" + ModItemCatalog.StorageDrive)
            return false;

        if (!Guid.TryParse(drive.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.storageDrive.notLinked"), HUDMessage.error_type));
            this.LogGameplay($"action=storage_drive result=fail player={DescribePlayer(Game1.player)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} reason=\"not_linked\"");
            return true;
        }

        var endpointId = this.endpointIdentityService.EnsureEndpointId(drive);
        var network = this.repository.GetOrCreateNetwork(networkId);
        var driveData = this.GetOrCreateDrive(network, endpointId);
        var held = Game1.player.CurrentItem;

        if (held is not null && ModItemCatalog.TryGetStorageCellTier(held.QualifiedItemId, out _))
        {
            if (this.GetInsertedCellCount(network) >= Math.Max(1, this.getConfig().MaxStorageCellsPerNetwork))
            {
                Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.storageDrive.networkCellLimit"), HUDMessage.error_type));
                this.LogGameplay($"action=storage_cell_insert result=fail player={DescribePlayer(Game1.player)} network={ShortId(networkId)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} cell={Quote(held.DisplayName)} reason=\"network_cell_limit\"");
                return true;
            }

            this.InsertCell(network, driveData, held);
            this.repository.Save();
            return true;
        }

        this.EjectCell(network, driveData, location, tile);
        this.repository.Save();
        return true;
    }

    public StructuralActionResult ApplyInteract(
        SObject drive,
        GameLocation location,
        Vector2 tile,
        string heldQualifiedItemId,
        string heldSerializedItem)
    {
        if (drive.QualifiedItemId != "(BC)" + ModItemCatalog.StorageDrive)
            return StructuralActionResult.Fail(ModText.Get("ui.storageDrive.notDrive"));

        if (!Guid.TryParse(drive.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
            return StructuralActionResult.Fail(ModText.Get("ui.storageDrive.notLinked"));

        var endpointId = this.endpointIdentityService.EnsureEndpointId(drive);
        var network = this.repository.GetOrCreateNetwork(networkId);
        var driveData = this.GetOrCreateDrive(network, endpointId);

        if (!string.IsNullOrWhiteSpace(heldSerializedItem)
            && ModItemCatalog.TryGetStorageCellTier(heldQualifiedItemId, out _))
        {
            if (this.GetInsertedCellCount(network) >= Math.Max(1, this.getConfig().MaxStorageCellsPerNetwork))
                return StructuralActionResult.Fail(ModText.Get("ui.storageDrive.networkCellLimit"));

            Item heldCell;
            try
            {
                heldCell = SerializedItemCodec.CreateItem(heldSerializedItem, 1);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Could not read storage cell payload: {ex.Message}", LogLevel.Trace);
                return StructuralActionResult.Fail(ModText.Get("ui.storageDrive.readFailed"));
            }

            if (!ModItemCatalog.TryGetStorageCellTier(heldCell.QualifiedItemId, out _))
                return StructuralActionResult.Fail(ModText.Get("ui.storageDrive.readFailed"));

            var inserted = this.InsertCellIntoNetwork(network, driveData, heldCell);
            if (inserted.Success)
                this.repository.Save();

            return inserted;
        }

        var ejected = this.EjectCellFromNetwork(driveData);
        if (ejected.Success)
            this.repository.Save();

        return ejected;
    }

    public void ResetRemainingHeldStorageCell(Item held)
    {
        this.storageCellInitializer.ResetEmptyCellData(held);
    }

    public IReadOnlyList<string> DescribeDrive(SObject drive)
    {
        if (!this.TryResolveDrive(drive, create: false, out _, out var driveData, out _, out var message))
            return new[] { message };

        if (driveData is null || driveData.Slots.Count == 0)
            return new[] { ModText.Get("ui.storageDrive.empty"), ModText.Get("ui.storageDrive.help") };

        var lines = new List<string>
        {
            ModText.Format("ui.storageDrive.inserted", driveData.Slots.Count, SlotCount)
        };
        foreach (var slot in driveData.Slots.OrderBy(slot => slot.SlotIndex))
        {
            var capacity = ReadCellCapacity(slot);
            lines.Add(ModText.Format("ui.storageDrive.slot", slot.SlotIndex + 1, FormatItem(slot.QualifiedItemId), capacity.Used, capacity.Max));
        }

        return lines;
    }

    public IReadOnlyList<int> GetOccupiedSlotIndexes(SObject drive)
    {
        return this.TryResolveDrive(drive, create: false, out _, out var driveData, out _, out _)
            && driveData is not null
            ? driveData.Slots.OrderBy(slot => slot.SlotIndex).Select(slot => slot.SlotIndex).ToList()
            : Array.Empty<int>();
    }

    public bool HasCellSlot(SObject drive, int slotIndex)
    {
        return this.TryResolveDrive(drive, create: false, out _, out var driveData, out _, out _)
            && driveData is not null
            && driveData.Slots.Any(slot => slot.SlotIndex == slotIndex);
    }

    public bool TryEjectCellSlot(SObject drive, GameLocation location, Vector2 tile, int slotIndex, out string message)
    {
        if (!this.TryResolveDrive(drive, create: false, out _, out var driveData, out _, out message)
            || driveData is null)
        {
            return false;
        }

        var slot = driveData.Slots.FirstOrDefault(entry => entry.SlotIndex == slotIndex);
        if (slot is null)
        {
            message = ModText.Get("ui.storageDrive.slotEmpty");
            return false;
        }

        var item = StorageCellCodec.CreateItem(slot);
        this.ReturnCellItem(item, location, tile, preferPlayerInventory: true);
        driveData.Slots.Remove(slot);
        driveData.InsertedCellIds = driveData.Slots.Select(entry => entry.CellId).ToList();
        this.repository.Save();

        message = ModText.Format("ui.storageDrive.ejectedSlot", slotIndex + 1);
        return true;
    }

    public void HandleStorageDriveRemoved(SObject drive, GameLocation location, Vector2 tile)
    {
        if (drive.QualifiedItemId != "(BC)" + ModItemCatalog.StorageDrive)
            return;

        if (!Guid.TryParse(drive.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId)
            || !Guid.TryParse(drive.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out var endpointId)
            || !this.repository.TryGetNetwork(networkId, out var network))
        {
            return;
        }

        if (network.StorageDrives.TryGetValue(endpointId, out var driveData))
        {
            var returned = 0;
            foreach (var slot in driveData.Slots.OrderBy(slot => slot.SlotIndex).ToList())
            {
                this.ReturnCellItem(StorageCellCodec.CreateItem(slot), location, tile, preferPlayerInventory: !Context.IsMultiplayer);
                returned++;
            }

            this.LogGameplay($"action=storage_drive_removed result=success network={ShortId(networkId)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} returnedCells={returned:N0}");

            network.StorageDrives.Remove(endpointId);
        }

        network.Endpoints.RemoveAll(endpoint => endpoint.EndpointId == endpointId);
        this.repository.Save();
    }

    private StorageDriveData GetOrCreateDrive(NetworkData network, Guid endpointId)
    {
        if (!network.StorageDrives.TryGetValue(endpointId, out var driveData))
        {
            driveData = new StorageDriveData { EndpointId = endpointId };
            network.StorageDrives[endpointId] = driveData;
        }

        return driveData;
    }

    private bool TryResolveDrive(
        SObject drive,
        bool create,
        out NetworkData network,
        out StorageDriveData? driveData,
        out Guid endpointId,
        out string message)
    {
        network = null!;
        driveData = null;
        endpointId = Guid.Empty;
        message = string.Empty;

        if (drive.QualifiedItemId != "(BC)" + ModItemCatalog.StorageDrive)
        {
            message = ModText.Get("ui.storageDrive.notDrive");
            return false;
        }

        if (!Guid.TryParse(drive.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
        {
            message = ModText.Get("ui.storageDrive.notLinked");
            return false;
        }

        endpointId = this.endpointIdentityService.EnsureEndpointId(drive);
        network = this.repository.GetOrCreateNetwork(networkId);
        driveData = create
            ? this.GetOrCreateDrive(network, endpointId)
            : network.StorageDrives.GetValueOrDefault(endpointId);
        return true;
    }

    private int GetInsertedCellCount(NetworkData network)
    {
        return network.StorageDrives.Values.Sum(drive => drive.Slots.Count);
    }

    private StructuralActionResult InsertCellIntoNetwork(NetworkData network, StorageDriveData driveData, Item held)
    {
        if (driveData.Slots.Count >= SlotCount)
            return StructuralActionResult.Fail(ModText.Get("ui.storageDrive.full"));

        this.storageCellInitializer.EnsureCellData(held);
        var slotIndex = Enumerable.Range(0, SlotCount)
            .First(index => driveData.Slots.All(slot => slot.SlotIndex != index));

        var cellItem = held.getOne();
        cellItem.Stack = 1;
        this.storageCellInitializer.EnsureCellData(cellItem);
        if (StorageCellCodec.TryReadCellData(cellItem, out var cellData)
            && this.IsCellIdInserted(network, cellData.CellId))
        {
            if (!IsCellEmpty(cellData))
                return StructuralActionResult.Fail(ModText.Get("ui.storageDrive.duplicateNonEmpty"));

            this.storageCellInitializer.ResetEmptyCellData(cellItem);
        }

        var slot = StorageCellCodec.ToSlotData(cellItem, slotIndex);
        driveData.Slots.Add(slot);
        driveData.InsertedCellIds = driveData.Slots.Select(entry => entry.CellId).ToList();

        return new StructuralActionResult
        {
            Success = true,
            Message = ModText.Get("ui.storageDrive.insertedOne"),
            ConsumeHeldOne = true
        };
    }

    private StructuralActionResult EjectCellFromNetwork(StorageDriveData driveData)
    {
        var slot = driveData.Slots.OrderByDescending(entry => entry.SlotIndex).FirstOrDefault();
        if (slot is null)
            return StructuralActionResult.Fail(ModText.Get("ui.storageDrive.empty"));

        var item = StorageCellCodec.CreateItem(slot);
        driveData.Slots.Remove(slot);
        driveData.InsertedCellIds = driveData.Slots.Select(entry => entry.CellId).ToList();

        return new StructuralActionResult
        {
            Success = true,
            Message = ModText.Get("ui.storageDrive.ejectedOne"),
            ReturnedSerializedItem = SerializedItemCodec.SerializePrototype(item)
        };
    }

    private void InsertCell(NetworkData network, StorageDriveData driveData, Item held)
    {
        if (driveData.Slots.Count >= SlotCount)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.storageDrive.full"), HUDMessage.error_type));
            this.LogGameplay($"action=storage_cell_insert result=fail player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} cell={Quote(held.DisplayName)} reason=\"drive_full\"");
            return;
        }

        this.storageCellInitializer.EnsureCellData(held);
        var slotIndex = Enumerable.Range(0, SlotCount)
            .First(index => driveData.Slots.All(slot => slot.SlotIndex != index));

        var cellItem = held.getOne();
        cellItem.Stack = 1;
        this.storageCellInitializer.EnsureCellData(cellItem);
        if (StorageCellCodec.TryReadCellData(cellItem, out var cellData)
            && this.IsCellIdInserted(network, cellData.CellId))
        {
            if (!IsCellEmpty(cellData))
            {
                Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.storageDrive.duplicateNonEmpty"), HUDMessage.error_type));
                this.LogGameplay($"action=storage_cell_insert result=fail player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} cell={Quote(cellItem.DisplayName)} cellId={ShortId(cellData.CellId)} reason=\"duplicate_non_empty_cell\"");
                return;
            }

            this.storageCellInitializer.ResetEmptyCellData(cellItem);
        }

        var slot = StorageCellCodec.ToSlotData(cellItem, slotIndex);
        driveData.Slots.Add(slot);
        driveData.InsertedCellIds = driveData.Slots.Select(entry => entry.CellId).ToList();

        held.Stack -= 1;
        if (held.Stack <= 0)
            Game1.player.removeItemFromInventory(held);
        else
            this.storageCellInitializer.ResetEmptyCellData(held);

        Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.storageDrive.insertedOne"), HUDMessage.newQuest_type));
        var insertedCapacity = ReadCellCapacity(slot);
        this.LogGameplay($"action=storage_cell_insert result=success player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} slot={slotIndex:N0} cell={Quote(cellItem.DisplayName)} cellId={ShortId(slot.CellId)} capacityUsed={insertedCapacity.Used:N0} capacityMax={insertedCapacity.Max:N0}");
    }

    private void EjectCell(NetworkData network, StorageDriveData driveData, GameLocation location, Vector2 tile)
    {
        var slot = driveData.Slots.OrderByDescending(entry => entry.SlotIndex).FirstOrDefault();
        if (slot is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.storageDrive.empty"), HUDMessage.error_type));
            this.LogGameplay($"action=storage_cell_eject result=fail player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} reason=\"drive_empty\"");
            return;
        }

        var item = StorageCellCodec.CreateItem(slot);
        this.ReturnCellItem(item, location, tile, preferPlayerInventory: true);

        driveData.Slots.Remove(slot);
        driveData.InsertedCellIds = driveData.Slots.Select(entry => entry.CellId).ToList();
        Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.storageDrive.ejectedOne"), HUDMessage.newQuest_type));
        var ejectedCapacity = ReadCellCapacity(slot);
        this.LogGameplay($"action=storage_cell_eject result=success player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} cell={Quote(item.DisplayName)} cellId={ShortId(slot.CellId)} capacityUsed={ejectedCapacity.Used:N0} capacityMax={ejectedCapacity.Max:N0}");
    }

    private void ReturnCellItem(Item item, GameLocation location, Vector2 tile, bool preferPlayerInventory)
    {
        if (preferPlayerInventory && TryPlaceCellInEmptyInventorySlot(Game1.player, item))
        {
            return;
        }

        Game1.createItemDebris(item, (tile + new Vector2(0.5f, 0.5f)) * Game1.tileSize, -1, location);
    }

    private static bool TryPlaceCellInEmptyInventorySlot(Farmer player, Item item)
    {
        for (var i = 0; i < player.Items.Count; i++)
        {
            if (player.Items[i] is not null)
                continue;

            player.Items[i] = item;
            return true;
        }

        return false;
    }

    private bool IsCellIdInserted(NetworkData network, Guid cellId)
    {
        return cellId != Guid.Empty
            && network.StorageDrives.Values
                .SelectMany(drive => drive.Slots)
                .Any(slot => slot.CellId == cellId);
    }

    private static bool IsCellEmpty(StorageCellData data)
    {
        return data.CapacityUsed <= 0 && data.Items.All(stack => stack.Count <= 0);
    }

    private static (long Used, long Max) ReadCellCapacity(StorageDriveSlotData slot)
    {
        return StorageCellCodec.TryReadCellData(slot, out var data)
            ? (data.CapacityUsed, data.CapacityMax)
            : (0, 0);
    }

    private static string FormatItem(string qualifiedItemId)
    {
        try
        {
            return ItemRegistry.Create(qualifiedItemId).DisplayName;
        }
        catch
        {
            return qualifiedItemId;
        }
    }

    private void LogGameplay(string message)
    {
        if (this.getConfig().DetailedGameplayLogs)
            this.monitor.Log("SVSAP_GAMELOG " + message, LogLevel.Info);
    }

    private static string DescribePlayer(Farmer player)
    {
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
}
