using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Content;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace SVSAP.UI;

internal sealed class TransferBusMenu : IClickableMenu
{
    private const int Pad = SVSAPMenuWidgets.Pad;
    private const int FilterCell = 64;
    private const int FilterColumns = 3;
    private const int FilterRows = 3;
    private const int UpgradeColumns = 8;
    private const int UpgradeRows = 1;
    private const int UpgradeCell = 46;
    private const int ControlButtonWidth = 150;
    private const int ControlButtonHeight = 42;
    private const int ControlButtonGap = 10;
    private const int ControlRowGap = 12;
    private const int DirectionButtonMaxWidth = 80;
    private const int DirectionButtonMinWidth = 48;
    private const int DirectionButtonGap = 6;

    private readonly SObject bus;
    private readonly TransferBusService transferBusService;
    private const int BackpackCell = 48;
    private readonly SVSAPBackpackGrid backpackGrid = new(BackpackCell);
    private readonly List<ClickableComponent> directionButtons = new();
    private ClickableComponent modeButton = null!;
    private ClickableComponent oreButton = null!;
    private ClickableComponent qualityButton = null!;
    private ClickableComponent clearButton = null!;
    private Rectangle filterArea;
    private Rectangle upgradeArea;
    private Rectangle invArea;
    private int selectedSlot;
    private int selectedUpgradeSlot = -1;

    public TransferBusMenu(SObject bus, TransferBusService transferBusService)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.bus = bus;
        this.transferBusService = transferBusService;
        this.BuildLayout();
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private static int GetMenuWidth() => Math.Max(1, Math.Min(1040, Game1.uiViewport.Width - 48));

    private static int GetMenuHeight() => Math.Max(1, Math.Min(760, Game1.uiViewport.Height - 48));

