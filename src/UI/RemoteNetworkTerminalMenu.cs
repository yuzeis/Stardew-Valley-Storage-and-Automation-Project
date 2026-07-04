using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemoteNetworkTerminalMenu : IClickableMenu
{
    private readonly Func<TerminalActionRequestMessage, TerminalSnapshotResponseMessage, bool> sendRequest;
    private TerminalSnapshotResponseMessage snapshot;
    private readonly List<ClickableComponent> categoryButtons = new();
    private readonly List<ClickableComponent> qualityButtons = new();
    private readonly List<ClickableComponent> sortButtons = new();
    private readonly List<ClickableComponent> depositButtons = new();
    private ClickableComponent toggleLockButton = null!;
    private readonly SVSAPIconGrid<RemoteInventoryEntryMessage> itemGrid = new();
    private readonly SVSAPBackpackGrid backpackGrid = new();
    private Rectangle searchBox;
    private Rectangle gridArea;
    private Rectangle invArea;
    private string search = string.Empty;
    private TerminalInventoryCategory selectedCategory = TerminalInventoryCategory.All;
    private int? selectedQuality;
    private TerminalInventorySortMode sortMode = TerminalInventorySortMode.Count;

    public RemoteNetworkTerminalMenu(TerminalSnapshotResponseMessage snapshot, Func<TerminalActionRequestMessage, TerminalSnapshotResponseMessage, bool> sendRequest)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.snapshot = snapshot;
        this.sendRequest = sendRequest;
        this.BuildLayout();
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private static int GetMenuWidth() => Math.Min(1120, Game1.uiViewport.Width - 80);

    private static int GetMenuHeight() => Math.Min(820, Game1.uiViewport.Height - 80);

    private void BuildLayout()
    {
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var innerW = this.width - SVSAPMenuWidgets.Pad * 2;
        var top = this.yPositionOnScreen + 24;
        this.searchBox = new Rectangle(innerX, top + 44, 380, 40);

        var bx = innerX;
        var catY = top + 96;
        foreach (var category in TerminalInventoryFilters.CategoryOrder)
        {
            this.categoryButtons.Add(new ClickableComponent(
                new Rectangle(bx, catY, 100, 34),
                category.ToString(),
                TerminalInventoryFilters.GetLabel(category)));
            bx += 104;
        }

        var filterY = top + 136;
        bx = innerX;
        foreach (var quality in new int?[] { null, 0, 1, 2, 4 })
        {
            this.qualityButtons.Add(new ClickableComponent(
                new Rectangle(bx, filterY, 66, 34),
                quality?.ToString() ?? "All",
                TerminalInventoryFilters.GetQualityLabel(quality)));
            bx += 70;
        }

        bx += 28;
        foreach (var sort in TerminalInventoryFilters.SortOrder)
        {
            this.sortButtons.Add(new ClickableComponent(
                new Rectangle(bx, filterY, 84, 34),
                sort.ToString(),
                TerminalInventoryFilters.GetLabel(sort)));
            bx += 88;
        }

        var bottomY = this.yPositionOnScreen + this.height - 54;
        this.depositButtons.Add(new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad - 130, bottomY, 130, 42), "all", ModText.Get("terminal.depositAll")));
        this.depositButtons.Add(new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad - 288, bottomY, 150, 42), "same", ModText.Get("terminal.depositSame")));
        this.toggleLockButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad - 396, bottomY, 100, 42), "lock", ModText.Get("terminal.lock"));

        var invH = SVSAPBackpackGrid.GetHeight();
        var invTop = bottomY - 18 - invH;
        var invW = SVSAPMenuWidgets.BackpackColumns * SVSAPMenuWidgets.Cell;
        this.invArea = new Rectangle(innerX + Math.Max(0, (innerW - invW) / 2), invTop, invW, invH);
        this.backpackGrid.SetBounds(this.invArea);

        var gridTop = filterY + 50;
        var gridBottom = invTop - 16;
        this.gridArea = new Rectangle(innerX, gridTop, innerW, Math.Max(SVSAPMenuWidgets.Cell, gridBottom - gridTop));
        this.itemGrid.SetBounds(this.gridArea);
    }

    public bool MatchesNetwork(Guid networkId)
    {
        return this.snapshot.NetworkId == networkId;
    }

    public void ApplySnapshot(TerminalSnapshotResponseMessage updatedSnapshot)
    {
        this.snapshot = updatedSnapshot;
        this.itemGrid.ClampScroll(this.GetVisibleEntries().Count);
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var visibleEntries = this.GetVisibleEntries();
        this.itemGrid.ClampScroll(visibleEntries.Count);
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + 24;
        var title = ModText.Format("remoteTerminal.title", this.snapshot.NetworkName, visibleEntries.Count, this.snapshot.Entries.Count, this.snapshot.SourceCount);
        b.DrawString(Game1.dialogueFont, title, new Vector2(innerX, top), Game1.textColor);
        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);
        b.DrawString(
            Game1.smallFont,
            TerminalInventoryFilters.FormatStorageSummary(this.snapshot.StorageSummary),
            new Vector2(this.searchBox.Right + 24, this.searchBox.Y + 10),
            Game1.textColor);

        foreach (var button in this.categoryButtons)
        {
            var selected = Enum.TryParse<TerminalInventoryCategory>(button.name, out var category) && category == this.selectedCategory;
            SVSAPMenuWidgets.DrawTab(b, button, selected);
        }

        foreach (var button in this.qualityButtons)
        {
            var selected = button.name == (this.selectedQuality?.ToString() ?? "All");
            SVSAPMenuWidgets.DrawTab(b, button, selected);
        }

        foreach (var button in this.sortButtons)
        {
            var selected = Enum.TryParse<TerminalInventorySortMode>(button.name, out var sort) && sort == this.sortMode;
            SVSAPMenuWidgets.DrawTab(b, button, selected);
        }

        this.itemGrid.Draw(
            b,
            visibleEntries,
            entry => SVSAPMenuWidgets.CreateIconItem(entry.QualifiedItemId, entry.SerializedItemPrototype),
            entry => entry.AvailableCount,
            entry => entry.AvailableCount <= 0);

        if (visibleEntries.Count == 0)
            b.DrawString(Game1.smallFont, ModText.Get("remoteTerminal.empty"), new Vector2(this.gridArea.X + 8, this.gridArea.Y + 8), Color.DarkSlateGray);

        SVSAPMenuWidgets.DrawSeparator(b, new Rectangle(innerX, this.invArea.Y - 10, this.width - SVSAPMenuWidgets.Pad * 2, 2));
        this.backpackGrid.Draw(b);

        b.DrawString(
            Game1.smallFont,
            TerminalInventoryFilters.FormatLockedList(this.snapshot.LockedQualifiedItemIds),
            new Vector2(innerX, this.invArea.Y - 38),
            Game1.textColor);
        b.DrawString(
            Game1.smallFont,
            ModText.Get("remoteTerminal.help"),
            new Vector2(innerX, this.depositButtons[0].bounds.Y + 12),
            Color.DimGray);

        SVSAPMenuWidgets.DrawButton(b, this.toggleLockButton, this.GetLockButtonLabel());
        foreach (var button in this.depositButtons)
            SVSAPMenuWidgets.DrawButton(b, button);

        this.DrawHoverTooltip(b, visibleEntries);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        foreach (var button in this.categoryButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (Enum.TryParse<TerminalInventoryCategory>(button.name, out var category))
                this.selectedCategory = category;

            this.ResetScroll();
            Game1.playSound("smallSelect");
            return;
        }

        foreach (var button in this.qualityButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            this.selectedQuality = int.TryParse(button.name, out var quality) ? quality : null;
            this.ResetScroll();
            Game1.playSound("smallSelect");
            return;
        }

        foreach (var button in this.sortButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (Enum.TryParse<TerminalInventorySortMode>(button.name, out var sort))
                this.sortMode = sort;

            this.ResetScroll();
            Game1.playSound("smallSelect");
            return;
        }

        if (this.toggleLockButton.containsPoint(x, y))
        {
            var held = Game1.player.CurrentItem;
            var sent = this.sendRequest(new TerminalActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = this.snapshot.NetworkId,
                EndpointId = this.snapshot.EndpointId,
                Action = TerminalActionKind.ToggleHeldItemLock,
                HeldQualifiedItemId = held?.QualifiedItemId ?? string.Empty,
                HeldDisplayName = held?.DisplayName ?? string.Empty
            }, this.snapshot);
            Game1.playSound(sent ? "smallSelect" : "cancel");
            return;
        }

        foreach (var button in this.depositButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            var sent = this.sendRequest(new TerminalActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = this.snapshot.NetworkId,
                EndpointId = this.snapshot.EndpointId,
                Action = button.name == "same" ? TerminalActionKind.DepositSame : TerminalActionKind.DepositAll
            }, this.snapshot);
            Game1.playSound(sent ? "smallSelect" : "cancel");
            return;
        }

        var visibleEntries = this.GetVisibleEntries();
        this.itemGrid.ClampScroll(visibleEntries.Count);
        var entry = this.itemGrid.HitTest(x, y, visibleEntries);
        if (entry is null)
            return;

        if (entry.AvailableCount <= 0)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("terminal.reserved"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        this.SendWithdraw(entry, GetStackWithdrawAmount(entry));
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        var visibleEntries = this.GetVisibleEntries();
        this.itemGrid.ClampScroll(visibleEntries.Count);
        var entry = this.itemGrid.HitTest(x, y, visibleEntries);
        if (entry is null)
            return;

        if (entry.AvailableCount <= 0)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("terminal.reserved"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        this.SendWithdraw(entry, 1);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            this.exitThisMenu();
            return;
        }

        if (key == Keys.Back)
        {
            if (this.search.Length > 0)
            {
                this.search = this.search[..^1];
                this.ResetScroll();
            }
            return;
        }

        var typed = SVSAPMenuWidgets.TryConvertKey(key);
        if (typed is not null && this.search.Length < 40)
        {
            this.search += typed.Value;
            this.ResetScroll();
            return;
        }

        base.receiveKeyPress(key);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        this.itemGrid.ScrollOffset += direction > 0 ? -1 : 1;
        this.itemGrid.ClampScroll(this.GetVisibleEntries().Count);
    }

    private void SendWithdraw(RemoteInventoryEntryMessage entry, int amount)
    {
        var sent = this.sendRequest(new TerminalActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            NetworkId = this.snapshot.NetworkId,
            EndpointId = this.snapshot.EndpointId,
            Action = TerminalActionKind.Withdraw,
            ItemKey = entry.Key,
            Amount = Math.Max(1, amount)
        }, this.snapshot);
        Game1.playSound(sent ? "smallSelect" : "cancel");
    }

    private static int GetStackWithdrawAmount(RemoteInventoryEntryMessage entry)
    {
        var prototype = SVSAPMenuWidgets.CreateIconItem(entry.QualifiedItemId, entry.SerializedItemPrototype);
        var maxStack = prototype?.maximumStackSize() ?? 999;
        var amount = (int)Math.Min(Math.Max(1, maxStack), entry.AvailableCount);
        return amount <= 0 ? 1 : amount;
    }

    private List<RemoteInventoryEntryMessage> GetVisibleEntries()
    {
        var entries = this.snapshot.Entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            entries = entries.Where(entry => entry.DisplayName.Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || entry.Name.Contains(this.search, StringComparison.OrdinalIgnoreCase)
                || entry.QualifiedItemId.Contains(this.search, StringComparison.OrdinalIgnoreCase));
        }

        entries = entries.Where(entry => TerminalInventoryFilters.MatchesCategory(entry.Category, this.selectedCategory));

        if (this.selectedQuality is not null)
            entries = entries.Where(entry => entry.Quality == this.selectedQuality.Value);

        entries = this.sortMode switch
        {
            TerminalInventorySortMode.Name => entries.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Price => entries
                .OrderByDescending(entry => entry.SalePrice)
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Category => entries
                .OrderBy(entry => TerminalInventoryFilters.GetLabel(entry.Category), StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Recent => entries
                .OrderByDescending(entry => entry.LastAddedSequence)
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => entries
                .OrderByDescending(entry => entry.AvailableCount)
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

        return entries.ToList();
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<RemoteInventoryEntryMessage> visibleEntries)
    {
        var mouseX = Game1.getMouseX();
        var mouseY = Game1.getMouseY();
        var entry = this.itemGrid.HitTest(mouseX, mouseY, visibleEntries);
        if (entry is null)
            return;

        var lines = new List<string>
        {
            ModText.Format("terminal.tooltip.count", entry.AvailableCount, entry.ReservedCount > 0 ? ModText.Format("terminal.tooltip.reserved", entry.ReservedCount) : string.Empty),
            ModText.Format("terminal.tooltip.qualityPrice", entry.Quality, entry.SalePrice)
        };
        lines.AddRange(entry.Locations
            .OrderByDescending(location => location.Count)
            .Take(5)
            .Select(FormatLocationLine));
        if (entry.Locations.Count > 5)
            lines.Add(ModText.Format("terminal.tooltip.moreStacks", entry.Locations.Count - 5));

        SVSAPMenuWidgets.DrawTooltipBox(b, mouseX + 28, mouseY + 28, entry.DisplayName, lines);
    }

    private static string FormatLocationLine(RemoteItemStackLocationMessage location)
    {
        var source = location.SourceKind == InventorySourceKind.StorageCell ? ModText.Get("terminal.source.cell") : ModText.Get("terminal.source.chest");
        return ModText.Format("remoteTerminal.location", source, location.LocationName, location.TileX, location.TileY, location.SlotIndex, location.Count);
    }

    private void ResetScroll()
    {
        this.itemGrid.ResetScroll();
    }

    private string GetLockButtonLabel()
    {
        var held = Game1.player.CurrentItem;
        if (held is null)
            return ModText.Get("terminal.lock");

        return this.snapshot.LockedQualifiedItemIds.Contains(held.QualifiedItemId, StringComparer.Ordinal)
            ? ModText.Get("terminal.unlock")
            : ModText.Get("terminal.lock");
    }
}
