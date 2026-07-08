using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemoteStorageDriveMenu : IClickableMenu
{
    private const int Pad = 28;
    private const int SlotCount = 10;
    private const int SlotColumns = 5;
    private readonly Action<int> ejectSlot;
    private readonly Action requestRefresh;
    private Rectangle slotArea;

    public RemoteStorageDriveMenu(StructuralSnapshotResponseMessage snapshot, Action<int> ejectSlot, Action requestRefresh)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - 760) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - 500) / 2),
            width: Math.Min(760, Math.Max(520, Game1.uiViewport.Width - 48)),
            height: Math.Min(500, Math.Max(420, Game1.uiViewport.Height - 48)),
            showUpperRightCloseButton: true)
    {
        this.Snapshot = snapshot;
        this.ejectSlot = ejectSlot;
        this.requestRefresh = requestRefresh;
        this.slotArea = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 112, SlotColumns * SVSAPMenuWidgets.Cell, 2 * SVSAPMenuWidgets.Cell);
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    internal StructuralSnapshotResponseMessage Snapshot { get; private set; }

    public void ApplySnapshot(StructuralSnapshotResponseMessage snapshot)
    {
        if (snapshot.Kind == StructuralSnapshotKind.StorageDrive && snapshot.StorageDrive is not null)
            this.Snapshot = snapshot;
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        Utility.drawTextWithShadow(b, this.Snapshot.DisplayName, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 26), Game1.textColor);

        var slots = this.Snapshot.StorageDrive?.Slots ?? new List<RemoteStorageDriveSlotMessage>();
        this.DrawSlots(b, slots);
        this.DrawSummary(b);
        this.DrawHoverTooltip(b, slots);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        var slotIndex = this.HitSlot(x, y);
        if (slotIndex < 0)
            return;

        var view = this.Snapshot.StorageDrive?.Slots.FirstOrDefault(slot => slot.SlotIndex == slotIndex);
        if (view?.Occupied != true)
            return;

        this.ejectSlot(slotIndex);
        Game1.playSound("smallSelect");
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
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
            if (y > this.yPositionOnScreen + this.height - 72)
                break;
        }
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
}