    private void BuildLayout()
    {
        var innerX = this.xPositionOnScreen + Pad;
        var innerW = this.width - Pad * 2;
        var contentTop = this.yPositionOnScreen + SVSAPMenuWidgets.ContentTopOffset;

        this.filterArea = new Rectangle(innerX, contentTop + 56, FilterColumns * FilterCell, FilterRows * FilterCell);
        this.upgradeArea = new Rectangle(innerX, contentTop, UpgradeColumns * UpgradeCell, UpgradeRows * UpgradeCell);
        var controlsX = this.filterArea.Right + 34;
        var controlsY = this.filterArea.Y;
        var controlsAvailable = Math.Max(1, this.xPositionOnScreen + this.width - Pad - controlsX);
        var controlColumns = CalculateControlColumns(controlsAvailable);
        var controlWidth = CalculateControlButtonWidth(controlsAvailable, controlColumns);
        this.modeButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 0, controlColumns, controlWidth), "mode", string.Empty);
        this.oreButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 1, controlColumns, controlWidth), "ore", string.Empty);
        this.qualityButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 2, controlColumns, controlWidth), "quality", ModText.Get("ui.transferBus.action.toggleQuality"));
        this.clearButton = new ClickableComponent(GetControlButtonBounds(controlsX, controlsY, 3, controlColumns, controlWidth), "clear", ModText.Get("ui.transferBus.action.clearFilter"));

        this.directionButtons.Clear();
        var dirY = controlsY + GetControlRows(4, controlColumns) * (ControlButtonHeight + ControlRowGap) + 10;
        var directionButtonWidth = CalculateDirectionButtonWidth(controlsAvailable);
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

        var backpackColumns = SVSAPBackpackGrid.GetColumnCount(innerW, BackpackCell);
        var invH = SVSAPBackpackGrid.GetHeight(backpackColumns, BackpackCell);
        var invW = backpackColumns * BackpackCell;
        this.invArea = new Rectangle(innerX + Math.Max(0, (innerW - invW) / 2), this.yPositionOnScreen + this.height - Pad - invH, invW, invH);
        this.backpackGrid.SetBounds(this.invArea);
    }

    internal static bool LayoutFits(int menuWidth, int menuHeight = 640, int inventorySlotCount = 36)
    {
        var controlsX = Pad + FilterColumns * FilterCell + 34;
        var controlsAvailable = menuWidth - Pad - controlsX;
        var controlColumns = CalculateControlColumns(controlsAvailable);
        var controlWidth = CalculateControlButtonWidth(controlsAvailable, controlColumns);
        var directionWidth = CalculateDirectionButtonWidth(controlsAvailable);
        var filterRight = Pad + FilterColumns * FilterCell;
        var upgradeRight = Pad + UpgradeColumns * UpgradeCell;
        var controlRight = controlsX + Math.Min(controlColumns, 3) * controlWidth + Math.Max(0, Math.Min(controlColumns, 3) - 1) * ControlButtonGap;
        var directionRight = controlsX + 5 * directionWidth + 4 * DirectionButtonGap;
        var backpackColumns = SVSAPBackpackGrid.GetColumnCount(menuWidth - Pad * 2, BackpackCell);
        var backpackRows = Math.Max(1, (int)Math.Ceiling(Math.Max(1, inventorySlotCount) / (double)backpackColumns));
        var inventoryTop = menuHeight - Pad - backpackRows * BackpackCell;
        var filterTop = SVSAPMenuWidgets.ContentTopOffset + 56;
        var filterBottom = filterTop + FilterRows * FilterCell;
        var upgradeBottom = SVSAPMenuWidgets.ContentTopOffset + UpgradeRows * UpgradeCell;
        return filterRight <= menuWidth - Pad
            && upgradeRight <= menuWidth - Pad
            && controlRight <= menuWidth - Pad
            && directionRight <= menuWidth - Pad
            && upgradeBottom + 10 <= filterTop
            && filterBottom + 12 <= inventoryTop
            && controlColumns >= 1
            && directionWidth >= DirectionButtonMinWidth;
    }

    private static int CalculateControlColumns(int availableWidth)
    {
        if (availableWidth >= ControlButtonWidth * 3 + ControlButtonGap * 2)
            return 3;
        if (availableWidth >= ControlButtonWidth * 2 + ControlButtonGap)
            return 2;
        return 1;
    }

    private static int CalculateControlButtonWidth(int availableWidth, int columns)
    {
        var fitWidth = (availableWidth - Math.Max(0, columns - 1) * ControlButtonGap) / Math.Max(1, columns);
        return Math.Clamp(fitWidth, 72, ControlButtonWidth);
    }

    private static int CalculateDirectionButtonWidth(int availableWidth)
    {
        var fitWidth = (availableWidth - DirectionButtonGap * 4) / 5;
        return Math.Clamp(fitWidth, DirectionButtonMinWidth, DirectionButtonMaxWidth);
    }

    private static int GetControlRows(int count, int columns)
    {
        return Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, columns)));
    }

    private static Rectangle GetControlButtonBounds(int x, int y, int index, int columns, int width)
    {
        var column = index % Math.Max(1, columns);
        var row = index / Math.Max(1, columns);
        return new Rectangle(
            x + column * (width + ControlButtonGap),
            y + row * (ControlButtonHeight + ControlRowGap),
            width,
            ControlButtonHeight);
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        SVSAPMenuWidgets.DrawFittedTitle(
            b,
            this.bus.DisplayName,
            new Rectangle(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 18, this.width - Pad * 2 - 70, 36),
            Game1.textColor);

        var slots = this.transferBusService.GetFilterSlotViews(this.bus);
        var upgrades = this.transferBusService.GetUpgradeSlotViews(this.bus);
        this.DrawFilterSlots(b, slots);
        this.DrawUpgradeSlots(b, upgrades);
        this.DrawControls(b);
        SVSAPMenuWidgets.DrawSeparator(b, new Rectangle(this.xPositionOnScreen + Pad, this.invArea.Y - 12, this.width - Pad * 2, 2));
        this.backpackGrid.Draw(b);
        this.DrawHoverTooltip(b, slots, upgrades);
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

        if (this.modeButton.containsPoint(x, y))
        {
            this.RunAction(() =>
            {
                var success = this.transferBusService.TryToggleFilterMode(this.bus, out var message);
                return (success, message);
            });
            return;
        }

        if (this.oreButton.containsPoint(x, y))
        {
            this.RunAction(() =>
            {
                var success = this.transferBusService.TryToggleOreDictionaryMode(this.bus, out var message);
                return (success, message);
            });
            return;
        }

        if (this.qualityButton.containsPoint(x, y))
        {
            this.RunAction(() =>
            {
                var success = this.transferBusService.TryToggleQualityStrategy(this.bus, out var message);
                return (success, message);
            });
            return;
        }

        if (this.clearButton.containsPoint(x, y))
        {
            this.RunAction(() =>
            {
                var success = this.transferBusService.TryClearFilter(this.bus, out var message);
                return (success, message);
            });
            return;
        }

        foreach (var button in this.directionButtons)
        {
            if (!button.containsPoint(x, y) || !int.TryParse(button.name, out var direction))
                continue;

            this.RunAction(() =>
            {
                var success = this.transferBusService.TrySetFacingDirection(this.bus, direction, out var message);
                return (success, message);
            });
            return;
        }

        var upgradeSlot = this.HitUpgradeSlot(x, y);
        if (upgradeSlot >= 0)
        {
            this.selectedUpgradeSlot = upgradeSlot;
            this.selectedSlot = -1;
            var view = this.transferBusService.GetUpgradeSlotViews(this.bus).FirstOrDefault(slot => slot.SlotIndex == upgradeSlot);
            if (view?.Occupied == true)
            {
                this.RunAction(() =>
                {
                    var success = this.transferBusService.TryEjectUpgradeSlot(this.bus, upgradeSlot, out var message);
                    return (success, message);
                });
            }
            else
            {
                Game1.playSound("smallSelect");
            }
            return;
        }

        var filterSlot = this.HitFilterSlot(x, y);
        if (filterSlot >= 0)
        {
            this.selectedSlot = filterSlot;
            this.selectedUpgradeSlot = -1;
            Game1.playSound("smallSelect");
            return;
        }

        var inventoryIndex = this.backpackGrid.HitTest(x, y);
        if (inventoryIndex >= 0)
            this.SetFilterFromInventory(inventoryIndex);
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        var upgradeSlot = this.HitUpgradeSlot(x, y);
        if (upgradeSlot >= 0)
        {
            this.RunAction(() =>
            {
                var success = this.transferBusService.TryEjectUpgradeSlot(this.bus, upgradeSlot, out var message);
                return (success, message);
            });
            return;
        }

        var filterSlot = this.HitFilterSlot(x, y);
        if (filterSlot >= 0)
        {
            this.RunAction(() =>
            {
                var success = this.transferBusService.TryClearFilterSlot(this.bus, filterSlot, out var message);
                return (success, message);
            });
            return;
        }

        base.receiveRightClick(x, y, playSound);
    }

    private void DrawFilterSlots(SpriteBatch b, IReadOnlyList<TransferFilterSlotView> slots)
    {
        for (var index = 0; index < FilterColumns * FilterRows; index++)
        {
            var cell = this.GetFilterSlotBounds(index);
            var selected = index == this.selectedSlot;
            SVSAPMenuWidgets.DrawSlotBackground(b, cell);
            var view = slots.FirstOrDefault(slot => slot.SlotIndex == index);
            SVSAPMenuWidgets.DrawSlotStatusLine(b, cell, view?.Occupied == true ? PixelStatus.Ready : PixelStatus.Idle);
            if (selected)
                DrawSelection(b, cell);

            SVSAPMenuWidgets.DrawItemInSlot(b, view?.Item, cell, 1, tint: Color.White * 0.58f);
        }
    }

    private void DrawUpgradeSlots(SpriteBatch b, IReadOnlyList<TransferUpgradeSlotView> slots)
    {
        for (var index = 0; index < TransferBusService.UpgradeSlotCount; index++)
        {
            var bounds = this.GetUpgradeSlotBounds(index);
            var view = slots.FirstOrDefault(slot => slot.SlotIndex == index);
            SVSAPMenuWidgets.DrawSlotBackground(b, bounds, view?.Occupied != true);
            SVSAPMenuWidgets.DrawSlotStatusLine(b, bounds, view?.Occupied == true ? PixelStatus.Ready : PixelStatus.Idle);
            if (index == this.selectedUpgradeSlot)
                DrawSelection(b, bounds);
            if (view?.Item is not null)
                SVSAPMenuWidgets.DrawItemInSlot(b, view.Item, bounds, 1, 0.58f);
            else
                SVSAPMenuWidgets.DrawGhostUpgradeSlot(b, bounds, "module");
        }
    }

    private void DrawControls(SpriteBatch b)
    {
        var blacklist = this.transferBusService.IsFilterBlacklistModeEnabled(this.bus);
        this.modeButton.label = blacklist
            ? ModText.Get("ui.transferBus.mode.blacklist")
            : ModText.Get("ui.transferBus.mode.whitelist");
        this.oreButton.label = this.transferBusService.IsOreDictionaryModeEnabled(this.bus)
            ? ModText.Get("ui.transferBus.oreDictionaryOnShort")
            : ModText.Get("ui.transferBus.oreDictionaryOffShort");

        this.qualityButton.label = this.transferBusService.GetConfiguredQualityStrategy(this.bus).ToString();
        SVSAPMenuWidgets.DrawButton(b, this.modeButton, tint: blacklist ? Color.Orange : Color.LightGreen);
        var hasOreCard = this.transferBusService.HasInstalledUpgrade(this.bus, "(O)" + ModItemCatalog.OreDictionaryCard);
        var hasQualityCard = this.transferBusService.HasInstalledUpgrade(this.bus, "(O)" + ModItemCatalog.QualityCard);
        SVSAPMenuWidgets.DrawButton(b, this.oreButton, tint: !hasOreCard ? Color.Gray : this.transferBusService.IsOreDictionaryModeEnabled(this.bus) ? Color.LightGreen : Color.White);
        SVSAPMenuWidgets.DrawButton(b, this.qualityButton, tint: hasQualityCard ? Color.White : Color.Gray);
        SVSAPMenuWidgets.DrawButton(b, this.clearButton);

        var facing = this.transferBusService.GetFacingDirection(this.bus);
        foreach (var button in this.directionButtons)
        {
            var selected = int.TryParse(button.name, out var direction) && direction == facing;
            SVSAPMenuWidgets.DrawButton(b, button, tint: selected ? Color.LightGreen : Color.White);
        }

        var lines = this.transferBusService.DescribeConfigurationLines(this.bus).ToList();
        var x = this.filterArea.Right + 34;
        var y = this.filterArea.Bottom + 18;
        var runtime = this.transferBusService.GetRuntimeStatus(this.bus);
        var runtimeStatus = !runtime.Linked
            ? PixelStatus.Warning
            : !runtime.Active
                ? PixelStatus.Offline
                : runtime.Ready
                    ? PixelStatus.Ready
                    : PixelStatus.Warning;
        SVSAPMenuWidgets.DrawPixelStatusLight(b, x + 2, y + 7, runtimeStatus);
        SVSAPMenuWidgets.DrawFittedLine(
            b,
            runtime.Message,
            new Rectangle(x + 22, y, this.xPositionOnScreen + this.width - Pad - x - 22, 22),
            runtime.Ready ? Game1.textColor : Color.DarkRed);
        y += 28;
        var maxLines = Math.Max(0, (this.invArea.Y - 18 - y) / 24);
        foreach (var line in lines.Take(maxLines))
        {
            SVSAPMenuWidgets.DrawFittedLine(
                b,
                line,
                new Rectangle(x, y, this.xPositionOnScreen + this.width - Pad - x, 22),
                Game1.textColor);
            y += 24;
        }
    }

    private void DrawHoverTooltip(
        SpriteBatch b,
        IReadOnlyList<TransferFilterSlotView> slots,
        IReadOnlyList<TransferUpgradeSlotView> upgrades)
    {
        var mx = Game1.getMouseX();
        var my = Game1.getMouseY();
        var slotIndex = this.HitFilterSlot(mx, my);
        if (slotIndex >= 0)
        {
            var view = slots.FirstOrDefault(slot => slot.SlotIndex == slotIndex);
            if (view?.Occupied == true)
            {
                var lines = new List<string>();
                if (view.OreGroups.Count > 0)
                    lines.Add(ModText.Format("ui.transferBus.tooltip.oreGroups", string.Join(", ", view.OreGroups)));
                SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, view.DisplayName, lines);
            }
            return;
        }

        var upgradeIndex = this.HitUpgradeSlot(mx, my);
        if (upgradeIndex >= 0)
        {
            var upgrade = upgrades.FirstOrDefault(slot => slot.SlotIndex == upgradeIndex);
            var title = upgrade?.Occupied == true
                ? upgrade.DisplayName
                : ModText.Get("ui.transferBus.upgradeEmpty");
            SVSAPMenuWidgets.DrawTooltipBox(
                b,
                mx + 28,
                my + 28,
                title,
                new[] { ModText.Get("ui.transferBus.upgradeHint") });
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

        if (TransferBusService.IsUpgradeCard(item.QualifiedItemId))
        {
            var target = this.FindTargetUpgradeSlot();
            this.RunAction(() =>
            {
                var success = this.transferBusService.TryInsertUpgradeSlot(this.bus, target, item, out var message);
                return (success, message);
            });
            return;
        }

        if (TransferBusService.IsConfigurationCard(item.QualifiedItemId))
        {
            this.RunAction(() =>
            {
                var success = this.transferBusService.TryApplyConfigurationItem(this.bus, item, out var message);
                return (success, message);
            });
            return;
        }

        var slot = this.FindTargetFilterSlot();
        if (this.RunAction(() =>
        {
            var success = this.transferBusService.TrySetFilterSlot(this.bus, slot, item.QualifiedItemId, out var message);
            return (success, message);
        }))
        {
            this.selectedSlot = Math.Min(slot + 1, FilterColumns * FilterRows - 1);
        }
    }

    private int FindTargetFilterSlot()
    {
        var slots = this.transferBusService.GetFilterSlotViews(this.bus);
        if (this.selectedSlot >= 0 && this.selectedSlot < FilterColumns * FilterRows)
            return this.selectedSlot;

        return slots.FirstOrDefault(slot => !slot.Occupied)?.SlotIndex ?? 0;
    }

    private int FindTargetUpgradeSlot()
    {
        var slots = this.transferBusService.GetUpgradeSlotViews(this.bus);
        if (this.selectedUpgradeSlot >= 0
            && this.selectedUpgradeSlot < TransferBusService.UpgradeSlotCount
            && slots.Any(slot => slot.SlotIndex == this.selectedUpgradeSlot && !slot.Occupied))
        {
            return this.selectedUpgradeSlot;
        }
        return slots.FirstOrDefault(slot => !slot.Occupied)?.SlotIndex ?? -1;
    }

    private int HitFilterSlot(int x, int y)
    {
        if (!this.filterArea.Contains(x, y))
            return -1;

        var column = (x - this.filterArea.X) / FilterCell;
        var row = (y - this.filterArea.Y) / FilterCell;
        if (column < 0 || column >= FilterColumns || row < 0 || row >= FilterRows)
            return -1;

        return row * FilterColumns + column;
    }

    private Rectangle GetFilterSlotBounds(int index)
    {
        var column = index % FilterColumns;
        var row = index / FilterColumns;
        return new Rectangle(
            this.filterArea.X + column * FilterCell,
            this.filterArea.Y + row * FilterCell,
            FilterCell - 4,
            FilterCell - 4);
    }

    private int HitUpgradeSlot(int x, int y)
    {
        if (!this.upgradeArea.Contains(x, y))
            return -1;
        var column = (x - this.upgradeArea.X) / UpgradeCell;
        var row = (y - this.upgradeArea.Y) / UpgradeCell;
        var index = row * UpgradeColumns + column;
        return index is >= 0 and < TransferBusService.UpgradeSlotCount ? index : -1;
    }

    private Rectangle GetUpgradeSlotBounds(int index)
    {
        return new Rectangle(
            this.upgradeArea.X + index % UpgradeColumns * UpgradeCell,
            this.upgradeArea.Y + index / UpgradeColumns * UpgradeCell,
            UpgradeCell - 4,
            UpgradeCell - 4);
    }

    private bool RunAction(Func<(bool Success, string Message)> action)
    {
        var result = action();
        if (!string.IsNullOrWhiteSpace(result.Message))
            Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        Game1.playSound(result.Success ? "smallSelect" : "cancel");
        return result.Success;
    }

    private static void DrawSelection(SpriteBatch b, Rectangle bounds)
    {
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.X, bounds.Y, 2, bounds.Height), Color.Gold);
        b.Draw(Game1.staminaRect, new Rectangle(bounds.Right - 2, bounds.Y, 2, bounds.Height), Color.Gold);
    }
}
