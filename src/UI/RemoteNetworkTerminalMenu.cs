using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace SVSAP.UI;

internal sealed class RemoteNetworkTerminalMenu : IClickableMenu
{
    private const int CompactCell = 52;
    private const int SnapshotRefreshTicks = 30;
    private const int SnapshotRequestTimeoutTicks = 180;
    private const int ActionRequestTimeoutTicks = 300;

    private readonly Func<TerminalActionRequestMessage, TerminalSnapshotResponseMessage, bool> sendRequest;
    private readonly Func<Guid, Guid, int, int, bool> requestSnapshot;
    private TerminalSnapshotResponseMessage snapshot;
    private readonly List<ClickableComponent> categoryButtons = new();
    private readonly List<ClickableComponent> qualityButtons = new();
    private readonly List<ClickableComponent> sortButtons = new();
    private readonly List<ClickableComponent> depositButtons = new();
    private ClickableComponent toggleLockButton = null!;
    private ClickableComponent previousPageButton = null!;
    private ClickableComponent nextPageButton = null!;
    private readonly SVSAPIconGrid<RemoteInventoryEntryMessage> itemGrid = new(CompactCell);
    private readonly SVSAPBackpackGrid backpackGrid = new(CompactCell);
    private Rectangle searchBox;
    private Rectangle gridArea;
    private Rectangle invArea;
    private string search = string.Empty;
    private TerminalInventoryCategory selectedCategory = TerminalInventoryCategory.All;
    private int? selectedQuality;
    private TerminalInventorySortMode sortMode = TerminalInventorySortMode.Count;
    private bool requestPending;
    private bool snapshotRequestPending;
    private readonly TextBox searchInput;
    private readonly Dictionary<string, string> displayNameCache = new();
    private readonly Dictionary<string, string> itemNameCache = new();
    private readonly SVSAPItemIconCache itemIconCache = new();
    private int snapshotAtTick;
    private int snapshotRequestAtTick;
    private int actionRequestAtTick;
    private readonly Guid menuSessionId;
    private long lastAppliedRequestSequence;
    private long lastAppliedPushSequence;

    public RemoteNetworkTerminalMenu(
        TerminalSnapshotResponseMessage snapshot,
        Func<TerminalActionRequestMessage, TerminalSnapshotResponseMessage, bool> sendRequest,
        Func<Guid, Guid, int, int, bool> requestSnapshot)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.snapshot = snapshot;
        this.snapshotAtTick = Game1.ticks;
        this.menuSessionId = snapshot.MenuSessionId;
        this.lastAppliedRequestSequence = snapshot.RequestSequence;
        this.sendRequest = sendRequest;
        this.requestSnapshot = requestSnapshot;
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
        var top = this.yPositionOnScreen + SVSAPMenuWidgets.HeaderTopOffset;
        this.searchBox = new Rectangle(innerX, top + 44, SVSAPMenuWidgets.CalculateTerminalSearchWidth(innerW, 184), 40);

        var bx = innerX;
        var catY = top + 96;
        var categoryGap = 4;
        var categoryWidth = SVSAPMenuWidgets.CalculateUniformButtonWidth(
            innerW,
            TerminalInventoryFilters.CategoryOrder.Length,
            categoryGap);
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
        var qualities = new int?[] { null, 0, 1, 2, 4 };
        var qualityWidth = innerW < 760 ? 56 : 66;
        var sortWidth = innerW < 760 ? 70 : 84;
        var groupGap = innerW < 760 ? 8 : 28;
        var requiredFilterWidth = qualities.Length * qualityWidth
            + Math.Max(0, qualities.Length - 1) * 4
            + groupGap
            + TerminalInventoryFilters.SortOrder.Length * sortWidth
            + Math.Max(0, TerminalInventoryFilters.SortOrder.Length - 1) * 4;
        if (requiredFilterWidth > innerW)
        {
            var totalButtons = qualities.Length + TerminalInventoryFilters.SortOrder.Length;
            var available = innerW
                - groupGap
                - Math.Max(0, qualities.Length - 1) * 4
                - Math.Max(0, TerminalInventoryFilters.SortOrder.Length - 1) * 4;
            var commonWidth = Math.Max(36, available / Math.Max(1, totalButtons));
            qualityWidth = commonWidth;
            sortWidth = commonWidth;
        }

