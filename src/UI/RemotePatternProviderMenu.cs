using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemotePatternProviderMenu : IClickableMenu
{
    private const int SnapshotRefreshTicks = 30;
    private const int Pad = 24;
    private const int SlotCount = 36;
    private const int SlotColumns = 6;
    private const int SlotRows = 6;
    private const int Cell = 56;
    private const int InventoryCell = 48;
    private const int InventoryColumns = 12;

    private readonly Action<StructuralActionKind, int, int, Item?> sendAction;
    private readonly Action requestRefresh;
    private readonly Rectangle slotArea;
    private readonly Rectangle controlArea;
    private readonly Rectangle inventoryArea;
    private readonly ClickableComponent priorityUpButton;
    private readonly ClickableComponent priorityDownButton;
    private readonly ClickableComponent patternUpButton;
    private readonly ClickableComponent patternDownButton;
    private readonly ClickableComponent ejectButton;
    private int selectedSlot;
    private bool requestPending;
    private int snapshotAtTick;

    public RemotePatternProviderMenu(
        StructuralSnapshotResponseMessage snapshot,
        Action<StructuralActionKind, int, int, Item?> sendAction,
        Action requestRefresh)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.Snapshot = snapshot;
        this.sendAction = sendAction;
        this.requestRefresh = requestRefresh;
        this.snapshotAtTick = Game1.ticks;
        this.slotArea = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 96, SlotColumns * Cell, SlotRows * Cell);
        this.inventoryArea = new Rectangle(
            this.xPositionOnScreen + (this.width - InventoryColumns * InventoryCell) / 2,
            this.yPositionOnScreen + this.height - Pad - 3 * InventoryCell,
            InventoryColumns * InventoryCell,
            3 * InventoryCell);
        this.controlArea = new Rectangle(
            this.slotArea.Right + 18,
            this.slotArea.Y,
            this.xPositionOnScreen + this.width - Pad - this.slotArea.Right - 18,
            this.inventoryArea.Y - this.slotArea.Y - 18);

        var buttonWidth = Math.Max(96, (this.controlArea.Width - 30) / 2);
        var x = this.controlArea.X + 10;
        var y = this.controlArea.Y + 44;
        this.priorityUpButton = new ClickableComponent(new Rectangle(x, y, buttonWidth, 38), "priority_up", ModText.Get("ui.patternProvider.priorityUp"));
        this.priorityDownButton = new ClickableComponent(new Rectangle(x + buttonWidth + 10, y, buttonWidth, 38), "priority_down", ModText.Get("ui.patternProvider.priorityDown"));
        this.patternUpButton = new ClickableComponent(new Rectangle(x, y + 48, buttonWidth, 38), "pattern_up", ModText.Get("ui.patternProvider.patternUp"));
        this.patternDownButton = new ClickableComponent(new Rectangle(x + buttonWidth + 10, y + 48, buttonWidth, 38), "pattern_down", ModText.Get("ui.patternProvider.patternDown"));
        this.ejectButton = new ClickableComponent(new Rectangle(x, y + 96, this.controlArea.Width - 20, 40), "eject", ModText.Get("ui.patternProvider.ejectSelected"));
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    internal StructuralSnapshotResponseMessage Snapshot { get; private set; }

    public void ApplySnapshot(StructuralSnapshotResponseMessage snapshot)
    {
        this.requestPending = false;
        this.snapshotAtTick = Game1.ticks;
        if (snapshot.Kind == StructuralSnapshotKind.PatternProvider && snapshot.PatternProvider is not null)
            this.Snapshot = snapshot;
    }

    public override void update(GameTime time)
    {
        base.update(time);
        var tick = Game1.ticks;
        if (this.requestPending || (tick >= this.snapshotAtTick && tick - this.snapshotAtTick < SnapshotRefreshTicks))
            return;

        this.snapshotAtTick = tick;
        this.requestRefresh();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        if (this.requestPending)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.requestPending"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        if (this.priorityUpButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.PatternProviderAdjustPriority, value: -1); return; }
        if (this.priorityDownButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.PatternProviderAdjustPriority, value: 1); return; }
        if (this.patternUpButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.PatternProviderMoveSlot, this.selectedSlot, -1); return; }
        if (this.patternDownButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.PatternProviderMoveSlot, this.selectedSlot, 1); return; }
        if (this.ejectButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.PatternProviderEjectSlot, this.selectedSlot); return; }

        var slotIndex = this.HitSlot(x, y);
        if (slotIndex >= 0)
        {
            this.selectedSlot = slotIndex;
            Game1.playSound("smallSelect");
            return;
        }

        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0)
            this.InsertFromInventory(inventoryIndex);
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        if (this.requestPending)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.requestPending"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        var slotIndex = this.HitSlot(x, y);
        if (slotIndex >= 0)
        {
            this.selectedSlot = slotIndex;
            this.RunAction(StructuralActionKind.PatternProviderEjectSlot, slotIndex);
            return;
        }

        this.requestPending = true;
        this.snapshotAtTick = Game1.ticks;
        this.requestRefresh();
        Game1.playSound("shwip");
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        Utility.drawTextWithShadow(
            b,
            this.Snapshot.DisplayName,
            Game1.dialogueFont,
            new Vector2(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 26),
            Game1.textColor);

        if (this.requestPending)
            b.DrawString(Game1.smallFont, ModText.Get("remoteTerminal.pendingInline"), new Vector2(this.xPositionOnScreen + Pad + 320, this.yPositionOnScreen + 34), Color.Firebrick);

        var slots = this.Snapshot.PatternProvider?.Slots ?? new List<RemotePatternProviderSlotMessage>();
        this.DrawPatternSlots(b, slots);
        this.DrawControls(b, slots);
        SVSAPMenuWidgets.DrawSeparator(b, new Rectangle(this.xPositionOnScreen + Pad, this.inventoryArea.Y - 12, this.width - Pad * 2, 2));
        this.DrawInventory(b);
        this.DrawHoverTooltip(b, slots);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    private void DrawPatternSlots(SpriteBatch b, IReadOnlyList<RemotePatternProviderSlotMessage> slots)
    {
        for (var index = 0; index < SlotCount; index++)
        {
            var bounds = this.GetSlotBounds(index);
            var slot = slots.FirstOrDefault(candidate => candidate.SlotIndex == index);
            SVSAPMenuWidgets.DrawSlotBackground(b, bounds, slot is null);
            SVSAPMenuWidgets.DrawSlotStatusLine(b, bounds, slot is null ? PixelStatus.Idle : PixelStatus.Ready);
            if (index == this.selectedSlot)
                DrawSelection(b, bounds);
            if (slot is not null && TryCreatePatternItem(slot, out var item))
                item.drawInMenu(b, new Vector2(bounds.X + 4, bounds.Y + 4), 0.72f, 1f, 0.86f, StackDrawType.Hide, Color.White, true);
        }
    }

    private void DrawControls(SpriteBatch b, IReadOnlyList<RemotePatternProviderSlotMessage> slots)
    {
        SVSAPMenuWidgets.DrawInsetBox(b, this.controlArea);
        SVSAPMenuWidgets.DrawFittedLine(
            b,
            ModText.Format("ui.patternProvider.priority", this.Snapshot.PatternProvider?.Priority ?? 0),
            new Rectangle(this.controlArea.X + 12, this.controlArea.Y + 10, this.controlArea.Width - 24, 26),
            Game1.textColor);

        var selected = slots.FirstOrDefault(slot => slot.SlotIndex == this.selectedSlot);
        SVSAPMenuWidgets.DrawButton(b, this.priorityUpButton);
        SVSAPMenuWidgets.DrawButton(b, this.priorityDownButton);
        SVSAPMenuWidgets.DrawButton(b, this.patternUpButton, tint: selected is null ? Color.Gray : Color.White);
        SVSAPMenuWidgets.DrawButton(b, this.patternDownButton, tint: selected is null ? Color.Gray : Color.White);
        SVSAPMenuWidgets.DrawButton(b, this.ejectButton, tint: selected is null ? Color.Gray : Color.White);

        var y = this.ejectButton.bounds.Bottom + 16;
        var lines = new List<string>();
        if (selected is null)
        {
            lines.Add(ModText.Get("ui.patternProvider.selectedEmpty"));
        }
        else
        {
            lines.Add(ModText.Format("ui.patternProvider.selectedSlot", selected.SlotIndex + 1));
            lines.Add(selected.DisplayName);
            if (TryCreatePatternItem(selected, out var item) && PatternCodec.TryRead(item, out var pattern))
            {
                lines.Add(pattern.Kind == PatternKind.Crafting
                    ? ModText.Get("ui.patternProvider.kind.crafting")
                    : ModText.Get("ui.patternProvider.kind.processing"));
            }
        }
        lines.Add(ModText.Get("ui.patternProvider.orderRule"));
        SVSAPMenuWidgets.DrawFittedLines(
            b,
            lines,
            new Rectangle(this.controlArea.X + 12, y, this.controlArea.Width - 24, Math.Max(24, this.controlArea.Bottom - y - 10)),
            Game1.textColor);
    }

    private void DrawInventory(SpriteBatch b)
    {
        for (var index = 0; index < Math.Min(36, Game1.player.Items.Count); index++)
        {
            var bounds = this.GetInventorySlotBounds(index);
            SVSAPMenuWidgets.DrawSlotBackground(b, bounds, Game1.player.Items[index] is null);
            Game1.player.Items[index]?.drawInMenu(b, new Vector2(bounds.X + 4, bounds.Y + 4), 0.68f, 1f, 0.86f, StackDrawType.Draw, Color.White, true);
        }
    }

    private void InsertFromInventory(int inventoryIndex)
    {
        if (inventoryIndex < 0 || inventoryIndex >= Game1.player.Items.Count || Game1.player.Items[inventoryIndex] is not Item item)
            return;
        if (!PatternCodec.IsPatternItem(item) || !PatternCodec.TryRead(item, out _))
        {
            Game1.playSound("cancel");
            return;
        }

        var occupied = (this.Snapshot.PatternProvider?.Slots ?? new List<RemotePatternProviderSlotMessage>())
            .Select(slot => slot.SlotIndex)
            .ToHashSet();
        var target = occupied.Contains(this.selectedSlot)
            ? Enumerable.Range(0, SlotCount).FirstOrDefault(index => !occupied.Contains(index), -1)
            : this.selectedSlot;
        if (target < 0)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.patternProvider.full"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        var oldToolIndex = Game1.player.CurrentToolIndex;
        Game1.player.CurrentToolIndex = inventoryIndex;
        try
        {
            this.RunAction(StructuralActionKind.PatternProviderInsertSlot, target, heldItem: item);
        }
        finally
        {
            Game1.player.CurrentToolIndex = oldToolIndex;
        }
    }

    private void RunAction(StructuralActionKind kind, int slotIndex = -1, int value = 0, Item? heldItem = null)
    {
        var selectedExists = this.Snapshot.PatternProvider?.Slots.Any(slot => slot.SlotIndex == slotIndex) == true;
        if (kind is StructuralActionKind.PatternProviderEjectSlot or StructuralActionKind.PatternProviderMoveSlot && !selectedExists)
        {
            Game1.playSound("cancel");
            return;
        }

        this.requestPending = true;
        this.snapshotAtTick = Game1.ticks;
        this.sendAction(kind, slotIndex, value, heldItem);
        Game1.playSound("smallSelect");
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<RemotePatternProviderSlotMessage> slots)
    {
        var x = Game1.getMouseX();
        var y = Game1.getMouseY();
        var slotIndex = this.HitSlot(x, y);
        if (slotIndex >= 0)
        {
            var slot = slots.FirstOrDefault(candidate => candidate.SlotIndex == slotIndex);
            if (slot is not null && TryCreatePatternItem(slot, out var item) && PatternCodec.TryRead(item, out var pattern))
            {
                var kind = pattern.Kind == PatternKind.Crafting
                    ? ModText.Get("ui.patternProvider.kind.crafting")
                    : ModText.Get("ui.patternProvider.kind.processing");
                var machine = string.IsNullOrWhiteSpace(pattern.MachineQualifiedItemId)
                    ? ModText.Get("ui.patternProvider.machine.crafting")
                    : FormatItem(pattern.MachineQualifiedItemId);
                SVSAPMenuWidgets.DrawTooltipBox(
                    b,
                    x + 28,
                    y + 28,
                    PatternDisplayNames.Get(pattern),
                    new[] { kind, ModText.Format("ui.patternProvider.machine", machine) });
            }
            return;
        }

        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0 && Game1.player.Items[inventoryIndex] is Item inventoryItem)
            SVSAPMenuWidgets.DrawTooltipBox(b, x + 28, y + 28, inventoryItem.DisplayName, new[] { inventoryItem.getDescription() });
    }

    private int HitSlot(int x, int y)
    {
        if (!this.slotArea.Contains(x, y))
            return -1;
        var column = (x - this.slotArea.X) / Cell;
        var row = (y - this.slotArea.Y) / Cell;
        var index = row * SlotColumns + column;
        return index is >= 0 and < SlotCount ? index : -1;
    }

    private Rectangle GetSlotBounds(int index)
    {
        return new Rectangle(
            this.slotArea.X + index % SlotColumns * Cell,
            this.slotArea.Y + index / SlotColumns * Cell,
            Cell - 4,
            Cell - 4);
    }

    private int HitInventorySlot(int x, int y)
    {
        if (!this.inventoryArea.Contains(x, y))
            return -1;
        var index = (y - this.inventoryArea.Y) / InventoryCell * InventoryColumns + (x - this.inventoryArea.X) / InventoryCell;
        return index >= 0 && index < Math.Min(36, Game1.player.Items.Count) ? index : -1;
    }

    private Rectangle GetInventorySlotBounds(int index)
    {
        return new Rectangle(
            this.inventoryArea.X + index % InventoryColumns * InventoryCell,
            this.inventoryArea.Y + index / InventoryColumns * InventoryCell,
            InventoryCell - 4,
            InventoryCell - 4);
    }

    private static bool TryCreatePatternItem(RemotePatternProviderSlotMessage slot, out Item item)
    {
        try
        {
            item = SerializedItemCodec.CreateItem(slot.SerializedItem, 1);
            return true;
        }
        catch
        {
            item = null!;
            return false;
        }
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

    private static void DrawSelection(SpriteBatch b, Rectangle bounds)
    {
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 2, bounds.Height), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 2, bounds.Y, 2, bounds.Height), Color.Gold);
    }

    private static int GetMenuWidth() => Math.Min(860, Game1.uiViewport.Width - 48);
    private static int GetMenuHeight() => Math.Min(680, Game1.uiViewport.Height - 48);
}
