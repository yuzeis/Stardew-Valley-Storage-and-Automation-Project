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
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.patternProvider.notLinked"), HUDMessage.error_type));
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
            return StructuralActionResult.Fail(ModText.Get("ui.patternProvider.notProvider"));

        if (!Guid.TryParse(provider.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
            return StructuralActionResult.Fail(ModText.Get("ui.patternProvider.notLinked"));

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
                return StructuralActionResult.Fail(ModText.Get("ui.patternProvider.readFailed"));
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

    public IReadOnlyList<string> DescribeProvider(SObject provider)
    {
        if (!this.TryResolveProvider(provider, create: false, out _, out var providerData, out _, out var message))
            return new[] { message };

        if (providerData is null || providerData.Slots.Count == 0)
            return new[] { ModText.Get("ui.patternProvider.empty"), ModText.Get("ui.patternProvider.help") };

        var lines = new List<string>
        {
            ModText.Format("ui.patternProvider.inserted", providerData.Slots.Count, SlotCount)
        };
        foreach (var slot in providerData.Slots.OrderBy(slot => slot.SlotIndex))
            lines.Add(ModText.Format("ui.patternProvider.slot", slot.SlotIndex + 1, DescribePatternSlot(slot)));

        return lines;
    }

    public IReadOnlyList<int> GetOccupiedSlotIndexes(SObject provider)
    {
        return this.TryResolveProvider(provider, create: false, out _, out var providerData, out _, out _)
            && providerData is not null
            ? providerData.Slots.OrderBy(slot => slot.SlotIndex).Select(slot => slot.SlotIndex).ToList()
            : Array.Empty<int>();
    }

    public bool HasPatternSlot(SObject provider, int slotIndex)
    {
        return this.TryResolveProvider(provider, create: false, out _, out var providerData, out _, out _)
            && providerData is not null
            && providerData.Slots.Any(slot => slot.SlotIndex == slotIndex);
    }

    public bool TryEjectPatternSlot(SObject provider, int slotIndex, out string message)
    {
        if (!this.TryResolveProvider(provider, create: false, out _, out var providerData, out _, out message)
            || providerData is null)
        {
            return false;
        }

        var slot = providerData.Slots.FirstOrDefault(entry => entry.SlotIndex == slotIndex);
        if (slot is null)
        {
            message = ModText.Get("ui.patternProvider.slotEmpty");
            return false;
        }

        var item = PatternCodec.CreateItem(slot);
        if (!Game1.player.couldInventoryAcceptThisItem(item) || !Game1.player.addItemToInventoryBool(item))
        {
            message = ModText.Get("ui.patternProvider.inventoryFull");
            return false;
        }

        providerData.Slots.Remove(slot);
        this.repository.Save();
        message = ModText.Format("ui.patternProvider.ejectedSlot", slotIndex + 1);
        return true;
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

    private bool TryResolveProvider(
        SObject provider,
        bool create,
        out NetworkData network,
        out PatternProviderData? providerData,
        out Guid endpointId,
        out string message)
    {
        network = null!;
        providerData = null;
        endpointId = Guid.Empty;
        message = string.Empty;

        if (provider.QualifiedItemId != "(BC)" + ModItemCatalog.PatternProvider)
        {
            message = ModText.Get("ui.patternProvider.notProvider");
            return false;
        }

        if (!Guid.TryParse(provider.modData.GetValueOrDefault(EndpointIdentityService.NetworkIdKey), out var networkId))
        {
            message = ModText.Get("ui.patternProvider.notLinked");
            return false;
        }

        endpointId = this.endpointIdentityService.EnsureEndpointId(provider);
        network = this.repository.GetOrCreateNetwork(networkId);
        providerData = create
            ? this.GetOrCreateProvider(network, endpointId)
            : network.PatternProviders.GetValueOrDefault(endpointId);
        return true;
    }

    private StructuralActionResult InsertPatternIntoNetwork(PatternProviderData providerData, Item held)
    {
        if (providerData.Slots.Count >= SlotCount)
            return StructuralActionResult.Fail(ModText.Get("ui.patternProvider.full"));

        if (!PatternCodec.TryRead(held, out _))
            return StructuralActionResult.Fail(ModText.Get("ui.patternProvider.noPatternData"));

        var slotIndex = Enumerable.Range(0, SlotCount)
            .First(index => providerData.Slots.All(slot => slot.SlotIndex != index));
        providerData.Slots.Add(PatternCodec.ToSlotData(held, slotIndex));

        return new StructuralActionResult
        {
            Success = true,
            Message = ModText.Get("ui.patternProvider.insertedOne"),
            ConsumeHeldOne = true
        };
    }

    private StructuralActionResult EjectPatternFromNetwork(PatternProviderData providerData)
    {
        var slot = providerData.Slots.OrderByDescending(entry => entry.SlotIndex).FirstOrDefault();
        if (slot is null)
            return StructuralActionResult.Fail(ModText.Get("ui.patternProvider.empty"));

        var item = PatternCodec.CreateItem(slot);
        providerData.Slots.Remove(slot);

        return new StructuralActionResult
        {
            Success = true,
            Message = ModText.Get("ui.patternProvider.ejectedOne"),
            ReturnedSerializedItem = SerializedItemCodec.SerializePrototype(item)
        };
    }

    private void InsertPattern(PatternProviderData providerData, Item held)
    {
        if (providerData.Slots.Count >= SlotCount)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.patternProvider.full"), HUDMessage.error_type));
            return;
        }

        if (!PatternCodec.TryRead(held, out _))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.patternProvider.noPatternData"), HUDMessage.error_type));
            return;
        }

        var slotIndex = Enumerable.Range(0, SlotCount)
            .First(index => providerData.Slots.All(slot => slot.SlotIndex != index));
        providerData.Slots.Add(PatternCodec.ToSlotData(held, slotIndex));

        held.Stack -= 1;
        if (held.Stack <= 0)
            Game1.player.removeItemFromInventory(held);

        Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.patternProvider.insertedOne"), HUDMessage.newQuest_type));
    }

    private void EjectPattern(PatternProviderData providerData)
    {
        var slot = providerData.Slots.OrderByDescending(entry => entry.SlotIndex).FirstOrDefault();
        if (slot is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.patternProvider.empty"), HUDMessage.error_type));
            return;
        }

        var item = PatternCodec.CreateItem(slot);
        if (!Game1.player.couldInventoryAcceptThisItem(item) || !Game1.player.addItemToInventoryBool(item))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.patternProvider.inventoryFull"), HUDMessage.error_type));
            return;
        }

        providerData.Slots.Remove(slot);
        Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.patternProvider.ejectedOne"), HUDMessage.newQuest_type));
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

    private static string DescribePatternSlot(PatternSlotData slot)
    {
        var item = PatternCodec.CreateItem(slot);
        if (!PatternCodec.TryRead(item, out var pattern))
            return item.DisplayName;

        var kind = pattern.Kind == PatternKind.Crafting
            ? ModText.Get("ui.patternProvider.kind.crafting")
            : ModText.Get("ui.patternProvider.kind.processing");
        var machine = string.IsNullOrWhiteSpace(pattern.MachineQualifiedItemId)
            ? string.Empty
            : ModText.Format("ui.patternProvider.machineSuffix", pattern.MachineQualifiedItemId);
        return ModText.Format("ui.patternProvider.patternLine", kind, PatternDisplayNames.Get(pattern), machine);
    }
}