        foreach (var quality in qualities)
        {
            this.qualityButtons.Add(new ClickableComponent(
                new Rectangle(bx, filterY, qualityWidth, 34),
                quality?.ToString() ?? "All",
                TerminalInventoryFilters.GetQualityLabel(quality)));
            bx += qualityWidth + 4;
        }

        bx += groupGap;
        foreach (var sort in TerminalInventoryFilters.SortOrder)
        {
            this.sortButtons.Add(new ClickableComponent(
                new Rectangle(bx, filterY, sortWidth, 34),
                sort.ToString(),
                TerminalInventoryFilters.GetLabel(sort)));
            bx += sortWidth + 4;
        }

        this.previousPageButton = new ClickableComponent(
            new Rectangle(innerX + innerW - 168, top + 44, 78, 40),
            "previous_page",
            ModText.Get("ui.page.previous"));
        this.nextPageButton = new ClickableComponent(
            new Rectangle(innerX + innerW - 84, top + 44, 78, 40),
            "next_page",
            ModText.Get("ui.page.next"));

        var bottomY = this.yPositionOnScreen + this.height - SVSAPMenuWidgets.Pad - 42;
        this.depositButtons.Add(new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad - 130, bottomY, 130, 42), "all", ModText.Get("terminal.depositAll")));
        this.depositButtons.Add(new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad - 288, bottomY, 150, 42), "same", ModText.Get("terminal.depositSame")));
        this.toggleLockButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad - 396, bottomY, 100, 42), "lock", ModText.Get("terminal.lock"));

        var gridTop = filterY + 50;
        var layoutCell = SVSAPMenuWidgets.CalculateTerminalCellSize(innerW, gridTop, bottomY, Game1.player.Items.Count, CompactCell);
        this.itemGrid.SetCellSize(layoutCell);
        this.backpackGrid.SetCellSize(layoutCell);
        var backpackColumns = SVSAPBackpackGrid.GetColumnCount(innerW, layoutCell);
        var invH = SVSAPBackpackGrid.GetHeight(backpackColumns, layoutCell);
        var invTop = bottomY - 18 - invH;
        var invW = backpackColumns * layoutCell;
        this.invArea = new Rectangle(innerX + Math.Max(0, (innerW - invW) / 2), invTop, invW, invH);
        this.backpackGrid.SetBounds(this.invArea);

        // Reserve the locked-item status line between the item grid and backpack.
        var gridBottom = SVSAPMenuWidgets.GetTerminalItemGridBottom(invTop);
        this.gridArea = new Rectangle(innerX, gridTop, innerW, Math.Max(layoutCell, gridBottom - gridTop));
        this.itemGrid.SetBounds(this.gridArea);
    }

    public bool MatchesNetwork(Guid networkId)
    {
        return this.snapshot.NetworkId == networkId;
    }

    public bool MatchesSnapshotContext(TerminalSnapshotResponseMessage candidate)
    {
        return RemoteSnapshotSessionRules.Matches(this.menuSessionId, candidate.MenuSessionId)
            && candidate.NetworkId == this.snapshot.NetworkId
            && candidate.EndpointId == this.snapshot.EndpointId;
    }

    public bool TryApplyRefreshSnapshot(TerminalSnapshotResponseMessage updatedSnapshot)
    {
        if (!this.MatchesSnapshotContext(updatedSnapshot))
            return false;

        if (!RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, updatedSnapshot.RequestSequence))
            return false;

        this.snapshotRequestPending = false;
        this.snapshotAtTick = Game1.ticks;
        this.lastAppliedRequestSequence = updatedSnapshot.RequestSequence;
        this.ApplySnapshot(updatedSnapshot);
        return true;
    }

    public void MarkSnapshotRequestFailed(long requestSequence)
    {
        if (!RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, requestSequence))
            return;

        this.lastAppliedRequestSequence = requestSequence;
        this.snapshotRequestPending = false;
        this.snapshotAtTick = Game1.ticks;
    }

    public void ApplySnapshot(TerminalSnapshotResponseMessage updatedSnapshot)
    {
        this.snapshot = updatedSnapshot;
        this.snapshotAtTick = Game1.ticks;
        this.snapshotRequestPending = false;
        this.displayNameCache.Clear();
        this.itemNameCache.Clear();
        this.itemGrid.ClampScroll(this.GetVisibleEntries().Count);
    }

    public void ApplyPushUpdate(TerminalSnapshotResponseMessage pushSnapshot)
    {
        if (!RemoteSnapshotSessionRules.ShouldApplyPush(this.lastAppliedPushSequence, pushSnapshot.PushSequence))
            return;

        if (pushSnapshot.PushSequence > 0)
            this.lastAppliedPushSequence = pushSnapshot.PushSequence;

        this.snapshot.NetworkName = pushSnapshot.NetworkName;
        this.snapshot.SourceCount = pushSnapshot.SourceCount;
        this.snapshot.TotalEntryCount = pushSnapshot.TotalEntryCount;
        this.snapshot.StorageSummary = pushSnapshot.StorageSummary;
        this.snapshot.LockedQualifiedItemIds = pushSnapshot.LockedQualifiedItemIds;
        this.snapshotAtTick = Game1.ticks;

        if (this.requestPending)
            return;

        var limit = this.GetEntryLimit();
        var maxOffset = pushSnapshot.TotalEntryCount <= 0
            ? 0
            : ((pushSnapshot.TotalEntryCount - 1) / limit) * limit;
        var requestedOffset = Math.Min(this.snapshot.EntryOffset, maxOffset);
        this.RequestSnapshot(requestedOffset, limit);
    }

    public override void update(GameTime time)
    {
        base.update(time);
        var tick = Game1.ticks;
        if (this.requestPending)
        {
            if (!RemoteSnapshotSessionRules.HasTimedOut(this.actionRequestAtTick, tick, ActionRequestTimeoutTicks))
                return;

            this.requestPending = false;
        }

        if (this.snapshotRequestPending)
        {
            if (!RemoteSnapshotSessionRules.HasTimedOut(this.snapshotRequestAtTick, tick, SnapshotRequestTimeoutTicks))
                return;

            this.snapshotRequestPending = false;
        }

        if (tick >= this.snapshotAtTick && tick - this.snapshotAtTick < SnapshotRefreshTicks)
            return;

        this.RequestSnapshot(this.snapshot.EntryOffset, this.GetEntryLimit());
    }

    public bool MarkActionComplete(TerminalActionResponseMessage response)
    {
        if (response.NetworkId != this.snapshot.NetworkId
            || !RemoteSnapshotSessionRules.Matches(this.menuSessionId, response.MenuSessionId)
            || !RemoteSnapshotSessionRules.IsNewer(this.lastAppliedRequestSequence, response.RequestSequence))
        {
            return false;
        }

        this.lastAppliedRequestSequence = response.RequestSequence;
        this.requestPending = false;
        if (response.Snapshot is not null && response.Snapshot.Success && this.MatchesSnapshotContext(response.Snapshot))
            this.ApplySnapshot(response.Snapshot);

        return true;
    }

    public override void draw(SpriteBatch b)
    {
        this.SyncSearchFromInput();
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var visibleEntries = this.GetVisibleEntries();
        this.itemGrid.ClampScroll(visibleEntries.Count);
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + SVSAPMenuWidgets.HeaderTopOffset;
        var totalEntries = this.GetTotalEntryCount();
        var title = ModText.Format("remoteTerminal.title", this.snapshot.NetworkName, visibleEntries.Count, totalEntries, this.snapshot.SourceCount);
        SVSAPMenuWidgets.DrawFittedTitle(
            b,
            title,
            new Rectangle(innerX, top, this.width - SVSAPMenuWidgets.Pad * 2 - 96, 42),
            Game1.textColor);
        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);

        var summaryText = TerminalInventoryFilters.FormatStorageSummary(this.snapshot.StorageSummary);
        var summarySize = Game1.smallFont.MeasureString(summaryText);
        var showSummary = this.searchBox.Right + 24 + summarySize.X <= this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad;
        if (this.HasMultiplePages())
        {
            var limitRight = this.previousPageButton.bounds.X - 12;
            showSummary = showSummary && (this.searchBox.Right + 24 + summarySize.X < limitRight - 120);
        }
        if (showSummary)
        {
            var summaryRight = this.HasMultiplePages()
                ? this.previousPageButton.bounds.X - 12
                : this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad;
            SVSAPMenuWidgets.DrawFittedLine(
                b,
                summaryText,
                new Rectangle(this.searchBox.Right + 16, this.searchBox.Y, Math.Max(1, summaryRight - this.searchBox.Right - 16), this.searchBox.Height),
                Game1.textColor);
        }

        if (this.HasMultiplePages())
        {
            SVSAPMenuWidgets.DrawButton(b, this.previousPageButton, tint: this.CanPagePrevious() ? Color.White : Color.LightGray);
            SVSAPMenuWidgets.DrawButton(b, this.nextPageButton, tint: this.CanPageNext() ? Color.White : Color.LightGray);

            var pageStatusText = ModText.Format("remoteTerminal.pageStatus", this.GetCurrentPageNumber(), this.GetPageCount(), totalEntries);
            var pageStatusSize = Game1.smallFont.MeasureString(pageStatusText);
            var pageStatusX = this.previousPageButton.bounds.X - pageStatusSize.X - 16;
            if (pageStatusX > this.searchBox.Right + (showSummary ? summarySize.X + 32 : 16))
            {
                b.DrawString(
                    Game1.smallFont,
                    pageStatusText,
                    new Vector2(pageStatusX, this.previousPageButton.bounds.Y + 10),
                    Color.DimGray);
            }
        }

        if (this.requestPending || this.snapshotRequestPending)
            SVSAPMenuWidgets.DrawPixelStatusLight(b, this.xPositionOnScreen + this.width - 96, this.yPositionOnScreen + 38, PixelStatus.Processing);

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
            this.GetEntryIcon,
            entry => entry.AvailableCount,
            entry => entry.AvailableCount <= 0);

        if (visibleEntries.Count == 0)
            SVSAPMenuWidgets.DrawFittedLine(b, ModText.Get("remoteTerminal.empty"), new Rectangle(this.gridArea.X + 8, this.gridArea.Y + 8, this.gridArea.Width - 16, 30), Color.DarkSlateGray);

        SVSAPMenuWidgets.DrawSeparator(b, new Rectangle(innerX, this.invArea.Y - 10, this.width - SVSAPMenuWidgets.Pad * 2, 2));
        this.backpackGrid.Draw(b);

        SVSAPMenuWidgets.DrawFittedLine(
            b,
            TerminalInventoryFilters.FormatLockedList(this.snapshot.LockedQualifiedItemIds),
            new Rectangle(innerX, this.invArea.Y - 42, this.width - SVSAPMenuWidgets.Pad * 2, 30),
            Game1.textColor);

        SVSAPMenuWidgets.DrawButton(b, this.toggleLockButton, this.GetLockButtonLabel());
        foreach (var button in this.depositButtons)
            SVSAPMenuWidgets.DrawButton(b, button);

        this.DrawHoverTooltip(b, visibleEntries);
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

        if (this.HasMultiplePages() && this.previousPageButton.containsPoint(x, y))
        {
            var requested = this.CanPagePrevious() && this.RequestPage(this.snapshot.EntryOffset - this.GetEntryLimit());
            Game1.playSound(requested ? "smallSelect" : "cancel");
            return;
        }

        if (this.HasMultiplePages() && this.nextPageButton.containsPoint(x, y))
        {
            var requested = this.CanPageNext() && this.RequestPage(this.snapshot.EntryOffset + this.GetEntryLimit());
            Game1.playSound(requested ? "smallSelect" : "cancel");
            return;
        }

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
            if (!this.CanSendMutation())
                return;

            var held = Game1.player.CurrentItem;
            this.SendRequest(new TerminalActionRequestMessage
            {
                TransactionId = Guid.NewGuid(),
                NetworkId = this.snapshot.NetworkId,
                EndpointId = this.snapshot.EndpointId,
                Action = TerminalActionKind.ToggleHeldItemLock,
                HeldQualifiedItemId = held?.QualifiedItemId ?? string.Empty,
                HeldDisplayName = held?.DisplayName ?? string.Empty
            });
            return;
        }

        foreach (var button in this.depositButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (!this.CanSendMutation())
                return;

            this.SendDepositBatch(sameOnly: button.name == "same");
            return;
        }

        var invIndex = this.backpackGrid.HitTest(x, y);
        if (invIndex >= 0)
        {
            this.SendDepositSlot(invIndex, single: false);
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
        var invIndex = this.backpackGrid.HitTest(x, y);
        if (invIndex >= 0)
        {
            this.SendDepositSlot(invIndex, single: true);
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

        this.SendWithdraw(entry, 1);
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

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        this.itemGrid.ScrollOffset += direction > 0 ? -1 : 1;
        this.itemGrid.ClampScroll(this.GetVisibleEntries().Count);
    }

    private void SendWithdraw(RemoteInventoryEntryMessage entry, int amount)
    {
        if (!this.CanSendMutation())
            return;

        this.SendRequest(new TerminalActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            NetworkId = this.snapshot.NetworkId,
            EndpointId = this.snapshot.EndpointId,
            Action = TerminalActionKind.Withdraw,
            ItemKey = entry.Key,
            Amount = Math.Max(1, amount)
        });
    }

    private void SendDepositSlot(int inventoryIndex, bool single)
    {
        if (!this.CanSendMutation())
            return;

        if (inventoryIndex < 0 || inventoryIndex >= Game1.player.Items.Count)
            return;

        var item = Game1.player.Items[inventoryIndex];
        if (item is null || item.Stack <= 0)
            return;

        var depositItems = this.CaptureDepositSlot(inventoryIndex, single);
        if (depositItems.Count == 0)
            return;

        this.SendRequest(new TerminalActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            NetworkId = this.snapshot.NetworkId,
            EndpointId = this.snapshot.EndpointId,
            Action = TerminalActionKind.DepositSlot,
            InventorySlotIndex = inventoryIndex,
            DepositSingle = single,
            HeldQualifiedItemId = item.QualifiedItemId,
            HeldDisplayName = item.DisplayName,
            DepositItems = depositItems
        });
    }

    private void SendDepositBatch(bool sameOnly)
    {
        var depositItems = this.CaptureDepositBatch(sameOnly);
        if (depositItems.Count == 0)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get(sameOnly ? "inventory.noSameToDeposit" : "inventory.noItemsToDeposit"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        this.SendRequest(new TerminalActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            NetworkId = this.snapshot.NetworkId,
            EndpointId = this.snapshot.EndpointId,
            Action = sameOnly ? TerminalActionKind.DepositSame : TerminalActionKind.DepositAll,
            DepositItems = depositItems
        });
    }

    private List<TerminalItemPayloadMessage> CaptureDepositSlot(int inventoryIndex, bool single)
    {
        var result = new List<TerminalItemPayloadMessage>();
        var item = Game1.player.Items[inventoryIndex];
        if (item is null || item.Stack <= 0 || !CanClientEscrowForRemoteDeposit(item))
            return result;

        var count = single ? 1 : item.Stack;
        result.Add(CreatePayload(item, count));
        item.Stack -= count;
        if (item.Stack <= 0)
            Game1.player.Items[inventoryIndex] = null;

        return result;
    }

    private List<TerminalItemPayloadMessage> CaptureDepositBatch(bool sameOnly)
    {
        var result = new List<TerminalItemPayloadMessage>();
        for (var slot = 0; slot < Game1.player.Items.Count; slot++)
        {
            var item = Game1.player.Items[slot];
            if (item is null || item.Stack <= 0 || !CanClientEscrowForRemoteDeposit(item))
                continue;

            if (sameOnly && !this.MatchesExistingNetworkStack(item))
                continue;

            result.Add(CreatePayload(item, item.Stack));
            Game1.player.Items[slot] = null;
        }

        return result;
    }

    private bool MatchesExistingNetworkStack(Item item)
    {
        var key = ItemKeyFactory.FromItem(item);
        foreach (var entry in this.snapshot.Entries)
        {
            try
            {
                var prototype = SerializedItemCodec.CreateItem(entry.SerializedItemPrototype, 1);
                if (ItemKeyFactory.SameStackBucket(entry.Key, prototype, key, item))
                    return true;
            }
            catch
            {
                // Ignore unreadable remote prototypes; the host will remain authoritative.
            }
        }

        return false;
    }

    private static TerminalItemPayloadMessage CreatePayload(Item item, int count)
    {
        var prototype = item.getOne();
        prototype.Stack = 1;
        return new TerminalItemPayloadMessage
        {
            SerializedItem = SerializedItemCodec.SerializePrototype(prototype),
            Count = count
        };
    }

    private static bool CanClientEscrowForRemoteDeposit(Item item)
    {
        if (item is null || item.Stack <= 0)
            return false;

        if (item is Tool or MeleeWeapon)
            return false;

        if (item is SObject obj && obj.questItem.Value)
            return false;

        if (HasPersistentIdentityModData(item))
            return false;

        return SerializedItemCodec.CanRoundTripPrototype(item);
    }

    private static bool HasPersistentIdentityModData(Item item)
    {
        foreach (var key in item.modData.Keys)
        {
            if (key.EndsWith("/EndpointId", StringComparison.Ordinal)
                || key.EndsWith("/MachineGuid", StringComparison.Ordinal)
                || key.EndsWith("/NetworkId", StringComparison.Ordinal)
                || key.EndsWith("/StoredWh", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void SendRequest(TerminalActionRequestMessage request)
    {
        request.MenuSessionId = this.menuSessionId;
        request.EntryOffset = this.snapshot.EntryOffset;
        request.EntryLimit = this.GetEntryLimit();
        var sent = this.sendRequest(request, this.snapshot);
        this.requestPending = sent;
        if (sent)
            this.actionRequestAtTick = Game1.ticks;
        Game1.playSound(sent ? "smallSelect" : "cancel");
    }

    private bool CanSendMutation()
    {
        if (!this.requestPending && !this.snapshotRequestPending)
            return true;

        Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteTerminal.requestPending"), HUDMessage.error_type));
        Game1.playSound("cancel");
        return false;
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
        this.SyncSearchFromInput();
        var entries = this.snapshot.Entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            entries = entries.Where(entry => this.GetEntryDisplayName(entry).Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || this.GetEntryName(entry).Contains(this.search, StringComparison.OrdinalIgnoreCase)
                || entry.QualifiedItemId.Contains(this.search, StringComparison.OrdinalIgnoreCase));
        }

        entries = entries.Where(entry => TerminalInventoryFilters.MatchesCategory(entry.Category, this.selectedCategory));

        if (this.selectedQuality is not null)
            entries = entries.Where(entry => entry.Quality == this.selectedQuality.Value);

        entries = this.sortMode switch
        {
            TerminalInventorySortMode.Name => entries.OrderBy(this.GetEntryDisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Price => entries
                .OrderByDescending(entry => entry.SalePrice)
                .ThenBy(this.GetEntryDisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Category => entries
                .OrderBy(entry => TerminalInventoryFilters.GetLabel(entry.Category), StringComparer.OrdinalIgnoreCase)
                .ThenBy(this.GetEntryDisplayName, StringComparer.CurrentCultureIgnoreCase),
            TerminalInventorySortMode.Recent => entries
                .OrderByDescending(entry => entry.LastAddedSequence)
                .ThenBy(this.GetEntryDisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => entries
                .OrderByDescending(entry => entry.AvailableCount)
                .ThenBy(this.GetEntryDisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

        return entries.ToList();
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<RemoteInventoryEntryMessage> visibleEntries)
    {
        var mouseX = Game1.getMouseX();
        var mouseY = Game1.getMouseY();
        var entry = this.itemGrid.HitTest(mouseX, mouseY, visibleEntries);
        if (entry is null)
        {
            var invIndex = this.backpackGrid.HitTest(mouseX, mouseY);
            if (invIndex >= 0 && invIndex < Game1.player.Items.Count)
            {
                var item = Game1.player.Items[invIndex];
                if (item is not null)
                    SVSAPMenuWidgets.DrawTooltipBox(b, mouseX + 28, mouseY + 28, item.DisplayName, new List<string> { item.getDescription() });
            }

            return;
        }

        var lines = new List<string>
        {
            ModText.Format("terminal.tooltip.count", entry.AvailableCount, entry.ReservedCount > 0 ? ModText.Format("terminal.tooltip.reserved", entry.ReservedCount) : string.Empty),
            ModText.Format("terminal.tooltip.qualityPrice", ItemDisplayService.GetQualityDisplayName(entry.Quality), entry.SalePrice)
        };
        if (entry.StackCount > 1)
            lines.Add(ModText.Format("terminal.tooltip.stackSummary", entry.StackCount));

        SVSAPMenuWidgets.DrawTooltipBox(b, mouseX + 28, mouseY + 28, this.GetEntryDisplayName(entry), lines);
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

    private void SyncSearchFromInput()
    {
        if (SVSAPMenuWidgets.SyncSearchText(this.searchInput, ref this.search))
            this.ResetScroll();
    }

    private string GetEntryDisplayName(RemoteInventoryEntryMessage entry)
    {
        var cacheKey = GetEntryCacheKey(entry);
        if (this.displayNameCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var item = this.GetEntryIcon(entry);
        var value = item?.DisplayName ?? entry.QualifiedItemId;
        this.displayNameCache[cacheKey] = value;
        return value;
    }

    private string GetEntryName(RemoteInventoryEntryMessage entry)
    {
        var cacheKey = GetEntryCacheKey(entry);
        if (this.itemNameCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var item = this.GetEntryIcon(entry);
        var value = item?.Name ?? entry.QualifiedItemId;
        this.itemNameCache[cacheKey] = value;
        return value;
    }

    private static string GetEntryCacheKey(RemoteInventoryEntryMessage entry)
    {
        return string.IsNullOrWhiteSpace(entry.SerializedItemPrototype)
            ? entry.QualifiedItemId
            : entry.SerializedItemPrototype;
    }

    private Item? GetEntryIcon(RemoteInventoryEntryMessage entry)
    {
        var key = GetEntryCacheKey(entry);
        return this.itemIconCache.GetOrCreate(
            key,
            () => SVSAPMenuWidgets.CreateIconItem(entry.QualifiedItemId, entry.SerializedItemPrototype));
    }

    private int GetTotalEntryCount()
    {
        return this.snapshot.TotalEntryCount > 0 ? this.snapshot.TotalEntryCount : this.snapshot.Entries.Count;
    }

    private int GetEntryLimit()
    {
        return this.snapshot.EntryLimit > 0 ? this.snapshot.EntryLimit : Math.Max(1, this.snapshot.Entries.Count);
    }

    private bool HasMultiplePages()
    {
        return this.GetTotalEntryCount() > this.GetEntryLimit();
    }

    private bool CanPagePrevious()
    {
        return this.snapshot.EntryOffset > 0;
    }

    private bool CanPageNext()
    {
        return this.snapshot.EntryOffset + this.snapshot.Entries.Count < this.GetTotalEntryCount();
    }

    private int GetPageCount()
    {
        return Math.Max(1, (int)Math.Ceiling(this.GetTotalEntryCount() / (double)this.GetEntryLimit()));
    }

    private int GetCurrentPageNumber()
    {
        return Math.Clamp((this.snapshot.EntryOffset / this.GetEntryLimit()) + 1, 1, this.GetPageCount());
    }

    private bool RequestPage(int entryOffset)
    {
        this.ResetScroll();
        return this.RequestSnapshot(Math.Max(0, entryOffset), this.GetEntryLimit());
    }

    private bool RequestSnapshot(int entryOffset, int entryLimit)
    {
        if (this.requestPending)
            return false;

        var tick = Game1.ticks;
        if (this.snapshotRequestPending
            && !RemoteSnapshotSessionRules.HasTimedOut(this.snapshotRequestAtTick, tick, SnapshotRequestTimeoutTicks))
        {
            return false;
        }

        this.snapshotAtTick = tick;
        this.snapshotRequestAtTick = tick;
        this.snapshotRequestPending = this.requestSnapshot(
            this.snapshot.NetworkId,
            this.snapshot.EndpointId,
            Math.Max(0, entryOffset),
            Math.Max(1, entryLimit));
        return this.snapshotRequestPending;
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
