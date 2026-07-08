using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SVSAP.UI;

internal sealed class StorageDriveMenu : IClickableMenu
{
    private const int Pad = 28;
    private const int SlotCount = 10;
    private const int MaxSlotColumns = 5;
    private const int MinSlotColumns = 3;
    private const int SummaryMinWidth = 180;
    private const int SummaryGap = 32;

    private readonly SObject drive;
    private readonly GameLocation location;
    private readonly Vector2 tile;
    private readonly StorageDriveService storageDriveService;
    private readonly int slotColumns;
    private readonly int slotRows;
    private Rectangle slotArea;

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
        this.slotArea = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 112, this.slotColumns * SVSAPMenuWidgets.Cell, this.slotRows * SVSAPMenuWidgets.Cell);
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private static int GetMenuWidth() => Math.Min(760, Math.Max(520, Game1.uiViewport.Width - 48));

    private static int GetMenuHeight() => Math.Min(500, Math.Max(420, Game1.uiViewport.Height - 48));

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
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        Utility.drawTextWithShadow(b, this.drive.DisplayName, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 26), Game1.textColor);

        var views = this.storageDriveService.GetSlotViews(this.drive);
        this.DrawSlots(b, views);
        this.DrawSummary(b);
        this.DrawHoverTooltip(b, views);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        var slotIndex = this.HitSlot(x, y);
        if (slotIndex < 0)
            return;

        if (this.storageDriveService.TryEjectCellSlot(this.drive, this.location, this.tile, slotIndex, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
            Game1.playSound("Ship");
        }
        else
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            Game1.playSound("cancel");
        }
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
            if (view?.Item is null)
                continue;

            view.Item.drawInMenu(
                b,
                new Vector2(cell.X + SVSAPMenuWidgets.IconInset, cell.Y + SVSAPMenuWidgets.IconInset),
                1f,
                1f,
                0.86f,
                StackDrawType.Hide,
                Color.White,
                true);

            var ratio = view.CapacityMax <= 0 ? 0f : Math.Clamp(view.CapacityUsed / (float)view.CapacityMax, 0f, 1f);
            var bar = new Rectangle(cell.X + 6, cell.Bottom - 9, cell.Width - 12, 4);
            b.Draw(Game1.staminaRect, bar, Color.Black * 0.35f);
            b.Draw(Game1.staminaRect, new Rectangle(bar.X, bar.Y, (int)(bar.Width * ratio), bar.Height), ratio >= 0.9f ? Color.OrangeRed : Color.LightGreen);
        }
    }

    private void DrawSummary(SpriteBatch b)
    {
        var lines = this.storageDriveService.DescribeDrive(this.drive);
        var x = this.slotArea.Right + 32;
        var y = this.slotArea.Y;
        var maxWidth = this.xPositionOnScreen + this.width - Pad - x;
        foreach (var line in lines.Take(10))
        {
            var text = line.Length > 48 ? line[..48] + "..." : line;
            b.DrawString(Game1.smallFont, text, new Vector2(x, y), Game1.textColor);
            y += 28;
            if (y > this.yPositionOnScreen + this.height - 72)
                break;
        }

        b.DrawString(
            Game1.smallFont,
            ModText.Get("ui.storageDrive.guiHelp"),
            new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + this.height - 58),
            Color.DimGray,
            0f,
            Vector2.Zero,
            maxWidth > 0 ? 1f : 1f,
            SpriteEffects.None,
            1f);
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
}

internal readonly record struct StorageDriveMenuLayoutShape(int Columns, int Rows);
