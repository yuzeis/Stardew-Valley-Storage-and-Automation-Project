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
    private int batches = 1;
    private bool showCraftableOnly;
    private MaterialQualityStrategy qualityStrategy = MaterialQualityStrategy.LowQualityFirst;

    public CraftingTerminalMenu(
        NetworkData network,
        InventoryScanner scanner,
        CraftingRecipeService craftingRecipeService,
        Func<string?>? getActionBlockMessage = null)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - 980) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - 680) / 2),
            width: 980,
            height: 680,
            showUpperRightCloseButton: true)
    {
        this.network = network;
        this.scanner = scanner;
        this.craftingRecipeService = craftingRecipeService;
        this.getActionBlockMessage = getActionBlockMessage ?? (() => null);
        this.recipes = this.craftingRecipeService.GetKnownRecipes();
        this.BuildLayout();
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private void BuildLayout()
    {
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var innerW = this.width - SVSAPMenuWidgets.Pad * 2;
        var top = this.yPositionOnScreen + 24;
        this.searchBox = new Rectangle(innerX, top + 48, 360, 40);

        var buttonY = this.yPositionOnScreen + this.height - 54;
        var buttonX = innerX;
        foreach (var amount in new[] { 1, 5, 10, 25, 100 })
        {
            this.amountButtons.Add(new ClickableComponent(new Rectangle(buttonX, buttonY, 76, 42), amount.ToString(), amount.ToString()));
            buttonX += 86;
        }

        buttonX += 16;
        foreach (var option in new[]
        {
            (Strategy: MaterialQualityStrategy.LowQualityFirst, Label: ModText.Get("craftingTerminal.quality.low")),
            (Strategy: MaterialQualityStrategy.HighQualityFirst, Label: ModText.Get("craftingTerminal.quality.high")),
            (Strategy: MaterialQualityStrategy.PreserveGoldIridium, Label: ModText.Get("craftingTerminal.quality.preserve"))
        })
        {
            this.qualityButtons.Add(new ClickableComponent(new Rectangle(buttonX, buttonY, 76, 42), option.Strategy.ToString(), option.Label));
            buttonX += 86;
        }

        this.craftableOnlyButton = new ClickableComponent(new Rectangle(buttonX, buttonY, 86, 42), "ready", ModText.Get("craftingTerminal.ready"));

        var gridTop = top + 108;
        var gridBottom = buttonY - 18;
        this.gridArea = new Rectangle(innerX, gridTop, innerW, Math.Max(SVSAPMenuWidgets.Cell, gridBottom - gridTop));
        this.recipeGrid.SetBounds(this.gridArea);
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var visible = this.GetVisibleRecipes();
        this.recipeGrid.ClampScroll(visible.Count);
        var snapshot = this.scanner.Scan(this.network);
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + 24;
        var title = ModText.Format("craftingTerminal.title", this.network.Name, visible.Count, snapshot.Entries.Count);
        b.DrawString(Game1.dialogueFont, title, new Vector2(innerX, top), Game1.textColor);

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
            recipe => recipe.OutputCount * (long)this.batches,
            recipe => !this.GetAvailability(recipe).CanCraft,
            recipe => this.GetAvailability(recipe).CanCraft ? null : "!");

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

        if (!this.EnsureActionAllowed())
            return;

        if (this.craftingRecipeService.TryCraft(this.network, recipe, this.batches, this.qualityStrategy, out var message))
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
            Game1.playSound("coin");
        }
        else
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
            Game1.playSound("cancel");
        }
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
        this.recipeGrid.ScrollOffset += direction > 0 ? -1 : 1;
        this.recipeGrid.ClampScroll(this.GetVisibleRecipes().Count);
    }

    private List<NetworkCraftingRecipe> GetVisibleRecipes()
    {
        var visible = this.recipes.AsEnumerable();

        if (this.showCraftableOnly)
        {
            visible = visible.Where(recipe =>
                this.GetAvailability(recipe).CanCraft);
        }

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            visible = visible.Where(recipe => recipe.DisplayName.Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || recipe.Name.Contains(this.search, StringComparison.OrdinalIgnoreCase)
                || recipe.OutputPrototype.QualifiedItemId.Contains(this.search, StringComparison.OrdinalIgnoreCase));
        }

        return visible.ToList();
    }

    private CraftingAvailability GetAvailability(NetworkCraftingRecipe recipe)
    {
        return this.craftingRecipeService.GetAvailability(this.network, recipe, this.batches, this.qualityStrategy);
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

    private void ResetScroll()
    {
        this.recipeGrid.ResetScroll();
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
        if (availability.MissingLines.Count > 0)
            lines.AddRange(availability.MissingLines.Take(6));
        else
            lines.AddRange(recipe.Ingredients.Take(6).Select(input => $"{input.DisplayKey} x{input.Count * this.batches:N0}"));

        if (recipe.Ingredients.Count > 6)
            lines.Add(ModText.Format("craftingTerminal.tooltip.moreIngredients", recipe.Ingredients.Count - 6));

        SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, recipe.DisplayName, lines);
    }
}
