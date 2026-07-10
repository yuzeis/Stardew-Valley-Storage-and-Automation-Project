using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemoteCraftingTerminalMenu : IClickableMenu
{
    private const int SnapshotRefreshTicks = 30;

    private readonly Func<CraftingActionRequestMessage, bool> sendCraftRequest;
    private readonly Action<int, MaterialQualityStrategy> requestSnapshot;
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
    private int batches;
    private bool showCraftableOnly;
    private MaterialQualityStrategy qualityStrategy;
    private bool requestPending;
    private int snapshotAtTick;

    public RemoteCraftingTerminalMenu(
        CraftingSnapshotResponseMessage snapshot,
        Func<CraftingActionRequestMessage, bool> sendCraftRequest,
        Action<int, MaterialQualityStrategy> requestSnapshot)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.snapshot = snapshot;
        this.snapshotAtTick = Game1.ticks;
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
        var top = this.yPositionOnScreen + 24;
        this.searchBox = new Rectangle(innerX, top + 48, 360, 40);

        var isTwoRow = this.width < 900;
        var buttonY1 = this.yPositionOnScreen + this.height - (isTwoRow ? 98 : 54);
        var buttonY2 = this.yPositionOnScreen + this.height - 54;

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

    public void ApplySnapshot(CraftingSnapshotResponseMessage updatedSnapshot)
    {
        this.snapshot = updatedSnapshot;
        this.snapshotAtTick = Game1.ticks;
        this.batches = Math.Max(1, updatedSnapshot.Batches);
        this.qualityStrategy = updatedSnapshot.QualityStrategy;
        this.requestPending = false;
        this.displayNameCache.Clear();
        this.recipeGrid.ClampScroll(this.GetVisibleRecipes().Count);
    }

    public void ApplyPushUpdate(CraftingSnapshotResponseMessage pushSnapshot)
    {
        this.snapshot.NetworkName = pushSnapshot.NetworkName;
        this.snapshot.NetworkItemTypes = pushSnapshot.NetworkItemTypes;
        this.snapshotAtTick = Game1.ticks;
        if (!this.requestPending)
            this.requestSnapshot(this.batches, this.qualityStrategy);
    }

    public override void update(GameTime time)
    {
        base.update(time);
        var tick = Game1.ticks;
        if (this.requestPending || (tick >= this.snapshotAtTick && tick - this.snapshotAtTick < SnapshotRefreshTicks))
            return;

        this.snapshotAtTick = tick;
        this.requestSnapshot(this.batches, this.qualityStrategy);
    }

    public void MarkActionComplete(CraftingSnapshotResponseMessage? updatedSnapshot)
    {
        this.requestPending = false;
        if (updatedSnapshot is not null && updatedSnapshot.Success)
            this.ApplySnapshot(updatedSnapshot);
    }

    public override void draw(SpriteBatch b)
    {
        this.SyncSearchFromInput();
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var visible = this.GetVisibleRecipes();
        this.recipeGrid.ClampScroll(visible.Count);
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + 24;
        var title = ModText.Format("remoteCraftingTerminal.title", this.snapshot.NetworkName, visible.Count, this.snapshot.NetworkItemTypes);
        b.DrawString(Game1.dialogueFont, title, new Vector2(innerX + 12, top), Game1.textColor);
        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);
        b.DrawString(
            Game1.smallFont,
            ModText.Format("remoteCraftingTerminal.summary", this.batches, this.GetQualityStrategyLabel()),
            new Vector2(this.searchBox.Right + 24, this.searchBox.Y + 10),
            Game1.textColor);

        if (this.requestPending)
            b.DrawString(Game1.smallFont, ModText.Get("remoteCrafting.pendingInline"), new Vector2(innerX, top + 36), Color.Firebrick);

        this.recipeGrid.Draw(
            b,
            visible,
            recipe => SVSAPMenuWidgets.CreateIconItem(recipe.OutputQualifiedItemId, recipe.OutputSerializedItemPrototype, recipe.OutputCount),
            _ => 0,
            recipe => !recipe.CanCraft);

        if (visible.Count == 0)
            b.DrawString(Game1.smallFont, ModText.Get("craftingTerminal.empty"), new Vector2(this.gridArea.X + 8, this.gridArea.Y + 8), Color.DarkSlateGray);

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
        base.receiveLeftClick(x, y, playSound);

        foreach (var button in this.amountButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            this.batches = int.Parse(button.name);
            this.ResetScroll();
            this.requestSnapshot(this.batches, this.qualityStrategy);
            Game1.playSound("smallSelect");
            return;
        }

        foreach (var button in this.qualityButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            if (Enum.TryParse<MaterialQualityStrategy>(button.name, out var strategy))
                this.qualityStrategy = strategy;

            this.ResetScroll();
            this.requestSnapshot(this.batches, this.qualityStrategy);
            Game1.playSound("smallSelect");
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

        if (this.requestPending)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteCrafting.requestPending"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

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
        SVSAPMenuWidgets.ReleaseSearchTextBox(this.searchInput);
        base.cleanupBeforeExit();
    }

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
        if (this.requestPending)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("remoteCrafting.requestPending"), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        var sent = this.sendCraftRequest(new CraftingActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            NetworkId = this.snapshot.NetworkId,
            EndpointId = this.snapshot.EndpointId,
            RecipeName = recipe.Name,
            Batches = this.batches,
            QualityStrategy = this.qualityStrategy
        });
        this.requestPending = sent;
        Game1.playSound(sent ? "smallSelect" : "cancel");
    }

    private string GetRecipeDisplayName(RemoteCraftingRecipeMessage recipe)
    {
        var cacheKey = string.IsNullOrWhiteSpace(recipe.OutputSerializedItemPrototype)
            ? recipe.OutputQualifiedItemId
            : recipe.OutputSerializedItemPrototype;
        if (this.displayNameCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var item = SVSAPMenuWidgets.CreateIconItem(recipe.OutputQualifiedItemId, recipe.OutputSerializedItemPrototype, recipe.OutputCount);
        var value = item?.DisplayName ?? recipe.OutputQualifiedItemId;
        this.displayNameCache[cacheKey] = value;
        return value;
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
