using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Content;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SVSAP.UI;

internal sealed class StorageDriveMenu : IClickableMenu
{
    private const int Pad = SVSAPMenuWidgets.Pad;
    private const int SlotCount = 10;
    private const int MaxSlotColumns = 5;
    private const int MinSlotColumns = 3;
    private const int SummaryMinWidth = 180;
    private const int SummaryGap = 32;
    private const int InventoryCell = 48;
    private const int MaxInventoryColumns = 12;
    private const int ViewRefreshTicks = 30;

    private readonly SObject drive;
    private readonly GameLocation location;
    private readonly Vector2 tile;
    private readonly StorageDriveService storageDriveService;
    private readonly int slotColumns;
    private readonly int slotRows;
    private readonly int inventoryColumns;
    private Rectangle slotArea;
    private readonly Rectangle inventoryArea;
    private IReadOnlyList<StorageDriveSlotView> cachedViews = Array.Empty<StorageDriveSlotView>();
    private IReadOnlyList<string> cachedSummary = Array.Empty<string>();
    private int cachedAtTick = -1;
    private int selectedSlot;

    public StorageDriveMenu(SObject drive, GameLocation location, Vector2 tile, StorageDriveService storageDriveService)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.drive = drive;
        this.location = location;
        this.tile = tile;
        this.storageDriveService = storageDriveService;
        var layout = CalculateLayoutShape(this.width);
        this.slotColumns = layout.Columns;
        this.slotRows = layout.Rows;
        this.inventoryColumns = Math.Clamp((this.width - Pad * 2) / InventoryCell, 4, MaxInventoryColumns);
        var inventoryRows = Math.Max(3, (int)Math.Ceiling(Game1.player.Items.Count / (double)this.inventoryColumns));
        this.slotArea = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 112, this.slotColumns * SVSAPMenuWidgets.Cell, this.slotRows * SVSAPMenuWidgets.Cell);
        this.inventoryArea = new Rectangle(
            this.xPositionOnScreen + (this.width - this.inventoryColumns * InventoryCell) / 2,
            this.yPositionOnScreen + this.height - Pad - inventoryRows * InventoryCell,
            this.inventoryColumns * InventoryCell,
            inventoryRows * InventoryCell);
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private static int GetMenuWidth() => Math.Max(1, Math.Min(760, Game1.uiViewport.Width - 48));

    private static int GetMenuHeight() => Math.Max(1, Math.Min(640, Game1.uiViewport.Height - 48));

    internal static StorageDriveMenuLayoutShape CalculateLayoutShape(int menuWidth)
    {
        var availableSlotWidth = menuWidth - Pad * 2 - SummaryMinWidth - SummaryGap;
        var columns = Math.Clamp(availableSlotWidth / SVSAPMenuWidgets.Cell, MinSlotColumns, MaxSlotColumns);
        var rows = Math.Max(1, (int)Math.Ceiling(SlotCount / (double)columns));
        return new StorageDriveMenuLayoutShape(columns, rows);
    }

    internal static bool LayoutFits(int menuWidth)
    {
        var layout = CalculateLayoutShape(menuWidth);
        var slotRight = Pad + layout.Columns * SVSAPMenuWidgets.Cell;
        var summaryX = slotRight + SummaryGap;
        return slotRight <= menuWidth - Pad
            && summaryX + SummaryMinWidth <= menuWidth - Pad;
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        SVSAPMenuWidgets.DrawFittedTitle(
            b,
            this.drive.DisplayName,
            new Rectangle(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 18, this.width - Pad * 2 - 70, 52),
            Game1.textColor);

        this.RefreshCachedViews();
        this.DrawSlots(b, this.cachedViews);
        this.DrawSummary(b, this.cachedSummary);
        this.DrawInventory(b);
        this.DrawHoverTooltip(b, this.cachedViews);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            base.receiveLeftClick(x, y, playSound);
            return;
        }

        base.receiveLeftClick(x, y, playSound);

        var slotIndex = this.HitSlot(x, y);
        if (slotIndex >= 0)
        {
            this.RefreshCachedViews(force: true);
            var occupied = this.cachedViews.FirstOrDefault(view => view.SlotIndex == slotIndex)?.Occupied == true;
            if (!occupied)
            {
                this.selectedSlot = slotIndex;
                var held = Game1.player.CurrentItem;
                if (held is not null && ModItemCatalog.TryGetStorageCellTier(held.QualifiedItemId, out _))
                    this.RunInsert(slotIndex, held);
                else
                    Game1.playSound("smallSelect");
                return;
            }

            this.RunEject(slotIndex);
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

        var targetSlot = this.storageDriveService.HasCellSlot(this.drive, this.selectedSlot)
            ? Enumerable.Range(0, SlotCount).FirstOrDefault(index => !this.storageDriveService.HasCellSlot(this.drive, index), -1)
            : this.selectedSlot;
        if (targetSlot < 0)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("ui.storageDrive.full"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        this.RunInsert(targetSlot, item);
    }

    private void DrawSlots(SpriteBatch b, IReadOnlyList<StorageDriveSlotView> views)
    {
        for (var index = 0; index < this.slotColumns * this.slotRows; index++)
        {
            if (index >= SlotCount)
                break;

            var cell = this.GetSlotBounds(index);
            var view = views.FirstOrDefault(slot => slot.SlotIndex == index);
            SVSAPMenuWidgets.DrawSlotBackground(b, cell, view?.Occupied != true);
            SVSAPMenuWidgets.DrawSlotStatusLine(b, cell, view?.Occupied == true ? PixelStatus.Ready : PixelStatus.Idle);
            if (index == this.selectedSlot)
                DrawSelection(b, cell);
            if (view?.Item is null)
                continue;

            SVSAPMenuWidgets.DrawItemInSlot(b, view.Item, cell, 1);

            var ratio = view.CapacityMax <= 0 ? 0f : Math.Clamp(view.CapacityUsed / (float)view.CapacityMax, 0f, 1f);
            var bar = new Rectangle(cell.X + 6, cell.Bottom - 9, cell.Width - 12, 4);
            b.Draw(Game1.staminaRect, bar, Color.Black * 0.35f);
            b.Draw(Game1.staminaRect, new Rectangle(bar.X, bar.Y, (int)(bar.Width * ratio), bar.Height), ratio >= 0.9f ? Color.OrangeRed : Color.LightGreen);
        }
    }

    private void DrawSummary(SpriteBatch b, IReadOnlyList<string> lines)
    {
        var x = this.slotArea.Right + 32;
        var y = this.slotArea.Y;
        var maxWidth = this.xPositionOnScreen + this.width - Pad - x;
        foreach (var line in lines.Take(10))
        {
            SVSAPMenuWidgets.DrawFittedLine(b, line, new Rectangle(x, y, Math.Max(1, maxWidth), 26), Game1.textColor, horizontalPadding: 0);
            y += 28;
            if (y > this.inventoryArea.Y - 56)
                break;
        }
    }

    private void DrawInventory(SpriteBatch b)
    {
        b.Draw(Game1.staminaRect, new Rectangle(this.inventoryArea.X, this.inventoryArea.Y - 10, this.inventoryArea.Width, 2), Color.SaddleBrown * 0.45f);
        for (var index = 0; index < Game1.player.Items.Count; index++)
        {
            var bounds = this.GetInventorySlotBounds(index);
            SVSAPMenuWidgets.DrawSlotBackground(b, bounds, Game1.player.Items[index] is null);
            var item = Game1.player.Items[index];
            SVSAPMenuWidgets.DrawItemInSlot(b, item, bounds, item?.Stack ?? 0, 0.68f);
        }
    }

    private void RunInsert(int slotIndex, Item item)
    {
        var success = this.storageDriveService.TryInsertCellSlot(this.drive, slotIndex, item, out var message);
        Game1.addHUDMessage(new HUDMessage(message, success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        Game1.playSound(success ? "Ship" : "cancel");
        if (success)
        {
            this.selectedSlot = Math.Min(SlotCount - 1, slotIndex + 1);
            this.RefreshCachedViews(force: true);
        }
    }

    private void RunEject(int slotIndex)
    {
        var success = this.storageDriveService.TryEjectCellSlot(this.drive, this.location, this.tile, slotIndex, out var message);
        Game1.addHUDMessage(new HUDMessage(message, success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        Game1.playSound(success ? "Ship" : "cancel");
        if (success)
        {
            this.selectedSlot = slotIndex;
            this.RefreshCachedViews(force: true);
        }
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

        this.cachedViews = this.storageDriveService.GetSlotViews(this.drive);
        this.cachedSummary = this.storageDriveService.DescribeDrive(this.drive);
        this.cachedAtTick = tick;
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<StorageDriveSlotView> views)
    {
        var slotIndex = this.HitSlot(Game1.getMouseX(), Game1.getMouseY());
        if (slotIndex < 0)
            return;

        var view = views.FirstOrDefault(slot => slot.SlotIndex == slotIndex);
        if (view?.Item is null)
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
        if (column < 0 || column >= this.slotColumns || row < 0 || row >= this.slotRows)
            return -1;

        var index = row * this.slotColumns + column;
        return index < SlotCount ? index : -1;
    }

    private Rectangle GetSlotBounds(int index)
    {
        var column = index % this.slotColumns;
        var row = index / this.slotColumns;
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
        var index = (y - this.inventoryArea.Y) / InventoryCell * this.inventoryColumns + (x - this.inventoryArea.X) / InventoryCell;
        return index >= 0 && index < Game1.player.Items.Count ? index : -1;
    }

    private Rectangle GetInventorySlotBounds(int index)
    {
        return new Rectangle(
            this.inventoryArea.X + index % this.inventoryColumns * InventoryCell,
            this.inventoryArea.Y + index / this.inventoryColumns * InventoryCell,
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
}

internal readonly record struct StorageDriveMenuLayoutShape(int Columns, int Rows);
