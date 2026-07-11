using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemoteCraftingTerminalMenu : IClickableMenu, ISearchTextInputOwner
{
    private const int SnapshotRefreshTicks = 30;
    private const int SnapshotRequestTimeoutTicks = 180;
    private const int ActionRequestTimeoutTicks = 300;

    private readonly Func<CraftingActionRequestMessage, bool> sendCraftRequest;
    private readonly Func<int, MaterialQualityStrategy, bool> requestSnapshot;
    private CraftingSnapshotResponseMessage snapshot;
    private readonly List<ClickableComponent> amountButtons = new();
    private readonly List<ClickableComponent> qualityButtons = new();
    private ClickableComponent craftableOnlyButton = null!;
    private readonly SVSAPIconGrid<RemoteCraftingRecipeMessage> recipeGrid = new();
    private Rectangle searchBox;
    private Rectangle gridArea;
    private string search = string.Empty;
    private readonly TextBox searchInput;
    private readonly Dictionary<string, string> displayNameCache = new();
    private readonly SVSAPItemIconCache itemIconCache = new();
    private int batches;
    private bool showCraftableOnly;
    private MaterialQualityStrategy qualityStrategy;
    private bool requestPending;
    private bool snapshotRequestPending;
    private int snapshotAtTick;
    private int snapshotRequestAtTick;
    private int actionRequestAtTick;
    private readonly Guid menuSessionId;
    private long lastAppliedRequestSequence;
    private long lastAppliedPushSequence;

    public RemoteCraftingTerminalMenu(
        CraftingSnapshotResponseMessage snapshot,
        Func<CraftingActionRequestMessage, bool> sendCraftRequest,
        Func<int, MaterialQualityStrategy, bool> requestSnapshot)
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
        this.sendCraftRequest = sendCraftRequest;
        this.requestSnapshot = requestSnapshot;
        this.batches = Math.Max(1, snapshot.Batches);
        this.qualityStrategy = snapshot.QualityStrategy;
        this.BuildLayout();
        this.searchInput = SVSAPMenuWidgets.CreateSearchTextBox(this.searchBox, this.search);
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private static int GetMenuWidth() => Math.Min(980, Game1.uiViewport.Width - 80);

    private static int GetMenuHeight() => Math.Min(680, Game1.uiViewport.Height - 80);

    private void BuildLayout()
    {
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var innerW = this.width - SVSAPMenuWidgets.Pad * 2;
        var top = this.yPositionOnScreen + SVSAPMenuWidgets.HeaderTopOffset;
        this.searchBox = new Rectangle(innerX, top + 48, Math.Min(360, Math.Max(180, innerW - 260)), 40);

        var isTwoRow = this.width < 900;
        var buttonY2 = this.yPositionOnScreen + this.height - SVSAPMenuWidgets.Pad - 42;
        var buttonY1 = isTwoRow ? buttonY2 - 44 : buttonY2;

        var buttonX = innerX;
        foreach (var amount in new[] { 1, 5, 10, 25, 100 })
        {
            this.amountButtons.Add(new ClickableComponent(new Rectangle(buttonX, buttonY1, 76, 42), amount.ToString(), amount.ToString()));
            buttonX += 86;
        }

        buttonX = isTwoRow ? innerX : buttonX + 16;
        var targetY = isTwoRow ? buttonY2 : buttonY1;
        foreach (var option in new[]
        {
            (Strategy: MaterialQualityStrategy.LowQualityFirst, Label: ModText.Get("craftingTerminal.quality.low")),
            (Strategy: MaterialQualityStrategy.HighQualityFirst, Label: ModText.Get("craftingTerminal.quality.high")),
            (Strategy: MaterialQualityStrategy.PreserveGoldIridium, Label: ModText.Get("craftingTerminal.quality.preserve"))
        })
        {
            this.qualityButtons.Add(new ClickableComponent(new Rectangle(buttonX, targetY, 76, 42), option.Strategy.ToString(), option.Label));
            buttonX += 86;
        }

        this.craftableOnlyButton = new ClickableComponent(new Rectangle(buttonX, targetY, 86, 42), "ready", ModText.Get("craftingTerminal.ready"));

        var gridTop = top + 108;
        var gridBottom = buttonY1 - 18;
        this.gridArea = new Rectangle(innerX, gridTop, innerW, Math.Max(SVSAPMenuWidgets.Cell, gridBottom - gridTop));
        this.recipeGrid.SetBounds(this.gridArea);
    }

    public bool MatchesNetwork(Guid networkId)
    {
        return this.snapshot.NetworkId == networkId;
    }

    public bool MatchesSnapshotContext(CraftingSnapshotResponseMessage candidate)
    {
        return RemoteSnapshotSessionRules.Matches(this.menuSessionId, candidate.MenuSessionId)
            && candidate.NetworkId == this.snapshot.NetworkId
            && candidate.EndpointId == this.snapshot.EndpointId;
    }

    public bool TryApplyRefreshSnapshot(CraftingSnapshotResponseMessage updatedSnapshot)
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

    public void ApplySnapshot(CraftingSnapshotResponseMessage updatedSnapshot)
    {
        this.snapshot = updatedSnapshot;
        this.snapshotAtTick = Game1.ticks;
        this.batches = Math.Max(1, updatedSnapshot.Batches);
        this.qualityStrategy = updatedSnapshot.QualityStrategy;
        this.snapshotRequestPending = false;
        this.displayNameCache.Clear();
        this.recipeGrid.ClampScroll(this.GetVisibleRecipes().Count);
    }

    public void ApplyPushUpdate(CraftingSnapshotResponseMessage pushSnapshot)
    {
        if (!RemoteSnapshotSessionRules.ShouldApplyPush(this.lastAppliedPushSequence, pushSnapshot.PushSequence))
            return;

        if (pushSnapshot.PushSequence > 0)
            this.lastAppliedPushSequence = pushSnapshot.PushSequence;

        this.snapshot.NetworkName = pushSnapshot.NetworkName;
        this.snapshot.NetworkItemTypes = pushSnapshot.NetworkItemTypes;
        this.snapshotAtTick = Game1.ticks;
        if (!this.requestPending)
            this.RequestSnapshot();
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

        this.RequestSnapshot();
    }

    public bool MarkActionComplete(CraftingActionResponseMessage response)
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

        var visible = this.GetVisibleRecipes();
        this.recipeGrid.ClampScroll(visible.Count);
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + SVSAPMenuWidgets.HeaderTopOffset;
        var title = ModText.Format("remoteCraftingTerminal.title", this.snapshot.NetworkName, visible.Count, this.snapshot.NetworkItemTypes);
        SVSAPMenuWidgets.DrawFittedTitle(b, title, new Rectangle(innerX + 12, top, this.width - SVSAPMenuWidgets.Pad * 2 - 104, 42), Game1.textColor);
        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);
        SVSAPMenuWidgets.DrawFittedLine(
            b,
            ModText.Format("remoteCraftingTerminal.summary", this.batches, this.GetQualityStrategyLabel()),
            new Rectangle(this.searchBox.Right + 16, this.searchBox.Y, Math.Max(1, this.xPositionOnScreen + this.width - SVSAPMenuWidgets.Pad - this.searchBox.Right - 16), this.searchBox.Height),
            Game1.textColor,
            horizontalPadding: 0);

        if (this.requestPending || this.snapshotRequestPending)
            SVSAPMenuWidgets.DrawPixelStatusLight(b, this.xPositionOnScreen + this.width - 96, this.yPositionOnScreen + 38, PixelStatus.Processing);

        this.recipeGrid.Draw(
            b,
            visible,
            this.GetRecipeIcon,
            recipe => recipe.OutputCount,
            recipe => !recipe.CanCraft);

        if (visible.Count == 0)
            SVSAPMenuWidgets.DrawFittedLine(b, ModText.Get("craftingTerminal.empty"), new Rectangle(this.gridArea.X + 8, this.gridArea.Y + 8, this.gridArea.Width - 16, 30), Color.DarkSlateGray);

        foreach (var button in this.amountButtons)
        {
            var amount = int.Parse(button.name);
            SVSAPMenuWidgets.DrawButton(b, button, tint: amount == this.batches ? Color.LightGreen : Color.White);
        }

        foreach (var button in this.qualityButtons)
        {
            var selected = Enum.TryParse<MaterialQualityStrategy>(button.name, out var strategy) && strategy == this.qualityStrategy;
            SVSAPMenuWidgets.DrawButton(b, button, tint: selected ? Color.LightGreen : Color.White);
        }

        SVSAPMenuWidgets.DrawButton(b, this.craftableOnlyButton, tint: this.showCraftableOnly ? Color.LightGreen : Color.White);
        this.DrawHoverTooltip(b, visible);
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

        foreach (var button in this.amountButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (!this.CanStartRemoteOperation())
                return;

            this.batches = int.Parse(button.name);
            this.ResetScroll();
            Game1.playSound(this.RequestSnapshot() ? "smallSelect" : "cancel");
            return;
        }

        foreach (var button in this.qualityButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (!this.CanStartRemoteOperation())
                return;

            if (Enum.TryParse<MaterialQualityStrategy>(button.name, out var strategy))
                this.qualityStrategy = strategy;

            this.ResetScroll();
            Game1.playSound(this.RequestSnapshot() ? "smallSelect" : "cancel");
            return;
        }

        if (this.craftableOnlyButton.containsPoint(x, y))
        {
            this.showCraftableOnly = !this.showCraftableOnly;
            this.ResetScroll();
            Game1.playSound("smallSelect");
            return;
        }

        var visible = this.GetVisibleRecipes();
        this.recipeGrid.ClampScroll(visible.Count);
        var recipe = this.recipeGrid.HitTest(x, y, visible);
        if (recipe is null)
            return;

        if (!this.CanStartRemoteOperation())
            return;

        this.OpenCraftConfirmation(recipe);
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
        this.SuspendSearchInput();
        base.cleanupBeforeExit();
    }

    public void SuspendSearchInput() => SVSAPMenuWidgets.ReleaseSearchTextBox(this.searchInput);

    public void ResumeSearchInput() => SVSAPMenuWidgets.ActivateSearchTextBox(this.searchInput);

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        this.recipeGrid.ScrollOffset += direction > 0 ? -1 : 1;
        this.recipeGrid.ClampScroll(this.GetVisibleRecipes().Count);
    }

    private List<RemoteCraftingRecipeMessage> GetVisibleRecipes()
    {
        this.SyncSearchFromInput();
        var visible = this.snapshot.Recipes.AsEnumerable();

        if (this.showCraftableOnly)
            visible = visible.Where(recipe => recipe.CanCraft);

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            visible = visible.Where(recipe => this.GetRecipeDisplayName(recipe).Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || recipe.Name.Contains(this.search, StringComparison.OrdinalIgnoreCase)
                || recipe.OutputQualifiedItemId.Contains(this.search, StringComparison.OrdinalIgnoreCase));
        }

        return visible
            .OrderByDescending(recipe => recipe.CanCraft)
            .ThenBy(this.GetRecipeDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private string GetQualityStrategyLabel()
    {
        return this.qualityStrategy switch
        {
            MaterialQualityStrategy.HighQualityFirst => ModText.Get("craftingTerminal.strategy.high"),
            MaterialQualityStrategy.PreserveGoldIridium => ModText.Get("craftingTerminal.strategy.preserve"),
            _ => ModText.Get("craftingTerminal.strategy.low")
        };
    }

    private void ResetScroll()
    {
        this.recipeGrid.ResetScroll();
    }

    private void SyncSearchFromInput()
    {
        if (SVSAPMenuWidgets.SyncSearchText(this.searchInput, ref this.search))
            this.ResetScroll();
    }

    private void OpenCraftConfirmation(RemoteCraftingRecipeMessage recipe)
    {
        var lines = new List<string>
        {
            ModText.Format("craftingTerminal.tooltip.output", recipe.OutputCount * this.batches),
            recipe.CanCraft ? ModText.Get("craftingTerminal.confirm.ready") : ModText.Get("craftingTerminal.confirm.missing")
        };

        Game1.activeClickableMenu = new CraftingConfirmationMenu(
            this,
            this.GetRecipeDisplayName(recipe),
            lines,
            recipe.Ingredients,
            () => this.SendCraftRequest(recipe));
        Game1.playSound("smallSelect");
    }

    private void SendCraftRequest(RemoteCraftingRecipeMessage recipe)
    {
        if (!this.CanStartRemoteOperation())
            return;

        var sent = this.sendCraftRequest(new CraftingActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            MenuSessionId = this.menuSessionId,
            NetworkId = this.snapshot.NetworkId,
            EndpointId = this.snapshot.EndpointId,
            RecipeName = recipe.Name,
            Batches = this.batches,
            QualityStrategy = this.qualityStrategy
        });
        this.requestPending = sent;
        if (sent)
            this.actionRequestAtTick = Game1.ticks;
        Game1.playSound(sent ? "smallSelect" : "cancel");
    }

    private bool RequestSnapshot()
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
        this.snapshotRequestPending = this.requestSnapshot(this.batches, this.qualityStrategy);
        return this.snapshotRequestPending;
    }

    private bool CanStartRemoteOperation()
    {
        if (!this.requestPending && !this.snapshotRequestPending)
            return true;

        Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteCrafting.requestPending"), HUDMessage.error_type));
        Game1.playSound("cancel");
        return false;
    }

    private string GetRecipeDisplayName(RemoteCraftingRecipeMessage recipe)
    {
        var cacheKey = string.IsNullOrWhiteSpace(recipe.OutputSerializedItemPrototype)
            ? recipe.OutputQualifiedItemId
            : recipe.OutputSerializedItemPrototype;
        if (this.displayNameCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var item = this.GetRecipeIcon(recipe);
        var value = item?.DisplayName ?? recipe.OutputQualifiedItemId;
        this.displayNameCache[cacheKey] = value;
        return value;
    }

    private Item? GetRecipeIcon(RemoteCraftingRecipeMessage recipe)
    {
        var cacheKey = string.IsNullOrWhiteSpace(recipe.OutputSerializedItemPrototype)
            ? recipe.OutputQualifiedItemId
            : recipe.OutputSerializedItemPrototype;
        return this.itemIconCache.GetOrCreate(
            cacheKey,
            () => SVSAPMenuWidgets.CreateIconItem(recipe.OutputQualifiedItemId, recipe.OutputSerializedItemPrototype, recipe.OutputCount));
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<RemoteCraftingRecipeMessage> visible)
    {
        var mx = Game1.getMouseX();
        var my = Game1.getMouseY();
        var recipe = this.recipeGrid.HitTest(mx, my, visible);
        if (recipe is null)
            return;

        var lines = new List<string>
        {
            ModText.Format("craftingTerminal.tooltip.output", recipe.OutputCount * this.batches),
            recipe.CanCraft ? ModText.Get("craftingTerminal.tooltip.ready") : ModText.Get("craftingTerminal.tooltip.missing")
        };
        if (recipe.IngredientLines.Count > 0)
            lines.AddRange(recipe.IngredientLines.Take(6));
        if (recipe.IngredientLines.Count > 6)
            lines.Add(ModText.Format("craftingTerminal.tooltip.moreIngredients", recipe.IngredientLines.Count - 6));

        SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, this.GetRecipeDisplayName(recipe), lines);
    }
}
