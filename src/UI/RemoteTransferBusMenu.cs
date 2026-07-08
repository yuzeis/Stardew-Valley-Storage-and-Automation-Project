using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemoteTransferBusMenu : IClickableMenu
{
    private const int Pad = 28;
    private const int FilterColumns = 3;
    private const int FilterRows = 3;
    private const int ControlButtonWidth = 150;
    private const int ControlButtonHeight = 42;
    private const int ControlButtonGap = 10;
    private const int DirectionButtonGap = 6;

    private readonly Action<StructuralActionKind, int, string, int> sendAction;
    private readonly Action requestRefresh;
    private readonly SVSAPBackpackGrid backpackGrid = new();
    private readonly List<ClickableComponent> directionButtons = new();
    private ClickableComponent modeButton = null!;
    private ClickableComponent oreButton = null!;
    private ClickableComponent qualityButton = null!;
    private ClickableComponent clearButton = null!;
    private Rectangle filterArea;
    private Rectangle invArea;
    private int selectedSlot;

    public RemoteTransferBusMenu(
        StructuralSnapshotResponseMessage snapshot,
        Action<StructuralActionKind, int, string, int> sendAction,
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
        this.BuildLayout();
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    internal StructuralSnapshotResponseMessage Snapshot { get; private set; }

    public void ApplySnapshot(StructuralSnapshotResponseMessage snapshot)
    {
        if (snapshot.Kind == StructuralSnapshotKind.TransferBus && snapshot.TransferBus is not null)
            this.Snapshot = snapshot;
    }

    private static int GetMenuWidth() => Math.Min(1040, Game1.uiViewport.Width - 80);

    private static int GetMenuHeight() => Math.Min(760, Game1.uiViewport.Height - 80);

    private void BuildLayout()
    {
        var innerX = this.xPositionOnScreen + Pad;
        var innerW = this.width - Pad * 2;
        var top = this.yPositionOnScreen + 24;

        this.filterArea = new Rectangle(innerX, top + 92, FilterColumns * SVSAPMenuWidgets.Cell, FilterRows * SVSAPMenuWidgets.Cell);
        var controlsX = this.filterArea.Right + 34;
        var controlsY = this.filterArea.Y;
        var controlsAvailable = Math.Max(1, this.xPositionOnScreen + this.width - Pad - controlsX);
        var controlColumns = controlsAvailable >= ControlButtonWidth * 2 + ControlButtonGap ? 2 : 1;
        var controlWidth = Math.Clamp((controlsAvailable - Math.Max(0, controlColumns - 1) * ControlButtonGap) / controlColumns, 90, ControlButtonWidth);
        this.modeButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 0, controlColumns, controlWidth), "mode", ModText.Get("ui.transferBus.action.toggleFilterMode"));
        this.oreButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 1, controlColumns, controlWidth), "ore", string.Empty);
        this.qualityButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 2, controlColumns, controlWidth), "quality", ModText.Get("ui.transferBus.action.toggleQuality"));
        this.clearButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 3, controlColumns, controlWidth), "clear", ModText.Get("ui.transferBus.action.clearFilter"));

        this.directionButtons.Clear();
        var dirY = controlsY + 2 * (ControlButtonHeight + 12) + 10;
        var directionButtonWidth = Math.Clamp((controlsAvailable - DirectionButtonGap * 4) / 5, 48, 80);
        var directions = new[]
        {
            (Value: -1, Label: ModText.Get("ui.transferBus.direction.all")),
            (Value: 0, Label: ModText.Get("ui.transferBus.direction.up")),
            (Value: 1, Label: ModText.Get("ui.transferBus.direction.right")),
            (Value: 2, Label: ModText.Get("ui.transferBus.direction.down")),
            (Value: 3, Label: ModText.Get("ui.transferBus.direction.left"))
        };
        for (var i = 0; i < directions.Length; i++)
        {
            this.directionButtons.Add(new ClickableComponent(
                new Rectangle(controlsX + i * (directionButtonWidth + DirectionButtonGap), dirY, directionButtonWidth, 36),
                directions[i].Value.ToString(),
                directions[i].Label));
        }

        var backpackColumns = SVSAPBackpackGrid.GetColumnCount(innerW);
        var invH = SVSAPBackpackGrid.GetHeight(backpackColumns);
        var invW = backpackColumns * SVSAPMenuWidgets.Cell;
        this.invArea = new Rectangle(innerX + Math.Max(0, (innerW - invW) / 2), this.yPositionOnScreen + this.height - Pad - invH, invW, invH);
        this.backpackGrid.SetBounds(this.invArea);
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        Utility.drawTextWithShadow(b, this.Snapshot.DisplayName, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 26), Game1.textColor);

        var slots = this.Snapshot.TransferBus?.FilterSlots ?? new List<RemoteTransferFilterSlotMessage>();
        this.DrawFilterSlots(b, slots);
        this.DrawControls(b);
        SVSAPMenuWidgets.DrawSeparator(b, new Rectangle(this.xPositionOnScreen + Pad, this.invArea.Y - 12, this.width - Pad * 2, 2));
        this.backpackGrid.Draw(b);
        this.DrawHoverTooltip(b, slots);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        if (this.modeButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.TransferBusToggleFilterMode); return; }
        if (this.oreButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.TransferBusToggleOreDictionary); return; }
        if (this.qualityButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.TransferBusToggleQuality); return; }
        if (this.clearButton.containsPoint(x, y)) { this.RunAction(StructuralActionKind.TransferBusClearFilter); return; }

        foreach (var button in this.directionButtons)
        {
            if (!button.containsPoint(x, y) || !int.TryParse(button.name, out var direction))
                continue;

            this.RunAction(StructuralActionKind.TransferBusSetFacing, direction: direction);
            return;
        }

        var filterSlot = this.HitFilterSlot(x, y);
        if (filterSlot >= 0)
        {
            this.selectedSlot = filterSlot;
            var view = this.Snapshot.TransferBus?.FilterSlots.FirstOrDefault(slot => slot.SlotIndex == filterSlot);
            if (view?.Occupied == true)
                this.RunAction(StructuralActionKind.TransferBusClearFilterSlot, slotIndex: filterSlot);
            else
                Game1.playSound("smallSelect");
            return;
        }

        var inventoryIndex = this.backpackGrid.HitTest(x, y);
        if (inventoryIndex >= 0)
            this.SetFilterFromInventory(inventoryIndex);
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        var filterSlot = this.HitFilterSlot(x, y);
        if (filterSlot >= 0)
        {
            this.RunAction(StructuralActionKind.TransferBusClearFilterSlot, slotIndex: filterSlot);
            return;
        }

        this.requestRefresh();
        Game1.playSound("shwip");
    }

    private void DrawFilterSlots(SpriteBatch b, IReadOnlyList<RemoteTransferFilterSlotMessage> slots)
    {
        for (var index = 0; index < FilterColumns * FilterRows; index++)
        {
            var cell = this.GetFilterSlotBounds(index);
            var selected = index == this.selectedSlot;
            SVSAPMenuWidgets.DrawSlotBackground(b, cell);
            if (selected)
                b.Draw(Game1.staminaRect, new Rectangle(cell.X - 2, cell.Y - 2, cell.Width + 4, cell.Height + 4), Color.LightGreen * 0.35f);

            var view = slots.FirstOrDefault(slot => slot.SlotIndex == index);
            if (view?.Occupied != true || string.IsNullOrWhiteSpace(view.QualifiedItemId))
                continue;

            try
            {
                ItemRegistry.Create(view.QualifiedItemId).drawInMenu(
                    b,
                    new Vector2(cell.X + SVSAPMenuWidgets.IconInset, cell.Y + SVSAPMenuWidgets.IconInset),
                    1f,
                    1f,
                    0.86f,
                    StackDrawType.Hide,
                    Color.White,
                    true);
            }
            catch
            {
                // The host-authored text still identifies unknown local items.
            }
        }
    }

    private void DrawControls(SpriteBatch b)
    {
        this.oreButton.label = this.Snapshot.TransferBus?.OreDictionaryMode == true
            ? ModText.Get("ui.transferBus.oreDictionaryOnShort")
            : ModText.Get("ui.transferBus.oreDictionaryOffShort");

        SVSAPMenuWidgets.DrawButton(b, this.modeButton);
        SVSAPMenuWidgets.DrawButton(b, this.oreButton);
        SVSAPMenuWidgets.DrawButton(b, this.qualityButton);
        SVSAPMenuWidgets.DrawButton(b, this.clearButton);

        var facing = this.Snapshot.TransferBus?.FacingDirection ?? -1;
        foreach (var button in this.directionButtons)
        {
            var selected = int.TryParse(button.name, out var direction) && direction == facing;
            SVSAPMenuWidgets.DrawButton(b, button, tint: selected ? Color.LightGreen : Color.White);
        }

        var lines = this.Snapshot.TransferBus?.ConfigurationLines.Take(7).ToList() ?? new List<string>();
        var x = this.filterArea.Right + 34;
        var y = this.filterArea.Bottom + 24;
        foreach (var line in lines)
        {
            b.DrawString(Game1.smallFont, line, new Vector2(x, y), Game1.textColor);
            y += 26;
        }
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<RemoteTransferFilterSlotMessage> slots)
    {
        var mx = Game1.getMouseX();
        var my = Game1.getMouseY();
        var slotIndex = this.HitFilterSlot(mx, my);
        if (slotIndex >= 0)
        {
            var view = slots.FirstOrDefault(slot => slot.SlotIndex == slotIndex);
            if (view?.Occupied == true)
            {
                var lines = new List<string> { view.QualifiedItemId };
                if (view.OreGroups.Count > 0)
                    lines.Add(ModText.Format("ui.transferBus.tooltip.oreGroups", string.Join(", ", view.OreGroups)));
                SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, view.DisplayName, lines);
            }
            return;
        }

        var inventoryIndex = this.backpackGrid.HitTest(mx, my);
        if (inventoryIndex >= 0)
        {
            var item = Game1.player.Items[inventoryIndex];
            if (item is not null)
                SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, item.DisplayName, new List<string> { item.getDescription() });
        }
    }

    private void SetFilterFromInventory(int inventoryIndex)
    {
        if (inventoryIndex < 0 || inventoryIndex >= Game1.player.Items.Count)
            return;

        var item = Game1.player.Items[inventoryIndex];
        if (item is null)
            return;

        var slot = this.FindTargetFilterSlot();
        this.RunAction(StructuralActionKind.TransferBusSetFilterSlot, slotIndex: slot, itemId: item.QualifiedItemId);
        this.selectedSlot = Math.Min(slot + 1, FilterColumns * FilterRows - 1);
    }

    private int FindTargetFilterSlot()
    {
        if (this.selectedSlot >= 0 && this.selectedSlot < FilterColumns * FilterRows)
            return this.selectedSlot;

        return this.Snapshot.TransferBus?.FilterSlots.FirstOrDefault(slot => !slot.Occupied)?.SlotIndex ?? 0;
    }

    private int HitFilterSlot(int x, int y)
    {
        if (!this.filterArea.Contains(x, y))
            return -1;

        var column = (x - this.filterArea.X) / SVSAPMenuWidgets.Cell;
        var row = (y - this.filterArea.Y) / SVSAPMenuWidgets.Cell;
        if (column < 0 || column >= FilterColumns || row < 0 || row >= FilterRows)
            return -1;

        return row * FilterColumns + column;
    }

    private Rectangle GetFilterSlotBounds(int index)
    {
        var column = index % FilterColumns;
        var row = index / FilterColumns;
        return new Rectangle(
            this.filterArea.X + column * SVSAPMenuWidgets.Cell,
            this.filterArea.Y + row * SVSAPMenuWidgets.Cell,
            SVSAPMenuWidgets.Cell - 4,
            SVSAPMenuWidgets.Cell - 4);
    }

    private void RunAction(StructuralActionKind kind, int slotIndex = -1, string itemId = "", int direction = -1)
    {
        this.sendAction(kind, slotIndex, itemId, direction);
        Game1.playSound("smallSelect");
    }

    private static Rectangle GetControlButtonBounds(int x, int y, int index, int columns, int width)
    {
        var column = index % Math.Max(1, columns);
        var row = index / Math.Max(1, columns);
        return new Rectangle(
            x + column * (width + ControlButtonGap),
            y + row * (ControlButtonHeight + 12),
            width,
            ControlButtonHeight);
    }
}
