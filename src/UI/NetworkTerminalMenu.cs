using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

/// <summary>
/// SVSAP ME-terminal style network storage menu: a 9-slice panel with a scrollable item icon
/// grid (network contents) on top and the player's backpack grid on the bottom.
/// </summary>
internal sealed class NetworkTerminalMenu : IClickableMenu
{
    private readonly NetworkData network;
    private readonly InventoryScanner scanner;
    private readonly InventoryTransactionService transactionService;
    private readonly Func<string?> getActionBlockMessage;
    private NetworkInventorySnapshot snapshot;

    private readonly List<ClickableComponent> categoryButtons = new();
    private readonly List<ClickableComponent> qualityButtons = new();
    private readonly List<ClickableComponent> sortButtons = new();
    private ClickableComponent depositAllButton = null!;
    private ClickableComponent depositSameButton = null!;
    private ClickableComponent lockButton = null!;

    private readonly SVSAPIconGrid<NetworkInventoryEntry> itemGrid = new();
    private readonly SVSAPBackpackGrid backpackGrid = new();
    private Rectangle searchBox;
    private Rectangle gridArea;
    private Rectangle invArea;

    private string search = string.Empty;
    private readonly TextBox searchInput;
    private TerminalInventoryCategory selectedCategory = TerminalInventoryCategory.All;
    private int? selectedQuality;
    private TerminalInventorySortMode sortMode = TerminalInventorySortMode.Count;

    public NetworkTerminalMenu(
        NetworkData network,
        InventoryScanner scanner,
        InventoryTransactionService transactionService,
        Func<string?>? getActionBlockMessage = null)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.network = network;
        this.scanner = scanner;
        this.transactionService = transactionService;
        this.getActionBlockMessage = getActionBlockMessage ?? (() => null);
        this.snapshot = this.CreateSnapshot();

        this.BuildLayout();
        this.searchInput = SVSAPMenuWidgets.CreateSearchTextBox(this.searchBox, this.search);
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
        var categoryGap = 4;
        var categoryWidth = Math.Clamp(
            (innerW - categoryGap * (TerminalInventoryFilters.CategoryOrder.Length - 1)) / TerminalInventoryFilters.CategoryOrder.Length,
            58,
            100);
        foreach (var category in TerminalInventoryFilters.CategoryOrder)
        {
            this.categoryButtons.Add(new ClickableComponent(
                new Rectangle(bx, catY, categoryWidth, 34),
                category.ToString(),
                TerminalInventoryFilters.GetLabel(category)));
            bx += categoryWidth + categoryGap;
        }

        var filterY = top + 136;
        bx = innerX;
        var qualityWidth = innerW < 760 ? 56 : 66;
        var sortWidth = innerW < 760 ? 70 : 84;
        foreach (var quality in new int?[] { null, 0, 1, 2, 4 })
        {
            this.qualityButtons.Add(new ClickableComponent(
                new Rectangle(bx, filterY, qualityWidth, 34),
                quality?.ToString() ?? "All",
                TerminalInventoryFilters.GetQualityLabel(quality)));
            bx += qualityWidth + 4;
        }

        bx += innerW < 760 ? 12 : 28;
        foreach (var sort in TerminalInventoryFilters.SortOrder)
        {
            this.sortButtons.Add(new ClickableComponent(
                new Rectangle(bx, filterY, sortWidth, 34),
                sort.ToString(),
                TerminalInventoryFilters.GetLabel(sort)));
            bx += sortWidth + 4;
        }

        var bottomY = this.yPositionOnScreen + this.height - 54;
        var allX = this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad - 130;
        this.depositAllButton = new ClickableComponent(new Rectangle(allX, bottomY, 130, 42), "all", ModText.Get("terminal.depositAll"));
        var sameX = allX - 8 - 150;
        this.depositSameButton = new ClickableComponent(new Rectangle(sameX, bottomY, 150, 42), "same", ModText.Get("terminal.depositSame"));
        var lockX = sameX - 8 - 100;
        this.lockButton = new ClickableComponent(new Rectangle(lockX, bottomY, 100, 42), "lock", ModText.Get("terminal.lock"));

