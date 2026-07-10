using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Content;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemoteStorageDriveMenu : IClickableMenu
{
    private const int SnapshotRefreshTicks = 30;
    private const int Pad = 28;
    private const int SlotCount = 10;
    private const int SlotColumns = 5;
    private const int InventoryCell = 48;
    private const int InventoryColumns = 12;
    private readonly Action<StructuralActionKind, int, Item?> runSlotAction;
    private readonly Action requestRefresh;
    private readonly Rectangle slotArea;
    private readonly Rectangle inventoryArea;
    private bool requestPending;
    private int selectedSlot;
    private int snapshotAtTick;

    public RemoteStorageDriveMenu(
        StructuralSnapshotResponseMessage snapshot,
        Action<StructuralActionKind, int, Item?> runSlotAction,
        Action requestRefresh)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.Snapshot = snapshot;
        this.snapshotAtTick = Game1.ticks;
        this.runSlotAction = runSlotAction;
        this.requestRefresh = requestRefresh;
        this.slotArea = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 112, SlotColumns * SVSAPMenuWidgets.Cell, 2 * SVSAPMenuWidgets.Cell);
        this.inventoryArea = new Rectangle(
            this.xPositionOnScreen + (this.width - InventoryColumns * InventoryCell) / 2,
            this.yPositionOnScreen + this.height - Pad - 3 * InventoryCell,
            InventoryColumns * InventoryCell,
            3 * InventoryCell);
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    internal StructuralSnapshotResponseMessage Snapshot { get; private set; }

    public void ApplySnapshot(StructuralSnapshotResponseMessage snapshot)
    {
        this.requestPending = false;
        this.snapshotAtTick = Game1.ticks;
        if (snapshot.Kind == StructuralSnapshotKind.StorageDrive && snapshot.StorageDrive is not null)
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

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        Utility.drawTextWithShadow(b, this.Snapshot.DisplayName, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 26), Game1.textColor);

        if (this.requestPending)
        {
            b.DrawString(Game1.smallFont, ModText.Get("remoteTerminal.pendingInline"), new Vector2(this.xPositionOnScreen + Pad + 320, this.yPositionOnScreen + 34), Color.Firebrick);
        }

        var slots = this.Snapshot.StorageDrive?.Slots ?? new List<RemoteStorageDriveSlotMessage>();
        this.DrawSlots(b, slots);
        this.DrawSummary(b);
        this.DrawInventory(b);
        this.DrawHoverTooltip(b, slots);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
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

        var slotIndex = this.HitSlot(x, y);
        if (slotIndex >= 0)
        {
            this.selectedSlot = slotIndex;
            var view = this.Snapshot.StorageDrive?.Slots.FirstOrDefault(slot => slot.SlotIndex == slotIndex);
            if (view?.Occupied == true)
            {
                this.SendAction(StructuralActionKind.StorageDriveEjectSlot, slotIndex, null);
                return;
            }

            var held = Game1.player.CurrentItem;
            if (held is not null && ModItemCatalog.TryGetStorageCellTier(held.QualifiedItemId, out _))
                this.SendAction(StructuralActionKind.StorageDriveInsertSlot, slotIndex, held);
            else
                Game1.playSound("smallSelect");
            return;
        }

        var inventoryIndex = this.HitInventorySlot(x, y);
        if (inventoryIndex < 0 || Game1.player.Items[inventoryIndex] is not Item item)
            return;
        if (!ModItemCatalog.TryGetStorageCellTier(item.QualifiedItemId, out _))
        {
            Game1.playSound("cancel");
            return;
        }

        var targetSlot = this.Snapshot.StorageDrive?.Slots.Any(slot => slot.SlotIndex == this.selectedSlot && slot.Occupied) == true
            ? Enumerable.Range(0, SlotCount).FirstOrDefault(index => this.Snapshot.StorageDrive?.Slots.Any(slot => slot.SlotIndex == index && slot.Occupied) != true)
            : this.selectedSlot;
        var oldToolIndex = Game1.player.CurrentToolIndex;
        Game1.player.CurrentToolIndex = inventoryIndex;
        try
        {
            this.SendAction(StructuralActionKind.StorageDriveInsertSlot, targetSlot, item);
        }
        finally
        {
            Game1.player.CurrentToolIndex = oldToolIndex;
        }
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        if (this.requestPending)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.requestPending"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        this.requestPending = true;
        this.snapshotAtTick = Game1.ticks;
        this.requestRefresh();
        Game1.playSound("shwip");
    }

    private void DrawSlots(SpriteBatch b, IReadOnlyList<RemoteStorageDriveSlotMessage> views)
    {
        for (var index = 0; index < SlotCount; index++)
        {
            var cell = this.GetSlotBounds(index);
            var view = views.FirstOrDefault(slot => slot.SlotIndex == index);
            SVSAPMenuWidgets.DrawSlotBackground(b, cell, view?.Occupied != true);
            SVSAPMenuWidgets.DrawSlotStatusLine(b, cell, view?.Occupied == true ? PixelStatus.Ready : PixelStatus.Idle);
            if (index == this.selectedSlot)
                DrawSelection(b, cell);
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
                // Snapshot text below still identifies the cell if the local client lacks an icon.
            }

            var ratio = view.CapacityMax <= 0 ? 0f : Math.Clamp(view.CapacityUsed / (float)view.CapacityMax, 0f, 1f);
            var bar = new Rectangle(cell.X + 6, cell.Bottom - 9, cell.Width - 12, 4);
            b.Draw(Game1.staminaRect, bar, Color.Black * 0.35f);
            b.Draw(Game1.staminaRect, new Rectangle(bar.X, bar.Y, (int)(bar.Width * ratio), bar.Height), ratio >= 0.9f ? Color.OrangeRed : Color.LightGreen);
        }
    }

    private void DrawSummary(SpriteBatch b)
    {
        var lines = this.Snapshot.StorageDrive?.SummaryLines ?? new List<string>();
        var x = this.slotArea.Right + 32;
        var y = this.slotArea.Y;
        foreach (var line in lines.Take(10))
        {
            var text = line.Length > 48 ? line[..48] + "..." : line;
            b.DrawString(Game1.smallFont, text, new Vector2(x, y), Game1.textColor);
            y += 28;
            if (y > this.inventoryArea.Y - 48)
                break;
        }
    }

    private void DrawInventory(SpriteBatch b)
    {
        b.Draw(Game1.staminaRect, new Rectangle(this.inventoryArea.X, this.inventoryArea.Y - 10, this.inventoryArea.Width, 2), Color.SaddleBrown * 0.45f);
        for (var index = 0; index < Math.Min(36, Game1.player.Items.Count); index++)
        {
            var bounds = this.GetInventorySlotBounds(index);
            SVSAPMenuWidgets.DrawSlotBackground(b, bounds, Game1.player.Items[index] is null);
            Game1.player.Items[index]?.drawInMenu(b, new Vector2(bounds.X + 4, bounds.Y + 4), 0.68f, 1f, 0.86f, StackDrawType.Draw, Color.White, true);
        }
    }

    private void SendAction(StructuralActionKind kind, int slotIndex, Item? held)
    {
        this.requestPending = true;
        this.snapshotAtTick = Game1.ticks;
        this.runSlotAction(kind, slotIndex, held);
        Game1.playSound("smallSelect");
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<RemoteStorageDriveSlotMessage> views)
    {
        var slotIndex = this.HitSlot(Game1.getMouseX(), Game1.getMouseY());
        if (slotIndex < 0)
            return;

        var view = views.FirstOrDefault(slot => slot.SlotIndex == slotIndex);
        if (view?.Occupied != true)
            return;

        var lines = new List<string>
        {
            ModText.Format("ui.storageDrive.tooltip.bytes", view.CapacityUsed, view.CapacityMax),
            ModText.Format("ui.storageDrive.tooltip.types", view.TypesUsed, view.TypesMax)
        };
        SVSAPMenuWidgets.DrawTooltipBox(b, Game1.getMouseX() + 28, Game1.getMouseY() + 28, view.DisplayName, lines);
    }

    private int HitSlot(int x, int y)
    {
        if (!this.slotArea.Contains(x, y))
            return -1;

        var column = (x - this.slotArea.X) / SVSAPMenuWidgets.Cell;
        var row = (y - this.slotArea.Y) / SVSAPMenuWidgets.Cell;
        if (column < 0 || column >= SlotColumns || row < 0 || row >= 2)
            return -1;

        var index = row * SlotColumns + column;
        return index < SlotCount ? index : -1;
    }

    private Rectangle GetSlotBounds(int index)
    {
        var column = index % SlotColumns;
        var row = index / SlotColumns;
        return new Rectangle(
            this.slotArea.X + column * SVSAPMenuWidgets.Cell,
            this.slotArea.Y + row * SVSAPMenuWidgets.Cell,
            SVSAPMenuWidgets.Cell - 4,
            SVSAPMenuWidgets.Cell - 4);
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

    private static void DrawSelection(SpriteBatch b, Rectangle bounds)
    {
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 2, bounds.Height), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 2, bounds.Y, 2, bounds.Height), Color.Gold);
    }

    private static int GetMenuWidth() => Math.Min(760, Math.Max(640, Game1.uiViewport.Width - 48));
    private static int GetMenuHeight() => Math.Min(640, Math.Max(560, Game1.uiViewport.Height - 48));
}
