using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SVSAP.UI;

internal sealed class PatternProviderMenu : IClickableMenu
{
    private const int Pad = 24;
    private const int SlotCount = 36;
    private const int SlotColumns = 6;
    private const int SlotRows = 6;
    private const int Cell = 56;
    private const int InventoryCell = 48;
    private const int InventoryColumns = 12;
    private const int ViewRefreshTicks = 30;

    private readonly SObject provider;
    private readonly PatternProviderService service;
    private readonly Rectangle slotArea;
    private readonly Rectangle controlArea;
    private readonly Rectangle inventoryArea;
    private readonly ClickableComponent priorityUpButton;
    private readonly ClickableComponent priorityDownButton;
    private readonly ClickableComponent patternUpButton;
    private readonly ClickableComponent patternDownButton;
    private readonly ClickableComponent ejectButton;
    private IReadOnlyList<PatternProviderSlotView> cachedViews = Array.Empty<PatternProviderSlotView>();
    private int cachedAtTick = -1;
    private int selectedSlot;

    public PatternProviderMenu(SObject provider, PatternProviderService service)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.provider = provider;
        this.service = service;
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

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        if (this.priorityUpButton.containsPoint(x, y))
        {
            this.RunPriority(-1);
            return;
        }
        if (this.priorityDownButton.containsPoint(x, y))
        {
            this.RunPriority(1);
            return;
        }
        if (this.patternUpButton.containsPoint(x, y))
        {
            this.MoveSelected(-1);
            return;
        }
        if (this.patternDownButton.containsPoint(x, y))
        {
            this.MoveSelected(1);
            return;
        }
        if (this.ejectButton.containsPoint(x, y))
        {
            this.EjectSelected();
            return;
        }

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
        var slotIndex = this.HitSlot(x, y);
        if (slotIndex < 0)
            return;

        this.selectedSlot = slotIndex;
        this.EjectSelected();
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        Utility.drawTextWithShadow(
            b,
            this.provider.DisplayName,
            Game1.dialogueFont,
            new Vector2(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 26),
            Game1.textColor);

        this.RefreshCachedViews();
        this.DrawPatternSlots(b, this.cachedViews);
        this.DrawControls(b, this.cachedViews);
        SVSAPMenuWidgets.DrawSeparator(b, new Rectangle(this.xPositionOnScreen + Pad, this.inventoryArea.Y - 12, this.width - Pad * 2, 2));
        this.DrawInventory(b);
        this.DrawHoverTooltip(b, this.cachedViews);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    private void DrawPatternSlots(SpriteBatch b, IReadOnlyList<PatternProviderSlotView> views)
    {
        for (var index = 0; index < SlotCount; index++)
        {
            var bounds = this.GetSlotBounds(index);
            var view = views.FirstOrDefault(candidate => candidate.SlotIndex == index);
            SVSAPMenuWidgets.DrawSlotBackground(b, bounds, view is null);
            SVSAPMenuWidgets.DrawSlotStatusLine(b, bounds, view is null ? PixelStatus.Idle : PixelStatus.Ready);
            if (index == this.selectedSlot)
                DrawSelection(b, bounds);
            view?.Item.drawInMenu(b, new Vector2(bounds.X + 4, bounds.Y + 4), 0.72f, 1f, 0.86f, StackDrawType.Hide, Color.White, true);
        }
    }

