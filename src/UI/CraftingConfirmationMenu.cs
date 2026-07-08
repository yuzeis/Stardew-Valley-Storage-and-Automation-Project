using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class CraftingConfirmationMenu : IClickableMenu
{
    private const int Pad = 28;
    private const int LineHeight = 28;

    private readonly IClickableMenu parent;
    private readonly string title;
    private readonly IReadOnlyList<string> lines;
    private readonly Action onConfirm;
    private readonly ClickableComponent confirmButton;
    private readonly ClickableComponent cancelButton;
    private readonly Rectangle contentBounds;
    private int scrollOffset;

    public CraftingConfirmationMenu(IClickableMenu parent, string title, IReadOnlyList<string> lines, Action onConfirm)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - 720) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - 520) / 2),
            width: 720,
            height: 520,
            showUpperRightCloseButton: true)
    {
        this.parent = parent;
        this.title = title;
        this.lines = lines;
        this.onConfirm = onConfirm;
        this.contentBounds = new Rectangle(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 92, this.width - Pad * 2, this.height - 168);
        this.cancelButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - 296, this.yPositionOnScreen + this.height - 60, 120, 42), "cancel", ModText.Get("craftingTerminal.confirm.cancel"));
        this.confirmButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + this.width - 164, this.yPositionOnScreen + this.height - 60, 120, 42), "confirm", ModText.Get("craftingTerminal.confirm.craft"));
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
        b.DrawString(Game1.dialogueFont, ModText.Get("craftingTerminal.confirm.title"), new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 24), Game1.textColor);
        b.DrawString(Game1.smallFont, this.title, new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 62), Game1.textColor);

        SVSAPMenuWidgets.DrawInsetBox(b, this.contentBounds);
        var maxLines = Math.Max(1, (this.contentBounds.Height - 20) / LineHeight);
        var first = Math.Clamp(this.scrollOffset, 0, Math.Max(0, this.lines.Count - maxLines));
        for (var i = 0; i < maxLines && first + i < this.lines.Count; i++)
        {
            b.DrawString(
                Game1.smallFont,
                this.lines[first + i],
                new Vector2(this.contentBounds.X + 16, this.contentBounds.Y + 10 + i * LineHeight),
                Game1.textColor);
        }

        if (this.lines.Count > maxLines)
        {
            var text = $"{first + 1}-{Math.Min(this.lines.Count, first + maxLines)}/{this.lines.Count}";
            var size = Game1.smallFont.MeasureString(text);
            b.DrawString(
                Game1.smallFont,
                text,
                new Vector2(this.contentBounds.Right - size.X - 12, this.contentBounds.Bottom - size.Y - 8),
                Color.DimGray);
        }

        SVSAPMenuWidgets.DrawButton(b, this.cancelButton);
        SVSAPMenuWidgets.DrawButton(b, this.confirmButton, tint: Color.LightGreen);
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

        this.CloseToParent("smallSelect");
        this.onConfirm();
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        var maxVisible = Math.Max(1, (this.contentBounds.Height - 20) / LineHeight);
        var maxOffset = Math.Max(0, this.lines.Count - maxVisible);
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
}