        var backpackColumns = SVSAPBackpackGrid.GetColumnCount(innerW);
        var invH = SVSAPBackpackGrid.GetHeight(backpackColumns);
        var invTop = bottomY - 18 - invH;
        var invW = backpackColumns * SVSAPMenuWidgets.Cell;
        this.invArea = new Rectangle(innerX + Math.Max(0, (innerW - invW) / 2), invTop, invW, invH);
        this.backpackGrid.SetBounds(this.invArea);

        var gridTop = filterY + 50;
        var gridBottom = invTop - 16;
        this.gridArea = new Rectangle(innerX, gridTop, innerW, Math.Max(SVSAPMenuWidgets.Cell, gridBottom - gridTop));
        this.itemGrid.SetBounds(this.gridArea);
    }

    public override void draw(SpriteBatch b)
    {
        this.SyncSearchFromInput();
        var panel = new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height);
        SVSAPMenuWidgets.DrawPanel(b, panel);

        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + 24;
        var entries = this.GetVisibleEntries();
        this.itemGrid.ClampScroll(entries.Count);

        var title = ModText.Format("terminal.title", this.network.Name, entries.Count, this.snapshot.Entries.Count, this.snapshot.SourceCount);
        b.DrawString(Game1.dialogueFont, title, new Vector2(innerX, top), Game1.textColor);

        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);
        b.DrawString(
            Game1.smallFont,
            TerminalInventoryFilters.FormatStorageSummary(this.snapshot.StorageSummary),
            new Vector2(this.searchBox.X + this.searchBox.Width + 24, this.searchBox.Y + 10),
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
            entries,
            entry => entry.Prototype,
            entry => entry.AvailableCount,
            entry => entry.AvailableCount <= 0);
        if (entries.Count == 0)
        {
            b.DrawString(
                Game1.smallFont,
                ModText.Get("terminal.empty"),
                new Vector2(this.gridArea.X + 8, this.gridArea.Y + 8),
                Color.DarkSlateGray);
        }

        SVSAPMenuWidgets.DrawSeparator(b, new Rectangle(innerX, this.invArea.Y - 10, this.width - SVSAPMenuWidgets.Pad * 2, 2));
        this.backpackGrid.Draw(b);

        b.DrawString(
            Game1.smallFont,
            TerminalInventoryFilters.FormatLockedList(this.network.LockedQualifiedItemIds),
            new Vector2(innerX, this.invArea.Y - 38),
            Game1.textColor);
        b.DrawString(
            Game1.smallFont,
            ModText.Get("terminal.help"),
            new Vector2(innerX, this.depositAllButton.bounds.Y + 12),
            Color.DimGray);

        SVSAPMenuWidgets.DrawButton(b, this.lockButton, this.GetLockButtonLabel());
        SVSAPMenuWidgets.DrawButton(b, this.depositSameButton);
        SVSAPMenuWidgets.DrawButton(b, this.depositAllButton);

        this.DrawHoverTooltip(b, entries);

        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        if (this.lockButton.containsPoint(x, y))
        {
            if (this.EnsureActionAllowed())
            {
                this.ToggleHeldItemLock();
                Game1.playSound("smallSelect");
            }
            return;
        }

        if (this.depositAllButton.containsPoint(x, y)) { this.DepositBulk(false); return; }
        if (this.depositSameButton.containsPoint(x, y)) { this.DepositBulk(true); return; }

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

        var invIndex = this.InventoryIndexAt(x, y);
        if (invIndex >= 0)
        {
            this.DepositSlot(invIndex, single: false);
            return;
        }

        var entry = this.GridEntryAt(x, y);
        if (entry != null)
        {
            if (entry.AvailableCount <= 0)
            {
                Game1.addHUDMessage(new HUDMessage(ModText.Get("terminal.reserved"), HUDMessage.error_type));
                Game1.playSound("cancel");
                return;
            }

            var amount = (int)Math.Min(entry.Prototype.maximumStackSize(), entry.AvailableCount);
            if (amount <= 0)
                amount = 1;
            this.Withdraw(entry, amount);
        }
    }

    public override void receiveRightClick(int x, int y, bool playSound = true)
    {
        var invIndex = this.InventoryIndexAt(x, y);
        if (invIndex >= 0)
        {
            this.DepositSlot(invIndex, single: true);
            return;
        }

        var entry = this.GridEntryAt(x, y);
        if (entry != null)
        {
            if (entry.AvailableCount <= 0)
            {
                Game1.addHUDMessage(new HUDMessage(ModText.Get("terminal.reserved"), HUDMessage.error_type));
                Game1.playSound("cancel");
                return;
            }

            this.Withdraw(entry, 1);
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        this.itemGrid.ScrollOffset += direction > 0 ? -1 : 1;
        this.itemGrid.ClampScroll(this.GetVisibleEntries().Count);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            this.exitThisMenu();
            return;
        }

        this.SyncSearchFromInput();
        base.receiveKeyPress(key);
    }

    protected override void cleanupBeforeExit()
    {
        SVSAPMenuWidgets.ReleaseSearchTextBox(this.searchInput);
        base.cleanupBeforeExit();
    }

    private void Withdraw(NetworkInventoryEntry entry, int amount)
    {
        if (!this.EnsureActionAllowed())
            return;

        if (this.transactionService.TryWithdraw(this.network, entry.Key, amount, out var message))
        {
            this.transactionService.SaveNetworkState();
            this.snapshot = this.CreateSnapshot();
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
            Game1.playSound("coin");
        }
        else
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            Game1.playSound("cancel");
        }
    }

    private void DepositSlot(int index, bool single)
    {
        if (index < 0 || index >= Game1.player.Items.Count)
            return;

        var item = Game1.player.Items[index];
        if (item is null)
            return;

        if (!this.EnsureActionAllowed())
            return;

        if (this.transactionService.TryDepositPlayerSlot(this.network, Game1.player, index, single, out var moved, out var message))
        {
            this.transactionService.SaveNetworkState();
            this.snapshot = this.CreateSnapshot();
            Game1.addHUDMessage(new HUDMessage(ModText.Format("inventory.depositSlotSuccess", moved), HUDMessage.newQuest_type));
            Game1.playSound("Ship");
        }
        else
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            Game1.playSound("cancel");
        }
    }

    private void DepositBulk(bool sameOnly)
    {
        if (!this.EnsureActionAllowed())
            return;

        if (this.transactionService.TryDepositFromPlayer(this.network, sameOnly, out var message))
        {
            this.transactionService.SaveNetworkState();
            this.snapshot = this.CreateSnapshot();
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
            Game1.playSound("Ship");
        }
        else
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            Game1.playSound("cancel");
        }
    }

    private NetworkInventoryEntry? GridEntryAt(int x, int y)
    {
        var entries = this.GetVisibleEntries();
        return this.itemGrid.HitTest(x, y, entries);
    }

    private int InventoryIndexAt(int x, int y)
    {
        return this.backpackGrid.HitTest(x, y);
    }

    private List<NetworkInventoryEntry> GetVisibleEntries()
    {
        this.SyncSearchFromInput();
        var entries = this.snapshot.Entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            entries = entries.Where(entry =>
                entry.Prototype.DisplayName.Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || entry.Prototype.Name.Contains(this.search, StringComparison.OrdinalIgnoreCase)
                || entry.Key.QualifiedItemId.Contains(this.search, StringComparison.OrdinalIgnoreCase));
        }

        entries = entries.Where(entry =>
            TerminalInventoryFilters.MatchesCategory(TerminalInventoryFilters.GetCategory(entry.Prototype), this.selectedCategory));

        if (this.selectedQuality is not null)
            entries = entries.Where(entry => entry.Key.Quality == this.selectedQuality.Value);

        entries = this.sortMode switch
        {
            TerminalInventorySortMode.Name => entries.OrderBy(entry => entry.Prototype.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Price => entries
                .OrderByDescending(entry => TerminalInventoryFilters.GetSalePrice(entry.Prototype))
                .ThenBy(entry => entry.Prototype.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Category => entries
                .OrderBy(entry => TerminalInventoryFilters.GetLabel(TerminalInventoryFilters.GetCategory(entry.Prototype)), StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Prototype.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Recent => entries
                .OrderByDescending(entry => entry.LastAddedSequence)
                .ThenBy(entry => entry.Prototype.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => entries
                .OrderByDescending(entry => entry.AvailableCount)
                .ThenBy(entry => entry.Prototype.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

        return entries.ToList();
    }

    private NetworkInventorySnapshot CreateSnapshot()
    {
        var current = this.scanner.Scan(this.network);
        this.transactionService.ApplyReservationOverlay(this.network, current);
        return current;
    }

    private bool EnsureActionAllowed()
    {
        var message = this.getActionBlockMessage();
        if (string.IsNullOrWhiteSpace(message))
            return true;

        Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
        Game1.playSound("cancel");
        return false;
    }

    private void ToggleHeldItemLock()
    {
        var held = Game1.player.CurrentItem;
        if (held is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("terminal.lockHoldItem"), HUDMessage.error_type));
            return;
        }

        var id = held.QualifiedItemId;
        var removed = this.network.LockedQualifiedItemIds.RemoveAll(candidate => string.Equals(candidate, id, StringComparison.Ordinal));
        if (removed > 0)
        {
            this.transactionService.SaveNetworkState();
            Game1.addHUDMessage(new HUDMessage(ModText.Format("terminal.unlocked", held.DisplayName), HUDMessage.newQuest_type));
            return;
        }

        this.network.LockedQualifiedItemIds.Add(id);
        this.network.LockedQualifiedItemIds.Sort(StringComparer.Ordinal);
        this.transactionService.SaveNetworkState();
        Game1.addHUDMessage(new HUDMessage(ModText.Format("terminal.locked", held.DisplayName), HUDMessage.newQuest_type));
    }

    private string GetLockButtonLabel()
    {
        var held = Game1.player.CurrentItem;
        if (held is null)
            return ModText.Get("terminal.lock");

        return this.network.LockedQualifiedItemIds.Contains(held.QualifiedItemId, StringComparer.Ordinal)
            ? ModText.Get("terminal.unlock")
            : ModText.Get("terminal.lock");
    }

    private void ResetScroll() => this.itemGrid.ResetScroll();

    private void SyncSearchFromInput()
    {
        if (SVSAPMenuWidgets.SyncSearchText(this.searchInput, ref this.search))
            this.ResetScroll();
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<NetworkInventoryEntry> entries)
    {
        var mx = Game1.getMouseX();
        var my = Game1.getMouseY();

        var entry = this.itemGrid.HitTest(mx, my, entries);
        if (entry != null)
        {
            var lines = new List<string>
            {
                ModText.Format("terminal.tooltip.count", entry.AvailableCount, entry.ReservedCount > 0 ? ModText.Format("terminal.tooltip.reserved", entry.ReservedCount) : string.Empty),
                ModText.Format("terminal.tooltip.qualityPrice", ItemDisplayService.GetQualityDisplayName(entry.Key.Quality), TerminalInventoryFilters.GetSalePrice(entry.Prototype))
            };
            if (entry.Locations.Count > 1)
                lines.Add(ModText.Format("terminal.tooltip.stackSummary", entry.Locations.Count));

            SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, entry.Prototype.DisplayName, lines);
            return;
        }

        var invIndex = this.InventoryIndexAt(mx, my);
        if (invIndex >= 0)
        {
            var item = Game1.player.Items[invIndex];
            if (item != null)
                SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, item.DisplayName, new List<string> { item.getDescription() });
        }
    }

    private static string FormatLocationLine(ItemStackLocation location)
    {
        var source = location.SourceKind == InventorySourceKind.StorageCell ? ModText.Get("terminal.source.cell") : ModText.Get("terminal.source.chest");
        return ModText.Format("terminal.location", source, location.LocationName, location.TileX, location.TileY, location.Count);
    }
}