    private void DrawControls(SpriteBatch b, IReadOnlyList<PatternProviderSlotView> views)
    {
        SVSAPMenuWidgets.DrawInsetBox(b, this.controlArea);
        SVSAPMenuWidgets.DrawFittedLine(
            b,
            ModText.Format("ui.patternProvider.priority", this.service.GetPriority(this.provider)),
            new Rectangle(this.controlArea.X + 12, this.controlArea.Y + 10, this.controlArea.Width - 24, 26),
            Game1.textColor);

        var selected = views.FirstOrDefault(view => view.SlotIndex == this.selectedSlot);
        SVSAPMenuWidgets.DrawButton(b, this.priorityUpButton);
        SVSAPMenuWidgets.DrawButton(b, this.priorityDownButton);
        SVSAPMenuWidgets.DrawButton(b, this.patternUpButton, tint: selected is null ? Color.Gray : Color.White);
        SVSAPMenuWidgets.DrawButton(b, this.patternDownButton, tint: selected is null ? Color.Gray : Color.White);
        SVSAPMenuWidgets.DrawButton(b, this.ejectButton, tint: selected is null ? Color.Gray : Color.White);

        var y = this.ejectButton.bounds.Bottom + 16;
        var lines = selected is null
            ? new[] { ModText.Get("ui.patternProvider.selectedEmpty"), ModText.Get("ui.patternProvider.orderRule") }
            : new[]
            {
                ModText.Format("ui.patternProvider.selectedSlot", selected.SlotIndex + 1),
                PatternDisplayNames.Get(selected.Pattern),
                selected.Pattern.Kind == PatternKind.Crafting
                    ? ModText.Get("ui.patternProvider.kind.crafting")
                    : ModText.Get("ui.patternProvider.kind.processing"),
                ModText.Get("ui.patternProvider.orderRule")
            };
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
        if (!PatternCodec.IsPatternItem(item))
        {
            Game1.playSound("cancel");
            return;
        }

        this.RefreshCachedViews(force: true);
        var occupied = this.cachedViews.Select(view => view.SlotIndex).ToHashSet();
        var target = occupied.Contains(this.selectedSlot)
            ? Enumerable.Range(0, SlotCount).FirstOrDefault(index => !occupied.Contains(index), -1)
            : this.selectedSlot;
        if (target < 0)
        {
            this.Show(false, ModText.Get("ui.patternProvider.full"));
            return;
        }

        var success = this.service.TryInsertPatternSlot(this.provider, target, item, out var message);
        if (success)
        {
            this.selectedSlot = Math.Min(SlotCount - 1, target + 1);
            this.RefreshCachedViews(force: true);
        }
        this.Show(success, message);
    }

    private void MoveSelected(int direction)
    {
        var success = this.service.TryMovePatternSlot(this.provider, this.selectedSlot, direction, out var message);
        if (success)
        {
            this.selectedSlot = Math.Clamp(this.selectedSlot + Math.Sign(direction), 0, SlotCount - 1);
            this.RefreshCachedViews(force: true);
        }
        this.Show(success, message);
    }

    private void EjectSelected()
    {
        var success = this.service.TryEjectPatternSlot(this.provider, this.selectedSlot, out var message);
        if (success)
            this.RefreshCachedViews(force: true);
        this.Show(success, message);
    }

    private void RunPriority(int delta)
    {
        var success = this.service.TryAdjustPriority(this.provider, delta, out var message);
        this.Show(success, message);
    }

    private void Show(bool success, string message)
    {
        Game1.addHUDMessage(new HUDMessage(message, success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        Game1.playSound(success ? "smallSelect" : "cancel");
    }

    private void RefreshCachedViews(bool force = false)
    {
        var tick = Game1.ticks;
        if (!force
            && this.cachedAtTick >= 0
            && tick >= this.cachedAtTick
            && tick - this.cachedAtTick < ViewRefreshTicks)
        {
            return;
        }

        this.cachedViews = this.service.GetSlotViews(this.provider);
        this.cachedAtTick = tick;
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<PatternProviderSlotView> views)
    {
        var x = Game1.getMouseX();
        var y = Game1.getMouseY();
        var slotIndex = this.HitSlot(x, y);
        if (slotIndex >= 0)
        {
            var view = views.FirstOrDefault(candidate => candidate.SlotIndex == slotIndex);
            if (view is not null)
            {
                var kind = view.Pattern.Kind == PatternKind.Crafting
                    ? ModText.Get("ui.patternProvider.kind.crafting")
                    : ModText.Get("ui.patternProvider.kind.processing");
                var machine = string.IsNullOrWhiteSpace(view.Pattern.MachineQualifiedItemId)
                    ? ModText.Get("ui.patternProvider.machine.crafting")
                    : FormatItem(view.Pattern.MachineQualifiedItemId);
                SVSAPMenuWidgets.DrawTooltipBox(
                    b,
                    x + 28,
                    y + 28,
                    PatternDisplayNames.Get(view.Pattern),
                    new[] { kind, ModText.Format("ui.patternProvider.machine", machine) });
            }
            return;
        }

        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex >= 0 && Game1.player.Items[inventoryIndex] is Item item)
            SVSAPMenuWidgets.DrawTooltipBox(b, x + 28, y + 28, item.DisplayName, new[] { item.getDescription() });
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
