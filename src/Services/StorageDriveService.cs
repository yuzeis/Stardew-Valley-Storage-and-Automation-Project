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
            Game1.addHUDMessage(new HUDMessage("请先把这个存储驱动器连接到网络。", HUDMessage.error_type));
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
                Game1.addHUDMessage(new HUDMessage("SVSAP 存储元件数量已达上限。", HUDMessage.error_type));
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
            return StructuralActionResult.Fail("目标不是存储驱动器。");

        if (!Guid.TryParse(drive.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
            return StructuralActionResult.Fail("请先把这个存储驱动器连接到网络。");

        var endpointId = this.endpointIdentityService.EnsureEndpointId(drive);
        var network = this.repository.GetOrCreateNetwork(networkId);
        var driveData = this.GetOrCreateDrive(network, endpointId);

        if (!string.IsNullOrWhiteSpace(heldSerializedItem)
            && ModItemCatalog.TryGetStorageCellTier(heldQualifiedItemId, out _))
        {
            if (this.GetInsertedCellCount(network) >= Math.Max(1, this.getConfig().MaxStorageCellsPerNetwork))
                return StructuralActionResult.Fail("SVSAP 存储元件数量已达上限。");

            Item heldCell;
            try
            {
                heldCell = SerializedItemCodec.CreateItem(heldSerializedItem, 1);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Could not read storage cell payload: {ex.Message}", LogLevel.Trace);
                return StructuralActionResult.Fail("无法读取这个存储元件。");
            }

            if (!ModItemCatalog.TryGetStorageCellTier(heldCell.QualifiedItemId, out _))
                return StructuralActionResult.Fail("无法读取这个存储元件。");

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

    private int GetInsertedCellCount(NetworkData network)
    {
        return network.StorageDrives.Values.Sum(drive => drive.Slots.Count);
    }

    private StructuralActionResult InsertCellIntoNetwork(NetworkData network, StorageDriveData driveData, Item held)
    {
        if (driveData.Slots.Count >= SlotCount)
            return StructuralActionResult.Fail("存储驱动器已满。");

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
                return StructuralActionResult.Fail("已有相同的非空存储元件插入网络。");

            this.storageCellInitializer.ResetEmptyCellData(cellItem);
        }

        var slot = StorageCellCodec.ToSlotData(cellItem, slotIndex);
        driveData.Slots.Add(slot);
        driveData.InsertedCellIds = driveData.Slots.Select(entry => entry.CellId).ToList();

        return new StructuralActionResult
        {
            Success = true,
            Message = "已插入存储元件。",
            ConsumeHeldOne = true
        };
    }

    private StructuralActionResult EjectCellFromNetwork(StorageDriveData driveData)
    {
        var slot = driveData.Slots.OrderByDescending(entry => entry.SlotIndex).FirstOrDefault();
        if (slot is null)
            return StructuralActionResult.Fail("存储驱动器为空。");

        var item = StorageCellCodec.CreateItem(slot);
        driveData.Slots.Remove(slot);
        driveData.InsertedCellIds = driveData.Slots.Select(entry => entry.CellId).ToList();

        return new StructuralActionResult
        {
            Success = true,
            Message = "已弹出存储元件。",
            ReturnedSerializedItem = SerializedItemCodec.SerializePrototype(item)
        };
    }

    private void InsertCell(NetworkData network, StorageDriveData driveData, Item held)
    {
        if (driveData.Slots.Count >= SlotCount)
        {
            Game1.addHUDMessage(new HUDMessage("存储驱动器已满。", HUDMessage.error_type));
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
                Game1.addHUDMessage(new HUDMessage("已有相同的非空存储元件插入网络。", HUDMessage.error_type));
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

        Game1.addHUDMessage(new HUDMessage("已插入存储元件。", HUDMessage.newQuest_type));
        var insertedCapacity = ReadCellCapacity(slot);
        this.LogGameplay($"action=storage_cell_insert result=success player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} slot={slotIndex:N0} cell={Quote(cellItem.DisplayName)} cellId={ShortId(slot.CellId)} capacityUsed={insertedCapacity.Used:N0} capacityMax={insertedCapacity.Max:N0}");
    }

    private void EjectCell(NetworkData network, StorageDriveData driveData, GameLocation location, Vector2 tile)
    {
        var slot = driveData.Slots.OrderByDescending(entry => entry.SlotIndex).FirstOrDefault();
        if (slot is null)
        {
            Game1.addHUDMessage(new HUDMessage("存储驱动器为空。", HUDMessage.error_type));
            this.LogGameplay($"action=storage_cell_eject result=fail player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} reason=\"drive_empty\"");
            return;
        }

        var item = StorageCellCodec.CreateItem(slot);
        this.ReturnCellItem(item, location, tile, preferPlayerInventory: true);

        driveData.Slots.Remove(slot);
        driveData.InsertedCellIds = driveData.Slots.Select(entry => entry.CellId).ToList();
        Game1.addHUDMessage(new HUDMessage("已弹出存储元件。", HUDMessage.newQuest_type));
        var ejectedCapacity = ReadCellCapacity(slot);
        this.LogGameplay($"action=storage_cell_eject result=success player={DescribePlayer(Game1.player)} network={ShortId(network.NetworkId)} location={Quote(location.NameOrUniqueName)} tile={FormatTile(tile)} cell={Quote(item.DisplayName)} cellId={ShortId(slot.CellId)} capacityUsed={ejectedCapacity.Used:N0} capacityMax={ejectedCapacity.Max:N0}");
    }

    private void ReturnCellItem(Item item, GameLocation location, Vector2 tile, bool preferPlayerInventory)
    {
        if (preferPlayerInventory
            && Game1.player.couldInventoryAcceptThisItem(item)
            && Game1.player.addItemToInventoryBool(item))
        {
            return;
        }

        Game1.createItemDebris(item, (tile + new Vector2(0.5f, 0.5f)) * Game1.tileSize, -1, location);
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
