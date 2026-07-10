using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class CraftingTerminalMenu : IClickableMenu
{
    private const int ViewRefreshTicks = 30;

    private readonly NetworkData network;
    private readonly InventoryScanner scanner;
    private readonly CraftingRecipeService craftingRecipeService;
    private readonly Func<string?> getActionBlockMessage;
    private readonly List<ClickableComponent> amountButtons = new();
    private readonly List<ClickableComponent> qualityButtons = new();
    private ClickableComponent craftableOnlyButton = null!;
    private readonly SVSAPIconGrid<NetworkCraftingRecipe> recipeGrid = new();
    private readonly List<NetworkCraftingRecipe> recipes;
    private Rectangle searchBox;
    private Rectangle gridArea;
    private string search = string.Empty;
    private readonly TextBox searchInput;
    private int batches = 1;
    private bool showCraftableOnly;
    private MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst;
    private NetworkInventorySnapshot cachedSnapshot = new();
    private IReadOnlyList<NetworkCraftingRecipe> cachedVisibleRecipes = Array.Empty<NetworkCraftingRecipe>();
    private Dictionary<NetworkCraftingRecipe, CraftingAvailability> cachedAvailability = new();
    private int cachedAtTick = -1;

    public CraftingTerminalMenu(
        NetworkData network,
        InventoryScanner scanner,
        CraftingRecipeService craftingRecipeService,
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
        this.craftingRecipeService = craftingRecipeService;
        this.getActionBlockMessage = getActionBlockMessage ?? (() => null);
        this.recipes = this.craftingRecipeService.GetKnownRecipes();
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

    public override void draw(SpriteBatch b)
    {
        this.SyncSearchFromInput();
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var visible = this.GetVisibleRecipes();
        this.recipeGrid.ClampScroll(visible.Count);
        var snapshot = this.GetCachedSnapshot();
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + 24;
        var title = ModText.Format("craftingTerminal.title", this.network.Name, visible.Count, snapshot.Entries.Count);
        b.DrawString(Game1.dialogueFont, title, new Vector2(innerX + 12, top), Game1.textColor);

        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);
        b.DrawString(
            Game1.smallFont,
            ModText.Format("craftingTerminal.summary", this.batches, this.GetQualityStrategyLabel()),
            new Vector2(this.searchBox.Right + 24, this.searchBox.Y + 10),
            Game1.textColor);

        this.recipeGrid.Draw(
            b,
            visible,
            recipe => recipe.OutputPrototype,
            _ => 0,
            recipe => !this.GetAvailability(recipe).CanCraft);

        if (visible.Count == 0)
        {
            b.DrawString(
                Game1.smallFont,
                ModText.Get("craftingTerminal.empty"),
                new Vector2(this.gridArea.X + 8, this.gridArea.Y + 8),
                Color.DarkSlateGray);
        }

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
            this.InvalidateCache();
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
            this.InvalidateCache();
            Game1.playSound("smallSelect");
            return;
        }

        if (this.craftableOnlyButton.containsPoint(x, y))
        {
            this.showCraftableOnly = !this.showCraftableOnly;
            this.ResetScroll();
            this.InvalidateCache();
            Game1.playSound("smallSelect");
            return;
        }

        var visible = this.GetVisibleRecipes();
        this.recipeGrid.ClampScroll(visible.Count);
        var recipe = this.recipeGrid.HitTest(x, y, visible);
        if (recipe is null)
            return;

        if (!this.EnsureActionAllowed())
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
        SVSAPMenuWidgets.ReleaseSearchTextBox(this.searchInput);
        base.cleanupBeforeExit();
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        this.recipeGrid.ScrollOffset += direction > 0 ? -1 : 1;
        this.recipeGrid.ClampScroll(this.GetVisibleRecipes().Count);
    }

    private IReadOnlyList<NetworkCraftingRecipe> GetVisibleRecipes()
    {
        this.RefreshCacheIfNeeded();
        return this.cachedVisibleRecipes;
    }

    private NetworkInventorySnapshot GetCachedSnapshot()
    {
        this.RefreshCacheIfNeeded();
        return this.cachedSnapshot;
    }

    private void RefreshCacheIfNeeded()
    {
        this.SyncSearchFromInput();
        var tick = Game1.ticks;
        if (this.cachedAtTick >= 0 && tick >= this.cachedAtTick && tick - this.cachedAtTick < ViewRefreshTicks)
            return;

        this.cachedSnapshot = this.scanner.Scan(this.network);
        var availability = new Dictionary<NetworkCraftingRecipe, CraftingAvailability>();
        CraftingAvailability getAvailability(NetworkCraftingRecipe recipe)
        {
            if (!availability.TryGetValue(recipe, out var value))
            {
                value = this.craftingRecipeService.GetAvailability(this.network, recipe, this.batches, this.qualityStrategy);
                availability[recipe] = value;
            }

            return value;
        }

        var visible = this.recipes.AsEnumerable();

        if (this.showCraftableOnly)
        {
            visible = visible.Where(recipe =>
                getAvailability(recipe).CanCraft);
        }

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            visible = visible.Where(recipe => recipe.DisplayName.Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || recipe.Name.Contains(this.search, StringComparison.OrdinalIgnoreCase)
                || recipe.OutputPrototype.QualifiedItemId.Contains(this.search, StringComparison.OrdinalIgnoreCase));
        }

        this.cachedAvailability = availability;
        this.cachedVisibleRecipes = visible
            .OrderByDescending(recipe => getAvailability(recipe).CanCraft)
            .ThenBy(recipe => recipe.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        this.cachedAtTick = tick;
    }

    private CraftingAvailability GetAvailability(NetworkCraftingRecipe recipe)
    {
        this.RefreshCacheIfNeeded();
        if (this.cachedAvailability.TryGetValue(recipe, out var availability))
            return availability;

        availability = this.craftingRecipeService.GetAvailability(this.network, recipe, this.batches, this.qualityStrategy);
        this.cachedAvailability[recipe] = availability;
        return availability;
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

    private bool EnsureActionAllowed()
    {
        var message = this.getActionBlockMessage();
        if (string.IsNullOrWhiteSpace(message))
            return true;

        Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
        Game1.playSound("cancel");
        return false;
    }

    private void OpenCraftConfirmation(NetworkCraftingRecipe recipe)
    {
        var availability = this.GetAvailability(recipe);
        var lines = new List<string>
        {
            ModText.Format("craftingTerminal.tooltip.output", recipe.OutputCount * this.batches),
            availability.CanCraft ? ModText.Get("craftingTerminal.confirm.ready") : ModText.Get("craftingTerminal.confirm.missing")
        };

        Game1.activeClickableMenu = new CraftingConfirmationMenu(
            this,
            recipe.DisplayName,
            lines,
            availability.Ingredients,
            () => this.ExecuteCraft(recipe));
        Game1.playSound("smallSelect");
    }

    private void ExecuteCraft(NetworkCraftingRecipe recipe)
    {
        if (!this.EnsureActionAllowed())
            return;

        if (this.craftingRecipeService.TryCraft(this.network, recipe, this.batches, this.qualityStrategy, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
            Game1.playSound("coin");
            this.InvalidateCache();
        }
        else
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            Game1.playSound("cancel");
            this.InvalidateCache();
        }
    }

    private void ResetScroll()
    {
        this.recipeGrid.ResetScroll();
        this.InvalidateCache();
    }

    private void SyncSearchFromInput()
    {
        if (SVSAPMenuWidgets.SyncSearchText(this.searchInput, ref this.search))
            this.ResetScroll();
    }

    private void InvalidateCache()
    {
        this.cachedAtTick = -1;
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<NetworkCraftingRecipe> visible)
    {
        var mx = Game1.getMouseX();
        var my = Game1.getMouseY();
        var recipe = this.recipeGrid.HitTest(mx, my, visible);
        if (recipe is null)
            return;

        var availability = this.GetAvailability(recipe);
        var lines = new List<string>
        {
            ModText.Format("craftingTerminal.tooltip.output", recipe.OutputCount * this.batches),
            availability.CanCraft ? ModText.Get("craftingTerminal.tooltip.ready") : ModText.Get("craftingTerminal.tooltip.missing")
        };
        if (availability.IngredientLines.Count > 0)
            lines.AddRange(availability.IngredientLines.Take(6));

        if (recipe.Ingredients.Count > 6)
            lines.Add(ModText.Format("craftingTerminal.tooltip.moreIngredients", recipe.Ingredients.Count - 6));

        SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, recipe.DisplayName, lines);
    }
}
