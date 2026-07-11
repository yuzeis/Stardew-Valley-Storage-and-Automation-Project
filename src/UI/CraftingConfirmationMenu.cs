using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class CraftingConfirmationMenu : IClickableMenu
{
    private const int Pad = SVSAPMenuWidgets.Pad;
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
    private bool parentRestored;

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
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.parent = parent;
        this.title = title;
        this.rows = rows;
        this.canConfirm = canConfirm;
        this.onConfirm = onConfirm;
        var buttonY = this.yPositionOnScreen + this.height - Pad - 42;
        this.contentBounds = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 116, this.width - Pad * 2, buttonY - this.yPositionOnScreen - 128);
        this.cancelButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - Pad - 252, buttonY, 120, 42), "cancel", ModText.Get("craftingTerminal.confirm.cancel"));
        this.confirmButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - Pad - 120, buttonY, 120, 42), "confirm", ModText.Get("craftingTerminal.confirm.craft"));
        (this.parent as ISearchTextInputOwner)?.SuspendSearchInput();
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private static int GetMenuWidth() => Math.Max(1, Math.Min(720, Game1.uiViewport.Width - 32));

    private static int GetMenuHeight() => Math.Max(1, Math.Min(520, Game1.uiViewport.Height - 32));

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        SVSAPMenuWidgets.DrawFittedTitle(
            b,
            ModText.Get("craftingTerminal.confirm.title"),
            new Rectangle(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 18, this.width - Pad * 2 - 68, 40),
            Game1.textColor);
        SVSAPMenuWidgets.DrawFittedLine(
            b,
            this.title,
            new Rectangle(this.xPositionOnScreen + Pad + 12, this.yPositionOnScreen + 80, this.width - Pad * 2 - 24, 24),
            Game1.textColor,
            horizontalPadding: 0);

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
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            base.receiveLeftClick(x, y, playSound);
            return;
        }

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
        this.RestoreParent();
    }

    private void CloseToParent(string sound)
    {
        this.RestoreParent();
        Game1.playSound(sound);
    }

    private void RestoreParent()
    {
        if (this.parentRestored)
            return;

        this.parentRestored = true;
        Game1.activeClickableMenu = this.parent;
        (this.parent as ISearchTextInputOwner)?.ResumeSearchInput();
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
