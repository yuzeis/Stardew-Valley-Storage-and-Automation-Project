using SVSAP.Content;
using SVSAP.Models;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAP.Services;

internal sealed class PatternProviderService
{
    private const int SlotCount = 36;

    private readonly NetworkRepository repository;
    private readonly EndpointIdentityService endpointIdentityService;

    public PatternProviderService(NetworkRepository repository, EndpointIdentityService endpointIdentityService)
    {
        this.repository = repository;
        this.endpointIdentityService = endpointIdentityService;
    }

    public bool TryHandlePatternProviderAction(SObject provider)
    {
        if (provider.QualifiedItemId != "(BC)" + ModItemCatalog.PatternProvider)
            return false;

        if (!Guid.TryParse(provider.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
        {
            Game1.addHUDMessage(new HUDMessage("请先把这个样板供应器连接到网络。", HUDMessage.error_type));
            return true;
        }

        var endpointId = this.endpointIdentityService.EnsureEndpointId(provider);
        var network = this.repository.GetOrCreateNetwork(networkId);
        var providerData = this.GetOrCreateProvider(network, endpointId);
        var held = Game1.player.CurrentItem;

        if (PatternCodec.IsPatternItem(held))
        {
            this.InsertPattern(providerData, held!);
            this.repository.Save();
            return true;
        }

        this.EjectPattern(providerData);
        this.repository.Save();
        return true;
    }

    public StructuralActionResult ApplyInteract(SObject provider, string heldQualifiedItemId, string heldSerializedItem)
    {
        if (provider.QualifiedItemId != "(BC)" + ModItemCatalog.PatternProvider)
            return StructuralActionResult.Fail("目标不是样板供应器。");

        if (!Guid.TryParse(provider.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
            return StructuralActionResult.Fail("请先把这个样板供应器连接到网络。");

        var endpointId = this.endpointIdentityService.EnsureEndpointId(provider);
        var network = this.repository.GetOrCreateNetwork(networkId);
        var providerData = this.GetOrCreateProvider(network, endpointId);

        if (!string.IsNullOrWhiteSpace(heldSerializedItem)
            && (heldQualifiedItemId is "(O)" + ModItemCatalog.CraftingPattern or "(O)" + ModItemCatalog.ProcessingPattern))
        {
            Item patternItem;
            try
            {
                patternItem = SerializedItemCodec.CreateItem(heldSerializedItem, 1);
            }
            catch
            {
                return StructuralActionResult.Fail("无法读取这个样板物品。");
            }

            var inserted = this.InsertPatternIntoNetwork(providerData, patternItem);
            if (inserted.Success)
                this.repository.Save();

            return inserted;
        }

        var ejected = this.EjectPatternFromNetwork(providerData);
        if (ejected.Success)
            this.repository.Save();

        return ejected;
    }

    public void HandlePatternProviderRemoved(SObject provider, GameLocation location, Vector2 tile)
    {
        if (provider.QualifiedItemId != "(BC)" + ModItemCatalog.PatternProvider)
            return;

        if (!Guid.TryParse(provider.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId)
            || !Guid.TryParse(provider.modData.GetValueOrDefault(EndpointIdentityService.EndpointIdKey), out var endpointId)
            || !this.repository.TryGetNetwork(networkId, out var network))
        {
            return;
        }

        if (network.PatternProviders.TryGetValue(endpointId, out var providerData))
        {
            foreach (var slot in providerData.Slots.OrderBy(slot => slot.SlotIndex).ToList())
                this.ReturnPatternItem(PatternCodec.CreateItem(slot), location, tile, preferPlayerInventory: !Context.IsMultiplayer);

            network.PatternProviders.Remove(endpointId);
        }

        network.Endpoints.RemoveAll(endpoint => endpoint.EndpointId == endpointId);
        this.repository.Save();
    }

    private PatternProviderData GetOrCreateProvider(NetworkData network, Guid endpointId)
    {
        if (!network.PatternProviders.TryGetValue(endpointId, out var providerData))
        {
            providerData = new PatternProviderData { EndpointId = endpointId };
            network.PatternProviders[endpointId] = providerData;
        }

        return providerData;
    }

    private StructuralActionResult InsertPatternIntoNetwork(PatternProviderData providerData, Item held)
    {
        if (providerData.Slots.Count >= SlotCount)
            return StructuralActionResult.Fail("样板供应器已满。");

        if (!PatternCodec.TryRead(held, out _))
            return StructuralActionResult.Fail("这个样板物品没有编码数据。");

        var slotIndex = Enumerable.Range(0, SlotCount)
            .First(index => providerData.Slots.All(slot => slot.SlotIndex != index));
        providerData.Slots.Add(PatternCodec.ToSlotData(held, slotIndex));

        return new StructuralActionResult
        {
            Success = true,
            Message = "已插入样板。",
            ConsumeHeldOne = true
        };
    }

    private StructuralActionResult EjectPatternFromNetwork(PatternProviderData providerData)
    {
        var slot = providerData.Slots.OrderByDescending(entry => entry.SlotIndex).FirstOrDefault();
        if (slot is null)
            return StructuralActionResult.Fail("样板供应器为空。");

        var item = PatternCodec.CreateItem(slot);
        providerData.Slots.Remove(slot);

        return new StructuralActionResult
        {
            Success = true,
            Message = "已弹出样板。",
            ReturnedSerializedItem = SerializedItemCodec.SerializePrototype(item)
        };
    }

    private void InsertPattern(PatternProviderData providerData, Item held)
    {
        if (providerData.Slots.Count >= SlotCount)
        {
            Game1.addHUDMessage(new HUDMessage("样板供应器已满。", HUDMessage.error_type));
            return;
        }

        if (!PatternCodec.TryRead(held, out _))
        {
            Game1.addHUDMessage(new HUDMessage("这个样板物品没有编码数据。", HUDMessage.error_type));
            return;
        }

        var slotIndex = Enumerable.Range(0, SlotCount)
            .First(index => providerData.Slots.All(slot => slot.SlotIndex != index));
        providerData.Slots.Add(PatternCodec.ToSlotData(held, slotIndex));

        held.Stack -= 1;
        if (held.Stack <= 0)
            Game1.player.removeItemFromInventory(held);

        Game1.addHUDMessage(new HUDMessage("已插入样板。", HUDMessage.newQuest_type));
    }

    private void EjectPattern(PatternProviderData providerData)
    {
        var slot = providerData.Slots.OrderByDescending(entry => entry.SlotIndex).FirstOrDefault();
        if (slot is null)
        {
            Game1.addHUDMessage(new HUDMessage("样板供应器为空。", HUDMessage.error_type));
            return;
        }

        var item = PatternCodec.CreateItem(slot);
        if (!Game1.player.couldInventoryAcceptThisItem(item) || !Game1.player.addItemToInventoryBool(item))
        {
            Game1.addHUDMessage(new HUDMessage("背包已满。", HUDMessage.error_type));
            return;
        }

        providerData.Slots.Remove(slot);
        Game1.addHUDMessage(new HUDMessage("已弹出样板。", HUDMessage.newQuest_type));
    }

    private void ReturnPatternItem(Item item, GameLocation location, Vector2 tile, bool preferPlayerInventory)
    {
        if (preferPlayerInventory
            && Game1.player.couldInventoryAcceptThisItem(item)
            && Game1.player.addItemToInventoryBool(item))
        {
            return;
        }

        Game1.createItemDebris(item, (tile + new Vector2(0.5f, 0.5f)) * Game1.tileSize, -1, location);
    }
}
