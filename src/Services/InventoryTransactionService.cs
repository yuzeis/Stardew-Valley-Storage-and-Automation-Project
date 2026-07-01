using Microsoft.Xna.Framework;
using SVSAP.Models;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class InventoryTransactionService
{
    private const int RecentItemLimit = 512;

    private static readonly Vector2[] AdjacentOffsets =
    {
        new Vector2(0, -1),
        new Vector2(1, 0),
        new Vector2(0, 1),
        new Vector2(-1, 0)
    };

    private readonly NetworkRepository repository;
    private readonly InventoryScanner scanner;
    private readonly Func<ModConfig> getConfig;
    private readonly IMonitor monitor;

    public InventoryTransactionService(NetworkRepository repository, InventoryScanner scanner, Func<ModConfig> getConfig, IMonitor monitor)
    {
        this.repository = repository;
        this.scanner = scanner;
        this.getConfig = getConfig;
        this.monitor = monitor;
    }

    public void SaveNetworkState()
    {
        this.repository.Save();
    }

    public void RecoverPendingTransactions()
    {
        if (this.repository.Data.PendingTransactions.Count == 0)
            return;

        var recovered = 0;
        foreach (var tx in this.repository.Data.PendingTransactions.ToList())
        {
            if (tx.State == TxState.Committed)
            {
                this.repository.Data.PendingTransactions.Remove(tx);
                recovered++;
                continue;
            }

            if (!this.repository.TryGetNetwork(tx.NetworkId, out var network))
            {
                this.repository.Data.PendingTransactions.Remove(tx);
                recovered++;
                continue;
            }

            var recoveryAction = this.RecoverPreparedTransaction(network, tx);
            this.monitor.Log($"Recovered prepared {tx.Kind} transaction {tx.TxId:N}; {recoveryAction}.", LogLevel.Trace);
            this.repository.Data.PendingTransactions.Remove(tx);
            recovered++;
        }

        this.repository.Save();
        this.monitor.Log($"Recovered {recovered} pending SVSAP inventory transaction(s).", LogLevel.Info);
    }

    public bool TryWithdraw(NetworkData network, ItemKey requestedKey, int requestedCount, out string message)
    {
        return this.TryWithdrawForPlayer(network, Game1.player, requestedKey, requestedCount, out message);
    }

    public bool TryWithdrawForPlayer(NetworkData network, Farmer player, ItemKey requestedKey, int requestedCount, out string message)
    {
        var fresh = this.scanner.Scan(network);
        var entry = fresh.Entries.FirstOrDefault(candidate => ItemKeyFactory.SameDisplayBucket(candidate.Key, requestedKey));
        var reserved = entry is null ? 0L : this.GetReservedCountForEntry(network, entry, Guid.Empty);
        var unreserved = entry is null ? 0L : Math.Max(0L, entry.TotalCount - reserved);
        if (entry is null || unreserved <= 0)
        {
            message = "物品已不可用，或已被 CPU 作业预留。";
            this.LogGameplay($"action=withdraw result=fail player={DescribePlayer(player)} network={ShortId(network.NetworkId)} requestedItem={Quote(requestedKey.QualifiedItemId)} requested={requestedCount:N0} available=0 reserved={reserved:N0} reason={Quote(message)}");
            return false;
        }

        var count = ClampToIntCount(requestedCount <= 0 ? unreserved : Math.Min((long)requestedCount, unreserved));
        if (count <= 0)
        {
            message = "单次取出数量过大。";
            this.LogGameplay($"action=withdraw result=fail player={DescribePlayer(player)} network={ShortId(network.NetworkId)} requestedItem={Quote(requestedKey.QualifiedItemId)} requested={requestedCount:N0} available={unreserved:N0} reason={Quote(message)}");
            return false;
        }

        var output = entry.Prototype.getOne();
        output.Stack = count;
        var acceptedByInventory = GetInventoryAcceptCount(player, output, count);
        if (acceptedByInventory <= 0)
        {
            message = "背包已满。";
            this.LogGameplay($"action=withdraw result=fail player={DescribePlayer(player)} network={ShortId(network.NetworkId)} item={Quote(entry.Prototype.DisplayName)} itemId={Quote(entry.Key.QualifiedItemId)} requested={requestedCount:N0} planned={count:N0} reason={Quote(message)}");
            return false;
        }

        if (acceptedByInventory < count)
        {
            count = acceptedByInventory;
            output.Stack = count;
        }

        var removed = this.RemoveFromCellSources(network, entry, count, out var rollbackCells);
        var rollbackChests = new List<ChestRollback>();
        if (removed < count)
        {
            removed += this.RemoveFromChestSources(entry, count - removed, out var chestRollbacks);
            rollbackChests.AddRange(chestRollbacks);
        }

        if (removed <= 0)
        {
            AbortCellTransactions(rollbackCells);
            message = "无法从网络移除物品。";
            this.LogGameplay($"action=withdraw result=fail player={DescribePlayer(player)} network={ShortId(network.NetworkId)} item={Quote(entry.Prototype.DisplayName)} itemId={Quote(entry.Key.QualifiedItemId)} requested={requestedCount:N0} planned={count:N0} reason={Quote(message)}");
            return false;
        }

        output.Stack = removed;
        if (!player.addItemToInventoryBool(output))
        {
            RollbackCells(rollbackCells);
            RollbackChests(rollbackChests);
            AbortCellTransactions(rollbackCells);

            this.monitor.Log("Player inventory rejected an item after preflight; SVSAP restored the removed network sources.", LogLevel.Error);
            message = "取出时背包发生变化，网络库存已回滚。";
            this.LogGameplay($"action=withdraw result=fail player={DescribePlayer(player)} network={ShortId(network.NetworkId)} item={Quote(entry.Prototype.DisplayName)} itemId={Quote(entry.Key.QualifiedItemId)} requested={requestedCount:N0} removed={removed:N0} reason={Quote(message)}");
            return false;
        }

        CommitCellTransactions(rollbackCells);
        ReleaseChestLocks(rollbackChests);
        message = $"已取出 {entry.Prototype.DisplayName} x{removed:N0}。";
        this.LogGameplay($"action=withdraw result=success player={DescribePlayer(player)} network={ShortId(network.NetworkId)} item={Quote(entry.Prototype.DisplayName)} itemId={Quote(entry.Key.QualifiedItemId)} requested={requestedCount:N0} moved={removed:N0} reservedBefore={reserved:N0} availableBefore={unreserved:N0}");
        return true;
    }

    public bool TryExtractItem(NetworkData network, NetworkItemRequest request, int requestedCount, out Item? extracted, out string message, Guid excludedReservationJobId = default, MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst)
    {
        extracted = null;
        var snapshot = this.scanner.Scan(network);
        var unreservedCount = this.GetUnreservedCount(network, request, excludedReservationJobId, qualityStrategy, autoConsumableOnly: true);
        if (unreservedCount <= 0)
        {
            message = "请求的物品已被其他 CPU 作业预留。";
            return false;
        }

        var ordered = snapshot.Entries
            .Where(candidate => MatchesRequest(candidate, request))
            .Where(candidate => CanAutoConsume(candidate.Prototype))
            .Where(candidate => AllowsQuality(candidate, qualityStrategy));
        var entry = qualityStrategy == MaterialQualityStrategy.HighQualityFirst
            ? ordered
                .OrderByDescending(candidate => candidate.Key.Quality)
                .ThenByDescending(candidate => candidate.TotalCount)
                .FirstOrDefault()
            : ordered
                .OrderBy(candidate => candidate.Key.Quality)
                .ThenByDescending(candidate => candidate.TotalCount)
                .FirstOrDefault();

        if (entry is null || entry.TotalCount <= 0)
        {
            message = "请求的物品当前不可用。";
            return false;
        }

        var count = Math.Min(Math.Min(Math.Max(0, requestedCount), ClampToIntCount(entry.TotalCount)), unreservedCount);
        var output = entry.Prototype.getOne();
        output.Stack = count;

        var removed = this.RemoveFromCellSources(network, entry, count, out var cellRollbacks);
        var chestRollbacks = new List<ChestRollback>();
        if (removed < count)
        {
            removed += this.RemoveFromChestSources(entry, count - removed, out chestRollbacks, autoConsumableOnly: true);
        }

        if (removed <= 0)
        {
            AbortCellTransactions(cellRollbacks);
            ReleaseChestLocks(chestRollbacks);
            message = "无法抽取请求的物品。";
            return false;
        }

        output.Stack = removed;
        extracted = output;
        CommitCellTransactions(cellRollbacks);
        ReleaseChestLocks(chestRollbacks);
        message = $"已抽取 {output.DisplayName} x{removed:N0}。";
        return true;
    }

    public bool TryExtractFirstMatchingItem(
        NetworkData network,
        Func<NetworkInventoryEntry, bool> predicate,
        Func<NetworkInventoryEntry, int> requestedCountSelector,
        out Item? extracted,
        out string message,
        MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst)
    {
        extracted = null;
        var snapshot = this.scanner.Scan(network);
        var ordered = snapshot.Entries
            .Where(predicate)
            .Where(entry => CanAutoConsume(entry.Prototype))
            .Where(entry => AllowsQuality(entry, qualityStrategy))
            .Select(entry => new
            {
                Entry = entry,
                UnreservedCount = ClampToIntCount(entry.TotalCount - this.GetReservedCountForEntry(network, entry, Guid.Empty))
            })
            .Where(candidate => candidate.UnreservedCount > 0);

        var candidate = qualityStrategy == MaterialQualityStrategy.HighQualityFirst
            ? ordered
                .OrderByDescending(candidate => candidate.Entry.Key.Quality)
                .ThenByDescending(candidate => candidate.UnreservedCount)
                .FirstOrDefault()
            : ordered
                .OrderBy(candidate => candidate.Entry.Key.Quality)
                .ThenByDescending(candidate => candidate.UnreservedCount)
                .FirstOrDefault();

        if (candidate is null)
        {
            message = "没有可用的未预留匹配物品。";
            return false;
        }

        var requestedCount = Math.Min(Math.Max(0, requestedCountSelector(candidate.Entry)), candidate.UnreservedCount);
        if (requestedCount <= 0)
        {
            message = "匹配物品已经达到目标数量。";
            return false;
        }

        var request = new NetworkItemRequest
        {
            QualifiedItemId = candidate.Entry.Key.QualifiedItemId,
            SerializedItemPrototype = SerializedItemCodec.SerializePrototype(candidate.Entry.Prototype),
            Count = requestedCount
        };

        return this.TryExtractItem(network, request, requestedCount, out extracted, out message, qualityStrategy: qualityStrategy);
    }

    public bool TryDepositFromPlayer(NetworkData network, bool sameOnly, out string message)
    {
        return this.TryDepositFromPlayer(network, Game1.player, sameOnly, out message);
    }

    public bool TryDepositFromPlayer(NetworkData network, Farmer player, bool sameOnly, out string message)
    {
        var existing = sameOnly ? this.scanner.Scan(network).Entries : new List<NetworkInventoryEntry>();
        var chests = this.GetWritableChests(network).ToList();
        var moved = 0;
        var movedDetails = new List<string>();

        for (var slot = 0; slot < player.Items.Count; slot++)
        {
            var item = player.Items[slot];
            if (item is null || item.Stack <= 0)
                continue;

            if (!this.CanEnterNetwork(item))
                continue;

            if (network.LockedQualifiedItemIds.Contains(item.QualifiedItemId, StringComparer.Ordinal))
                continue;

            if (sameOnly && !existing.Any(entry => ItemKeyFactory.SameStackBucket(entry.Key, entry.Prototype, ItemKeyFactory.FromItem(item), item)))
                continue;

            var beforeStack = item.Stack;
            if (this.getConfig().PreferStorageCellsForDeposits)
            {
                moved += this.TryDepositStackIntoStorageCells(network, item);
                moved += this.TryDepositStackIntoChests(chests, item);
            }
            else
            {
                moved += this.TryDepositStackIntoChests(chests, item);
                moved += this.TryDepositStackIntoStorageCells(network, item);
            }

            if (beforeStack > item.Stack)
            {
                RecordRecentAdded(network, item);
                if (movedDetails.Count < 12)
                    movedDetails.Add($"{item.QualifiedItemId}:{beforeStack - item.Stack:N0}");
            }

            if (item.Stack <= 0)
                player.Items[slot] = null;
        }

        if (moved <= 0)
        {
            message = sameOnly ? "没有可存入的同类物品。" : "没有可存入的物品。";
            this.LogGameplay($"action=deposit result=fail player={DescribePlayer(player)} network={ShortId(network.NetworkId)} mode={(sameOnly ? "same" : "all")} writableChests={chests.Count:N0} reason={Quote(message)}");
            return false;
        }

        message = sameOnly ? $"已存入同类物品 x{moved:N0}。" : $"已存入物品 x{moved:N0}。";
        this.LogGameplay($"action=deposit result=success player={DescribePlayer(player)} network={ShortId(network.NetworkId)} mode={(sameOnly ? "same" : "all")} moved={moved:N0} itemSummary={Quote(string.Join(",", movedDetails))}");
        return true;
    }

    public bool TryDepositItem(NetworkData network, Item item, out int moved, Chest? excludedChest = null)
    {
        if (!this.CanEnterNetwork(item))
        {
            moved = 0;
            return false;
        }

        var chests = this.GetWritableChests(network)
            .Where(chest => !ReferenceEquals(chest, excludedChest))
            .ToList();
        moved = 0;

        var beforeStack = item.Stack;
        if (this.getConfig().PreferStorageCellsForDeposits)
        {
            moved += this.TryDepositStackIntoStorageCells(network, item);
            moved += this.TryDepositStackIntoChests(chests, item);
        }
        else
        {
            moved += this.TryDepositStackIntoChests(chests, item);
            moved += this.TryDepositStackIntoStorageCells(network, item);
        }

        if (beforeStack > item.Stack)
            RecordRecentAdded(network, item);

        return moved > 0;
    }

    public bool TryReturnItemToNetwork(NetworkData network, Item item)
    {
        return this.TryDepositItem(network, item, out _);
    }

    public bool CanAcceptItem(NetworkData network, Item item, int count)
    {
        return this.CanAcceptItem(network, Game1.player, item, count);
    }

    public bool CanAcceptItem(NetworkData network, Farmer player, Item item, int count)
    {
        if (player.couldInventoryAcceptThisItem(item))
            return true;

        return this.CanAcceptNetworkItem(network, item, count);
    }

    public bool CanAcceptNetworkItem(NetworkData network, Item item, int count)
    {
        if (!this.CanEnterNetwork(item))
            return false;

        var remaining = count;
        if (this.CanDigitize(item))
        {
            foreach (var slot in this.GetActiveStorageCellSlots(network))
            {
                if (!StorageCellCodec.TryReadCellData(slot, out var data))
                    continue;

                var exactStack = data.Items.FirstOrDefault(candidate =>
                {
                    if (!this.TryCreateStoredItemPrototype(candidate, out var prototype))
                        return false;

                    return item.canStackWith(prototype);
                });

                remaining -= StorageCellTierInfo.GetRemainingItemCapacity(
                    data,
                    exactStack,
                    remaining,
                    this.getConfig().MaxItemTypesPerStorageCell);

                if (remaining <= 0)
                    return true;
            }
        }

        foreach (var chest in this.GetWritableChests(network))
        {
            foreach (var stack in chest.Items)
            {
                if (remaining <= 0)
                    return true;

                if (stack is null)
                {
                    remaining -= item.maximumStackSize();
                    continue;
                }

                if (stack.canStackWith(item))
                    remaining -= Math.Max(0, stack.maximumStackSize() - stack.Stack);
            }
        }

        return remaining <= 0;
    }

    public int GetAvailableCount(NetworkData network, NetworkItemRequest request, MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst, bool autoConsumableOnly = false)
    {
        var count = this.scanner.Scan(network).Entries
            .Where(entry => MatchesRequest(entry, request) && AllowsQuality(entry, qualityStrategy))
            .Where(entry => !autoConsumableOnly || CanAutoConsume(entry.Prototype))
            .Sum(entry => entry.TotalCount);
        return ClampToIntCount(count);
    }

    public int GetAvailableCountMatching(NetworkData network, Func<NetworkInventoryEntry, bool> predicate)
    {
        var count = this.scanner.Scan(network).Entries
            .Where(predicate)
            .Sum(entry => entry.TotalCount);
        return ClampToIntCount(count);
    }

    public void ApplyReservationOverlay(NetworkData network, NetworkInventorySnapshot snapshot)
    {
        foreach (var entry in snapshot.Entries)
            entry.ReservedCount = Math.Min(entry.TotalCount, (long)this.GetReservedCountForEntry(network, entry, Guid.Empty));
    }

    public int GetUnreservedCount(NetworkData network, NetworkItemRequest request, Guid excludedReservationJobId = default, MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst, bool autoConsumableOnly = false)
    {
        return Math.Max(0, this.GetAvailableCount(network, request, qualityStrategy, autoConsumableOnly) - this.GetReservedCount(network, request, excludedReservationJobId));
    }

    public bool HasIngredients(NetworkData network, IReadOnlyList<NetworkItemRequest> requests, out List<string> missingLines, Guid excludedReservationJobId = default, MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst, bool autoConsumableOnly = false)
    {
        missingLines = new List<string>();

        foreach (var request in requests)
        {
            var available = this.GetUnreservedCount(network, request, excludedReservationJobId, qualityStrategy, autoConsumableOnly);

            if (available < request.Count)
                missingLines.Add($"{request.DisplayKey}: {available:N0}/{request.Count:N0}");
        }

        return missingLines.Count == 0;
    }

    public bool TryConsumeIngredients(NetworkData network, IReadOnlyList<NetworkItemRequest> requests, out string message, Guid excludedReservationJobId = default, MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst)
    {
        if (!this.HasIngredients(network, requests, out var missing, excludedReservationJobId, qualityStrategy, autoConsumableOnly: true))
        {
            message = "缺少：" + string.Join(", ", missing);
            return false;
        }

        var rollbackCells = new List<CellRollback>();
        var rollbackChests = new List<ChestRollback>();
        foreach (var request in requests)
        {
            var remaining = request.Count;
            var snapshot = this.scanner.Scan(network);
            var ordered = snapshot.Entries
                .Where(entry => MatchesRequest(entry, request))
                .Where(entry => CanAutoConsume(entry.Prototype))
                .Where(entry => AllowsQuality(entry, qualityStrategy));
            var entries = qualityStrategy == MaterialQualityStrategy.HighQualityFirst
                ? ordered
                    .OrderByDescending(entry => entry.Key.Quality)
                    .ThenByDescending(entry => entry.TotalCount)
                    .ToList()
                : ordered
                    .OrderBy(entry => entry.Key.Quality)
                    .ThenByDescending(entry => entry.TotalCount)
                    .ToList();

            foreach (var entry in entries)
            {
                if (remaining <= 0)
                    break;

                var take = Math.Min(remaining, ClampToIntCount(entry.TotalCount));
                var removed = this.RemoveFromCellSources(network, entry, take, out var cellRollbacks);
                rollbackCells.AddRange(cellRollbacks);
                if (removed < take)
                {
                    removed += this.RemoveFromChestSources(entry, take - removed, out var chestRollbacks, autoConsumableOnly: true);
                    rollbackChests.AddRange(chestRollbacks);
                }

                remaining -= removed;
            }

            if (remaining > 0)
            {
                RollbackCells(rollbackCells);
                RollbackChests(rollbackChests);
                AbortCellTransactions(rollbackCells);
                message = $"消耗 {request.DisplayKey} 时库存发生变化。";
                return false;
            }
        }

        message = "已消耗合成材料。";
        CommitCellTransactions(rollbackCells);
        ReleaseChestLocks(rollbackChests);
        return true;
    }

    private int RemoveFromChestSources(NetworkInventoryEntry entry, int count, out List<ChestRollback> rollbackChests, bool autoConsumableOnly = false)
    {
        rollbackChests = new List<ChestRollback>();
        var remaining = count;
        var removed = 0;

        foreach (var stackLocation in entry.Locations.Where(location => location.SourceKind == InventorySourceKind.Chest))
        {
            if (remaining <= 0)
                break;

            var location = this.scanner.GetLocation(stackLocation.LocationName);
            if (location is null)
                continue;

            if (!location.objects.TryGetValue(new Microsoft.Xna.Framework.Vector2(stackLocation.TileX, stackLocation.TileY), out SObject? placedObject)
                || placedObject is not Chest chest
                || stackLocation.SlotIndex < 0
                || stackLocation.SlotIndex >= chest.Items.Count)
            {
                continue;
            }

            if (!ChestMutexHelper.TryAcquireImmediate(chest, out var chestLock))
                continue;

            var keepLock = false;
            try
            {
                var item = chest.Items[stackLocation.SlotIndex];
                if (item is null || item.Stack <= 0 || !item.canStackWith(entry.Prototype))
                    continue;

                if (autoConsumableOnly && !CanAutoConsume(item))
                    continue;

                var take = Math.Min(remaining, item.Stack);
                rollbackChests.Add(new ChestRollback(chest, stackLocation.SlotIndex, CloneStackForRollback(item), chestLock));
                keepLock = true;
                item.Stack -= take;
                if (item.Stack <= 0)
                    chest.Items[stackLocation.SlotIndex] = null;

                remaining -= take;
                removed += take;
            }
            finally
            {
                if (!keepLock)
                    chestLock.Release();
            }
        }

        return removed;
    }

    private int RemoveFromCellSources(NetworkData network, NetworkInventoryEntry entry, int count, out List<CellRollback> rollbackCells)
    {
        rollbackCells = new List<CellRollback>();
        var remaining = count;
        var removed = 0;

        foreach (var stackLocation in entry.Locations.Where(location => location.SourceKind == InventorySourceKind.StorageCell))
        {
            if (remaining <= 0)
                break;

            var slot = this.FindSlot(network, stackLocation.EndpointId, stackLocation.CellId);
            if (slot is null || !StorageCellCodec.TryReadCellData(slot, out var data))
                continue;

            var stack = data.Items.FirstOrDefault(candidate =>
            {
                if (!this.TryCreateStoredItemPrototype(candidate, out var prototype))
                    return false;

                return ItemKeyFactory.SameStackBucket(candidate.Key, prototype, entry.Key, entry.Prototype);
            });

            if (stack is null || stack.Count <= 0)
                continue;

            var oldRaw = slot.ModData.GetValueOrDefault(StorageCellInitializer.CellDataKey) ?? string.Empty;
            var take = Math.Min(remaining, stack.Count);
            var tx = this.Prepare(network.NetworkId, TxKind.CellWithdraw, entry.Prototype, take, $"Cell:{slot.CellId:N}", slot.CellId, oldRaw);

            stack.Count -= take;
            if (stack.Count <= 0)
                data.Items.Remove(stack);

            data.CapacityUsed = StorageCellTierInfo.CalculateUsedBytes(data.Items);
            StorageCellCodec.WriteCellData(slot, data);
            this.MarkCellApplied(tx, slot);

            rollbackCells.Add(new CellRollback(slot, oldRaw, tx));
            remaining -= take;
            removed += take;
        }

        return removed;
    }

    private IEnumerable<Chest> GetWritableChests(NetworkData network)
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in network.Endpoints.Where(endpoint => endpoint.Active && (endpoint.Type == EndpointType.Chest || endpoint.Type == EndpointType.StorageInterface)))
        {
            var location = this.scanner.GetLocation(endpoint.LocationName);
            if (location is null)
                continue;

            var endpointTile = new Vector2(endpoint.TileX, endpoint.TileY);
            if (endpoint.Type == EndpointType.Chest)
            {
                if (!TryGetWritableChest(location, endpointTile, out var chest))
                    continue;

                if (!yielded.Add(GetChestKey(location, endpointTile)))
                    continue;

                yield return chest;
                continue;
            }

            foreach (var chestInfo in GetAdjacentWritableChests(location, endpointTile))
            {
                if (!yielded.Add(GetChestKey(location, chestInfo.Tile)))
                    continue;

                yield return chestInfo.Chest;
            }
        }
    }

    private int TryDepositStackIntoChests(IEnumerable<Chest> chests, Item source)
    {
        var before = source.Stack;

        foreach (var chest in chests)
        {
            if (source.Stack <= 0)
                break;

            if (!ChestMutexHelper.TryAcquireImmediate(chest, out var chestLock))
                continue;

            try
            {
                var toInsert = source.getOne();
                toInsert.Stack = source.Stack;
                var leftover = chest.addItem(toInsert);
                var accepted = source.Stack - (leftover?.Stack ?? 0);
                if (accepted > 0)
                    source.Stack -= accepted;
            }
            finally
            {
                chestLock.Release();
            }
        }

        return before - source.Stack;
    }

    private static bool IsChestLocked(Chest chest)
    {
        return ChestMutexHelper.IsLockedByAnotherActor(chest);
    }

    private static bool TryGetWritableChest(GameLocation location, Vector2 tile, out Chest chest)
    {
        chest = null!;
        if (!location.objects.TryGetValue(tile, out SObject? placedObject) || placedObject is not Chest found)
            return false;

        if (IsChestLocked(found))
            return false;

        chest = found;
        return true;
    }

    private static IEnumerable<(Vector2 Tile, Chest Chest)> GetAdjacentWritableChests(GameLocation location, Vector2 origin)
    {
        foreach (var offset in AdjacentOffsets)
        {
            var tile = origin + offset;
            if (TryGetWritableChest(location, tile, out var chest))
                yield return (tile, chest);
        }
    }

    private static string GetChestKey(GameLocation location, Vector2 tile)
    {
        return $"{location.NameOrUniqueName}:{tile.X:0}:{tile.Y:0}";
    }

    private static void RollbackCells(List<CellRollback> rollbackCells)
    {
        for (var i = rollbackCells.Count - 1; i >= 0; i--)
        {
            var rollback = rollbackCells[i];
            rollback.Slot.ModData[StorageCellInitializer.CellDataKey] = rollback.RawCellData;
        }
    }

    private static void RollbackChests(List<ChestRollback> rollbackChests)
    {
        for (var i = rollbackChests.Count - 1; i >= 0; i--)
        {
            var rollback = rollbackChests[i];
            try
            {
                if (rollback.SlotIndex < 0 || rollback.SlotIndex >= rollback.Chest.Items.Count)
                    continue;

                rollback.Chest.Items[rollback.SlotIndex] = rollback.Item;
            }
            finally
            {
                rollback.Lock.Release();
            }
        }
    }

    private static Item CloneStackForRollback(Item item)
    {
        var copy = item.getOne();
        copy.Stack = item.Stack;
        return copy;
    }

    private static bool MatchesRequest(NetworkInventoryEntry entry, NetworkItemRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SerializedItemPrototype))
        {
            try
            {
                var prototype = SerializedItemCodec.CreateItem(request.SerializedItemPrototype, 1);
                if (!entry.Prototype.canStackWith(prototype))
                    return false;
            }
            catch
            {
                return false;
            }
        }

        if (request.QualifiedItemId is not null && entry.Key.QualifiedItemId != request.QualifiedItemId)
            return false;

        if (request.Category is not null
            && (entry.Prototype is not SObject obj || obj.Category != request.Category.Value))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.PreservedParentQualifiedItemId)
            && ItemKeyFactory.NormalizeItemId(entry.Key.PreservedParentSheetIndex) != ItemKeyFactory.NormalizeItemId(request.PreservedParentQualifiedItemId))
        {
            return false;
        }

        return request.QualifiedItemId is not null
            || request.Category is not null
            || !string.IsNullOrWhiteSpace(request.SerializedItemPrototype)
            || !string.IsNullOrWhiteSpace(request.PreservedParentQualifiedItemId);
    }

    private static bool AllowsQuality(NetworkInventoryEntry entry, MaterialQualityStrategy strategy)
    {
        return strategy != MaterialQualityStrategy.PreserveGoldIridium
            || entry.Key.Quality is not (2 or 4);
    }

    private int GetReservedCount(NetworkData network, NetworkItemRequest request, Guid excludedJobId)
    {
        return network.Jobs
            .Where(job => job.JobId != excludedJobId && IsOpenState(job.State))
            .SelectMany(job => job.Reservations)
            .Where(reservation => RequestsOverlap(reservation.Request, request))
            .Sum(reservation => Math.Max(0, reservation.Count - reservation.ConsumedCount));
    }

    private int GetReservedCountForEntry(NetworkData network, NetworkInventoryEntry entry, Guid excludedJobId)
    {
        return network.Jobs
            .Where(job => job.JobId != excludedJobId && IsOpenState(job.State))
            .SelectMany(job => job.Reservations)
            .Where(reservation => MatchesRequest(entry, reservation.Request))
            .Sum(reservation => Math.Max(0, reservation.Count - reservation.ConsumedCount));
    }

    private static int ClampToIntCount(long count)
    {
        if (count <= 0)
            return 0;

        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private static bool SameRequest(NetworkItemRequest left, NetworkItemRequest right)
    {
        if (!string.Equals(left.SerializedItemPrototype, right.SerializedItemPrototype, StringComparison.Ordinal))
            return false;

        if (!string.Equals(left.QualifiedItemId, right.QualifiedItemId, StringComparison.Ordinal))
            return false;

        if (left.Category != right.Category)
            return false;

        return ItemKeyFactory.NormalizeItemId(left.PreservedParentQualifiedItemId) == ItemKeyFactory.NormalizeItemId(right.PreservedParentQualifiedItemId);
    }

    private static bool RequestsOverlap(NetworkItemRequest left, NetworkItemRequest right)
    {
        var leftHasSerialized = !string.IsNullOrWhiteSpace(left.SerializedItemPrototype);
        var rightHasSerialized = !string.IsNullOrWhiteSpace(right.SerializedItemPrototype);
        if (leftHasSerialized && rightHasSerialized
            && !string.Equals(left.SerializedItemPrototype, right.SerializedItemPrototype, StringComparison.Ordinal))
        {
            return false;
        }

        var leftHasQualifiedId = !string.IsNullOrWhiteSpace(left.QualifiedItemId);
        var rightHasQualifiedId = !string.IsNullOrWhiteSpace(right.QualifiedItemId);
        if (leftHasQualifiedId && rightHasQualifiedId
            && !string.Equals(left.QualifiedItemId, right.QualifiedItemId, StringComparison.Ordinal))
        {
            return false;
        }

        if (left.Category is not null && right.Category is not null && left.Category.Value != right.Category.Value)
            return false;

        var leftParent = ItemKeyFactory.NormalizeItemId(left.PreservedParentQualifiedItemId);
        var rightParent = ItemKeyFactory.NormalizeItemId(right.PreservedParentQualifiedItemId);
        if (leftParent.Length > 0 && rightParent.Length > 0 && leftParent != rightParent)
            return false;

        if (left.Category is not null && RequestCanMatchCategory(right, left.Category.Value))
            return true;

        if (right.Category is not null && RequestCanMatchCategory(left, right.Category.Value))
            return true;

        return (leftHasSerialized && rightHasSerialized)
            || (leftHasQualifiedId && rightHasQualifiedId)
            || (left.Category is not null && right.Category is not null);
    }

    private static bool RequestCanMatchCategory(NetworkItemRequest request, int category)
    {
        if (request.Category is not null)
            return request.Category.Value == category;

        try
        {
            Item? item = null;
            if (!string.IsNullOrWhiteSpace(request.SerializedItemPrototype))
                item = SerializedItemCodec.CreateItem(request.SerializedItemPrototype, 1);
            else if (!string.IsNullOrWhiteSpace(request.QualifiedItemId))
                item = ItemRegistry.Create(request.QualifiedItemId);

            return item is SObject obj && obj.Category == category;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOpenState(CraftingJobState state)
    {
        return state is CraftingJobState.Planning
            or CraftingJobState.MissingItems
            or CraftingJobState.Reserved
            or CraftingJobState.Running
            or CraftingJobState.WaitingForMachine
            or CraftingJobState.WaitingForOutput;
    }

    private static void RecordRecentAdded(NetworkData network, Item item)
    {
        var key = ItemKeyFactory.FromItem(item);
        network.RecentItems.RemoveAll(entry => ItemKeyFactory.SameDisplayBucket(entry.Key, key));
        network.RecentItems.Add(new NetworkRecentItemData
        {
            Key = key,
            LastAddedSequence = ++network.RecentSequence
        });

        if (network.RecentItems.Count > RecentItemLimit)
        {
            network.RecentItems = network.RecentItems
                .OrderByDescending(entry => entry.LastAddedSequence)
                .Take(RecentItemLimit)
                .OrderBy(entry => entry.LastAddedSequence)
                .ToList();
        }
    }

    private int TryDepositStackIntoStorageCells(NetworkData network, Item source)
    {
        if (!this.CanDigitize(source))
            return 0;

        var moved = 0;
        while (source.Stack > 0)
        {
            var target = this.FindDepositTarget(network, source);
            if (target is null)
                break;

            var (slot, data, stack) = target.Value;
            var capacityRemaining = StorageCellTierInfo.GetRemainingItemCapacity(
                data,
                stack,
                source.Stack,
                this.getConfig().MaxItemTypesPerStorageCell);
            if (capacityRemaining <= 0)
                break;

            var oldRaw = slot.ModData.GetValueOrDefault(StorageCellInitializer.CellDataKey) ?? string.Empty;
            var moveCount = Math.Min(source.Stack, capacityRemaining);
            var beforeStack = source.Stack;
            var tx = this.Prepare(network.NetworkId, TxKind.CellDeposit, source, moveCount, "Player", slot.CellId, oldRaw);

            try
            {
                if (stack is null)
                {
                    stack = new StoredItemStack
                    {
                        Key = ItemKeyFactory.FromItem(source),
                        Count = 0,
                        SerializedItemPrototype = SerializedItemCodec.SerializePrototype(source.getOne())
                    };
                    data.Items.Add(stack);
                }

                stack.Count += moveCount;
                data.CapacityUsed = StorageCellTierInfo.CalculateUsedBytes(data.Items);
                StorageCellCodec.WriteCellData(slot, data);
                this.MarkCellApplied(tx, slot);

                source.Stack -= moveCount;
                this.MarkCounterpartApplied(tx);
                moved += moveCount;
                this.Commit(tx);
            }
            catch (Exception ex)
            {
                slot.ModData[StorageCellInitializer.CellDataKey] = oldRaw;
                source.Stack = beforeStack;
                this.repository.Data.PendingTransactions.Remove(tx);
                this.monitor.Log($"Digital deposit transaction {tx.TxId:N} failed and was rolled back: {ex.Message}", LogLevel.Error);
                break;
            }
        }

        return moved;
    }

    private (StorageDriveSlotData Slot, StorageCellData Data, StoredItemStack? Stack)? FindDepositTarget(NetworkData network, Item source)
    {
        foreach (var slot in this.GetActiveStorageCellSlots(network))
        {
            if (!StorageCellCodec.TryReadCellData(slot, out var data))
                continue;

            var stack = data.Items.FirstOrDefault(candidate =>
            {
                if (!this.TryCreateStoredItemPrototype(candidate, out var prototype))
                    return false;

                return ItemKeyFactory.SameStackBucket(candidate.Key, prototype, ItemKeyFactory.FromItem(source), source);
            });

            if (stack is not null
                && StorageCellTierInfo.GetRemainingItemCapacity(data, stack, 1, this.getConfig().MaxItemTypesPerStorageCell) > 0)
            {
                return (slot, data, stack);
            }
        }

        foreach (var slot in this.GetActiveStorageCellSlots(network))
        {
            if (!StorageCellCodec.TryReadCellData(slot, out var data))
                continue;

            if (StorageCellTierInfo.GetRemainingItemCapacity(data, null, 1, this.getConfig().MaxItemTypesPerStorageCell) > 0)
                return (slot, data, null);
        }

        return null;
    }

    private IEnumerable<StorageDriveSlotData> GetActiveStorageCellSlots(NetworkData network)
    {
        foreach (var endpoint in network.Endpoints.Where(endpoint => endpoint.Active && endpoint.Type == EndpointType.StorageDrive))
        {
            if (!network.StorageDrives.TryGetValue(endpoint.EndpointId, out var drive))
                continue;

            foreach (var slot in drive.Slots.OrderBy(slot => slot.SlotIndex))
                yield return slot;
        }
    }

    private StorageDriveSlotData? FindSlot(NetworkData network, Guid endpointId, Guid cellId)
    {
        if (!network.StorageDrives.TryGetValue(endpointId, out var drive))
            return null;

        return drive.Slots.FirstOrDefault(slot => slot.CellId == cellId);
    }

    private TxLogRecord Prepare(Guid networkId, TxKind kind, Item item, int count, string sourceRef, Guid targetCellId, string cellDataBefore)
    {
        var tx = new TxLogRecord
        {
            TxId = Guid.NewGuid(),
            NetworkId = networkId,
            Kind = kind,
            State = TxState.Prepared,
            ApplyPhase = TxApplyPhase.Prepared,
            SerializedItem = SerializedItemCodec.SerializePrototype(item.getOne()),
            Count = count,
            SourceRef = sourceRef,
            TargetCellId = targetCellId,
            CellDataBefore = cellDataBefore
        };

        this.repository.Data.PendingTransactions.Add(tx);
        if (this.getConfig().DebugTransactionLogs)
        {
            this.monitor.Log(
                $"Prepared SVSAP {kind} transaction {tx.TxId:N}: network={networkId:N}, cell={targetCellId:N}, count={count:N0}, source={sourceRef}, item={item.QualifiedItemId}.",
                LogLevel.Trace);
        }

        return tx;
    }

    private void MarkCellApplied(TxLogRecord tx, StorageDriveSlotData slot)
    {
        tx.ApplyPhase = TxApplyPhase.CellApplied;
        tx.CellDataAfter = slot.ModData.GetValueOrDefault(StorageCellInitializer.CellDataKey) ?? string.Empty;
    }

    private void MarkCounterpartApplied(TxLogRecord tx)
    {
        tx.ApplyPhase = TxApplyPhase.CounterpartApplied;
    }

    private void Commit(TxLogRecord tx)
    {
        tx.State = TxState.Committed;
        if (this.getConfig().DebugTransactionLogs)
        {
            this.monitor.Log(
                $"Committed SVSAP {tx.Kind} transaction {tx.TxId:N}: network={tx.NetworkId:N}, cell={tx.TargetCellId:N}, count={tx.Count:N0}, source={tx.SourceRef}.",
                LogLevel.Trace);
        }

        this.repository.Data.PendingTransactions.Remove(tx);
    }

    private bool CanDigitize(Item item)
    {
        if (item is Tool || item is not SObject || item.Stack <= 0 || item.maximumStackSize() <= 1)
            return false;

        if (Content.ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out _))
            return false;

        return CanAutoConsume(item);
    }

    private static bool CanAutoConsume(Item item)
    {
        if (item is Tool || item is not SObject obj || item.Stack <= 0 || item.maximumStackSize() <= 1)
            return false;

        if (obj.questItem.Value)
            return false;

        if (Content.ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out _))
            return false;

        return PassesSerializationSafetyCheck(item);
    }

    private static bool PassesSerializationSafetyCheck(Item item)
    {
        try
        {
            var prototype = item.getOne();
            prototype.Stack = 1;
            var restored = SerializedItemCodec.CreateItem(SerializedItemCodec.SerializePrototype(prototype), 1);
            return prototype.canStackWith(restored) && restored.canStackWith(prototype);
        }
        catch
        {
            return false;
        }
    }

    private bool TryCreateStoredItemPrototype(StoredItemStack stack, out Item prototype)
    {
        prototype = null!;
        try
        {
            prototype = SerializedItemCodec.CreateItem(stack.SerializedItemPrototype, 1);
            return true;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Skipped unreadable storage cell stack prototype: {ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    private bool CanEnterNetwork(Item item)
    {
        if (Content.ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out _))
            return true;

        if (item is MeleeWeapon)
            return this.getConfig().AllowWeaponsInNetwork;

        if (item is Tool)
            return this.getConfig().AllowToolsInNetwork;

        if (item is SObject obj && obj.questItem.Value)
            return false;

        return true;
    }

    private static int GetInventoryAcceptCount(Farmer player, Item item, int requestedCount)
    {
        if (requestedCount <= 0)
            return 0;

        var remaining = requestedCount;
        var accepted = 0;
        var emptySlotCapacity = Math.Max(1, item.maximumStackSize());
        foreach (var slot in player.Items)
        {
            if (remaining <= 0)
                break;

            var space = 0;
            if (slot is null)
            {
                space = emptySlotCapacity;
            }
            else if (slot.canStackWith(item))
            {
                space = Math.Max(0, slot.maximumStackSize() - slot.Stack);
            }

            if (space <= 0)
                continue;

            var moved = Math.Min(remaining, space);
            accepted += moved;
            remaining -= moved;
        }

        return accepted;
    }

    private string RecoverPreparedTransaction(NetworkData network, TxLogRecord tx)
    {
        switch (tx.ApplyPhase)
        {
            case TxApplyPhase.Prepared:
                return "prepared transaction had no applied side effects";

            case TxApplyPhase.CellApplied:
                var slot = this.FindSlotByCellId(network, tx.TargetCellId);
                if (slot is null)
                    return "target cell is no longer inserted; discarded cell-applied log";

                if (string.IsNullOrWhiteSpace(tx.CellDataBefore))
                    return "cell-applied transaction had no pre-transaction snapshot; left saved cell state unchanged";

                slot.ModData[StorageCellInitializer.CellDataKey] = tx.CellDataBefore;
                return "restored target cell from a cell-only applied transaction";

            case TxApplyPhase.CounterpartApplied:
                return "both transaction sides were already applied; discarded stale pending log";

            case TxApplyPhase.Unknown:
            default:
                return "legacy prepared log lacks apply phase; left saved cell state unchanged to avoid unsafe one-sided recovery";
        }
    }

    private StorageDriveSlotData? FindSlotByCellId(NetworkData network, Guid cellId)
    {
        return this.GetStorageCellSlotsByCellId(network, cellId).FirstOrDefault();
    }

    private IEnumerable<StorageDriveSlotData> GetStorageCellSlotsByCellId(NetworkData network, Guid cellId)
    {
        foreach (var drive in network.StorageDrives.Values)
        {
            foreach (var slot in drive.Slots)
            {
                if (slot.CellId == cellId)
                    yield return slot;
            }
        }
    }

    private void CommitCellTransactions(List<CellRollback> rollbackCells)
    {
        foreach (var rollback in rollbackCells)
        {
            if (rollback.Transaction is null)
                continue;

            this.MarkCounterpartApplied(rollback.Transaction);
            this.Commit(rollback.Transaction);
        }
    }

    private void AbortCellTransactions(List<CellRollback> rollbackCells)
    {
        foreach (var rollback in rollbackCells)
        {
            if (rollback.Transaction is not null)
                this.repository.Data.PendingTransactions.Remove(rollback.Transaction);
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

    private static string Quote(string? value)
    {
        return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private sealed record CellRollback(StorageDriveSlotData Slot, string RawCellData, TxLogRecord? Transaction = null);
    private static void ReleaseChestLocks(List<ChestRollback> rollbackChests)
    {
        for (var i = rollbackChests.Count - 1; i >= 0; i--)
            rollbackChests[i].Lock.Release();
    }

    private sealed record ChestRollback(Chest Chest, int SlotIndex, Item Item, ChestMutexLease Lock);
}
