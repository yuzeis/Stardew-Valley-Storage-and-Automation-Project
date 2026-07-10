using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class CraftingConfirmationMenu : IClickableMenu
{
    private const int Pad = 28;
    private const int LineHeight = 28;

    private readonly IClickableMenu parent;
    private readonly string title;
    private readonly IReadOnlyList<ConfirmationRow> rows;
    private readonly Action onConfirm;
    private readonly bool canConfirm;
    private readonly ClickableComponent confirmButton;
    private readonly ClickableComponent cancelButton;
    private readonly Rectangle contentBounds;
    private int scrollOffset;

    public CraftingConfirmationMenu(IClickableMenu parent, string title, IReadOnlyList<string> lines, Action onConfirm)
        : this(parent, title, lines.Select(line => new ConfirmationRow(line, PixelStatus.Idle)).ToList(), true, onConfirm)
    {
    }

    public CraftingConfirmationMenu(
        IClickableMenu parent,
        string title,
        IReadOnlyList<string> summaryLines,
        IReadOnlyList<CraftingIngredientAvailability> ingredients,
        Action onConfirm)
        : this(parent, title, BuildRows(summaryLines, ingredients), ingredients.All(ingredient => ingredient.IsSufficient), onConfirm)
    {
    }

    private CraftingConfirmationMenu(
        IClickableMenu parent,
        string title,
        IReadOnlyList<ConfirmationRow> rows,
        bool canConfirm,
        Action onConfirm)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - 720) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - 520) / 2),
            width: 720,
            height: 520,
            showUpperRightCloseButton: true)
    {
        this.parent = parent;
        this.title = title;
        this.rows = rows;
        this.canConfirm = canConfirm;
        this.onConfirm = onConfirm;
        this.contentBounds = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 92, this.width - Pad * 2, this.height - 168);
        this.cancelButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - 296, this.yPositionOnScreen + this.height - 60, 120, 42), "cancel", ModText.Get("craftingTerminal.confirm.cancel"));
        this.confirmButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - 164, this.yPositionOnScreen + this.height - 60, 120, 42), "confirm", ModText.Get("craftingTerminal.confirm.craft"));
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        b.DrawString(Game1.dialogueFont, ModText.Get("craftingTerminal.confirm.title"), new Vector2(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 24), Game1.textColor);
        b.DrawString(Game1.smallFont, this.title, new Vector2(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 62), Game1.textColor);

        SVSAPMenuWidgets.DrawInsetBox(b, this.contentBounds);
        var maxLines = Math.Max(1, (this.contentBounds.Height - 20) / LineHeight);
        var first = Math.Clamp(this.scrollOffset, 0, Math.Max(0, this.rows.Count - maxLines));
        for (var i = 0; i < maxLines && first + i < this.rows.Count; i++)
        {
            var row = this.rows[first + i];
            var y = this.contentBounds.Y + 10 + i * LineHeight;
            SVSAPMenuWidgets.DrawPixelStatusLight(b, this.contentBounds.X + 14, y + 7, row.Status);
            var color = row.Status == PixelStatus.Error
                ? Color.Crimson
                : row.Status == PixelStatus.Ready
                    ? Color.DarkGreen
                    : Game1.textColor;
            SVSAPMenuWidgets.DrawFittedLine(
                b,
                row.Text,
                new Rectangle(this.contentBounds.X + 34, y, this.contentBounds.Width - 48, LineHeight - 2),
                color);
        }

        if (this.rows.Count > maxLines)
        {
            var text = $"{first + 1}-{Math.Min(this.rows.Count, first + maxLines)}/{this.rows.Count}";
            var size = Game1.smallFont.MeasureString(text);
            b.DrawString(
                Game1.smallFont,
                text,
                new Vector2(this.contentBounds.Right - size.X - 12, this.contentBounds.Bottom - size.Y - 8),
                Color.DimGray);
        }

        SVSAPMenuWidgets.DrawButton(b, this.cancelButton);
        SVSAPMenuWidgets.DrawButton(b, this.confirmButton, tint: this.canConfirm ? Color.LightGreen : Color.Gray);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        if (this.cancelButton.containsPoint(x, y))
        {
            this.CloseToParent("bigDeSelect");
            return;
        }

        if (!this.confirmButton.containsPoint(x, y))
            return;

        if (!this.canConfirm)
        {
            Game1.playSound("cancel");
            return;
        }

        this.CloseToParent("smallSelect");
        this.onConfirm();
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        var maxVisible = Math.Max(1, (this.contentBounds.Height - 20) / LineHeight);
        var maxOffset = Math.Max(0, this.rows.Count - maxVisible);
        this.scrollOffset = Math.Clamp(this.scrollOffset + (direction > 0 ? -1 : 1), 0, maxOffset);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            this.CloseToParent("bigDeSelect");
            return;
        }

        if (key == Keys.Enter)
        {
            if (!this.canConfirm)
            {
                Game1.playSound("cancel");
                return;
            }
            this.CloseToParent("smallSelect");
            this.onConfirm();
            return;
        }

        base.receiveKeyPress(key);
    }

    protected override void cleanupBeforeExit()
    {
        Game1.activeClickableMenu = this.parent;
    }

    private void CloseToParent(string sound)
    {
        Game1.activeClickableMenu = this.parent;
        Game1.playSound(sound);
    }

    private static IReadOnlyList<ConfirmationRow> BuildRows(
        IReadOnlyList<string> summaryLines,
        IReadOnlyList<CraftingIngredientAvailability> ingredients)
    {
        var canCraft = ingredients.All(ingredient => ingredient.IsSufficient);
        var rows = new List<ConfirmationRow>();
        for (var i = 0; i < summaryLines.Count; i++)
        {
            rows.Add(new ConfirmationRow(
                summaryLines[i],
                i == 0 ? PixelStatus.Idle : canCraft ? PixelStatus.Ready : PixelStatus.Error));
        }

        rows.AddRange(ingredients.Select(ingredient => new ConfirmationRow(
            ingredient.ToDisplayLine(),
            ingredient.IsSufficient ? PixelStatus.Ready : PixelStatus.Error)));
        return rows;
    }

    private sealed record ConfirmationRow(string Text, PixelStatus Status);
}
