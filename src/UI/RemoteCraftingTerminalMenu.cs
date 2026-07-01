using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class RemoteCraftingTerminalMenu : IClickableMenu
{
    private readonly Action<CraftingActionRequestMessage> sendCraftRequest;
    private readonly Action<int, MaterialQualityStrategy> requestSnapshot;
    private CraftingSnapshotResponseMessage snapshot;
    private readonly List<ClickableComponent> amountButtons = new();
    private readonly List<ClickableComponent> qualityButtons = new();
    private ClickableComponent craftableOnlyButton = null!;
    private readonly SVSAPIconGrid<RemoteCraftingRecipeMessage> recipeGrid = new();
    private Rectangle searchBox;
    private Rectangle gridArea;
    private string search = string.Empty;
    private int batches;
    private bool showCraftableOnly;
    private MaterialQualityStrategy qualityStrategy;

    public RemoteCraftingTerminalMenu(
        CraftingSnapshotResponseMessage snapshot,
        Action<CraftingActionRequestMessage> sendCraftRequest,
        Action<int, MaterialQualityStrategy> requestSnapshot)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - 980) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - 680) / 2),
            width: 980,
            height: 680,
            showUpperRightCloseButton: true)
    {
        this.snapshot = snapshot;
        this.sendCraftRequest = sendCraftRequest;
        this.requestSnapshot = requestSnapshot;
        this.batches = Math.Max(1, snapshot.Batches);
        this.qualityStrategy = snapshot.QualityStrategy;
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
            (Strategy: MaterialQualityStrategy.LowQualityFirst, Label: "低质"),
            (Strategy: MaterialQualityStrategy.HighQualityFirst, Label: "高质"),
            (Strategy: MaterialQualityStrategy.PreserveGoldIridium, Label: "保金铱")
        })
        {
            this.qualityButtons.Add(new ClickableComponent(new Rectangle(buttonX, buttonY, 76, 42), option.Strategy.ToString(), option.Label));
            buttonX += 86;
        }

        this.craftableOnlyButton = new ClickableComponent(new Rectangle(buttonX, buttonY, 86, 42), "ready", "可做");

        var gridTop = top + 108;
        var gridBottom = buttonY - 18;
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
        this.batches = Math.Max(1, updatedSnapshot.Batches);
        this.qualityStrategy = updatedSnapshot.QualityStrategy;
        this.recipeGrid.ClampScroll(this.GetVisibleRecipes().Count);
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var visible = this.GetVisibleRecipes();
        this.recipeGrid.ClampScroll(visible.Count);
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + 24;
        var title = $"{this.snapshot.NetworkName} 远程合成  -  {visible.Count:N0} 个配方，网络 {this.snapshot.NetworkItemTypes:N0} 类物品";
        b.DrawString(Game1.dialogueFont, title, new Vector2(innerX, top), Game1.textColor);
        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);
        b.DrawString(
            Game1.smallFont,
            $"远程批量 x{this.batches:N0}  ·  材料策略：{this.GetQualityStrategyLabel()}",
            new Vector2(this.searchBox.Right + 24, this.searchBox.Y + 10),
            Game1.textColor);

        this.recipeGrid.Draw(
            b,
            visible,
            recipe => SVSAPMenuWidgets.CreateIconItem(recipe.OutputQualifiedItemId, recipe.OutputSerializedItemPrototype, recipe.OutputCount),
            recipe => recipe.OutputCount * (long)this.batches,
            recipe => !recipe.CanCraft,
            recipe => recipe.CanCraft ? null : "!");

        if (visible.Count == 0)
            b.DrawString(Game1.smallFont, "没有匹配的已知合成配方。", new Vector2(this.gridArea.X + 8, this.gridArea.Y + 8), Color.DarkSlateGray);

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

        this.sendCraftRequest(new CraftingActionRequestMessage
        {
            TransactionId = Guid.NewGuid(),
            NetworkId = this.snapshot.NetworkId,
            EndpointId = this.snapshot.EndpointId,
            RecipeName = recipe.Name,
            Batches = this.batches,
            QualityStrategy = this.qualityStrategy
        });
        Game1.playSound("smallSelect");
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

    private List<RemoteCraftingRecipeMessage> GetVisibleRecipes()
    {
        var visible = this.snapshot.Recipes.AsEnumerable();

        if (this.showCraftableOnly)
            visible = visible.Where(recipe => recipe.CanCraft);

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            visible = visible.Where(recipe => recipe.DisplayName.Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || recipe.Name.Contains(this.search, StringComparison.OrdinalIgnoreCase)
                || recipe.OutputQualifiedItemId.Contains(this.search, StringComparison.OrdinalIgnoreCase));
        }

        return visible.ToList();
    }

    private string GetQualityStrategyLabel()
    {
        return this.qualityStrategy switch
        {
            MaterialQualityStrategy.HighQualityFirst => "高品质优先",
            MaterialQualityStrategy.PreserveGoldIridium => "保留金/铱",
            _ => "低品质优先"
        };
    }

    private void ResetScroll()
    {
        this.recipeGrid.ResetScroll();
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
            $"产出 x{recipe.OutputCount * this.batches:N0}",
            recipe.CanCraft ? "材料齐全" : "缺少材料"
        };
        lines.AddRange(recipe.MissingLines.Take(6));

        SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, recipe.DisplayName, lines);
    }
}
