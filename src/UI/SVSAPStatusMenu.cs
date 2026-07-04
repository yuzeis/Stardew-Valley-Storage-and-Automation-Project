using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class SVSAPStatusMenu : IClickableMenu
{
    private const int Pad = 28;
    private const int LineHeight = 28;
    private const int ButtonHeight = 42;

    private readonly string title;
    private readonly Func<IReadOnlyList<string>> getLines;
    private readonly List<SVSAPMenuAction> actions;
    private readonly List<ClickableComponent> actionButtons = new();
    private int scrollOffset;

    public SVSAPStatusMenu(
        string title,
        Func<IReadOnlyList<string>> getLines,
        IEnumerable<SVSAPMenuAction>? actions = null)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.title = title;
        this.getLines = getLines;
        this.actions = actions?.ToList() ?? new List<SVSAPMenuAction>();
        this.BuildLayout();
        SVSAPMenuWidgets.PositionCloseButton(this.upperRightCloseButton, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
    }

    private static int GetMenuWidth() => Math.Min(820, Game1.uiViewport.Width - 80);

    private static int GetMenuHeight() => Math.Min(660, Game1.uiViewport.Height - 80);

    private int ActionRows => this.actions.Count == 0 ? 0 : (int)Math.Ceiling(this.actions.Count / 3d);

    private Rectangle ContentBounds => new(
        this.xPositionOnScreen + Pad,
        this.yPositionOnScreen + 92,
        this.width - Pad * 2,
        this.height - 128 - this.ActionRows * 52);

    private void BuildLayout()
    {
        this.actionButtons.Clear();
        if (this.actions.Count == 0)
            return;

        var columns = Math.Min(3, Math.Max(1, this.actions.Count));
        var buttonWidth = Math.Max(120, (this.width - Pad * 2 - (columns - 1) * 12) / columns);
        var startY = this.yPositionOnScreen + this.height - 28 - this.ActionRows * 52;
        for (var i = 0; i < this.actions.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            var x = this.xPositionOnScreen + Pad + column * (buttonWidth + 12);
            var y = startY + row * 52;
            this.actionButtons.Add(new ClickableComponent(new Rectangle(x, y, buttonWidth, ButtonHeight), this.actions[i].Label, this.actions[i].Label));
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            this.exitThisMenu();
            return;
        }

        for (var i = 0; i < this.actionButtons.Count && i < this.actions.Count; i++)
        {
            if (!this.actionButtons[i].containsPoint(x, y) || !this.actions[i].IsEnabled())
                continue;

            var message = this.actions[i].Execute();
            if (!string.IsNullOrWhiteSpace(message))
                Game1.addHUDMessage(new HUDMessage(message, HUDMessage.newQuest_type));
            Game1.playSound("smallSelect");
            return;
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        var lines = this.GetWrappedLines();
        var visibleRows = Math.Max(1, this.ContentBounds.Height / LineHeight);
        var maxOffset = Math.Max(0, lines.Count - visibleRows);
        this.scrollOffset = direction > 0
            ? Math.Max(0, this.scrollOffset - 1)
            : Math.Min(maxOffset, this.scrollOffset + 1);
    }

    public override void draw(SpriteBatch b)
    {
        var panel = new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height);
        SVSAPMenuWidgets.DrawPanel(b, panel);
        Utility.drawTextWithShadow(b, this.title, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + Pad, this.yPositionOnScreen + 28), Game1.textColor);

        var content = this.ContentBounds;
        SVSAPMenuWidgets.DrawInsetBox(b, content);
        var lines = this.GetWrappedLines();
        var visibleRows = Math.Max(1, content.Height / LineHeight);
        this.scrollOffset = Math.Clamp(this.scrollOffset, 0, Math.Max(0, lines.Count - visibleRows));
        var count = Math.Min(visibleRows, Math.Max(0, lines.Count - this.scrollOffset));
        for (var i = 0; i < count; i++)
            b.DrawString(Game1.smallFont, lines[this.scrollOffset + i], new Vector2(content.X + 16, content.Y + 14 + i * LineHeight), Game1.textColor);

        if (lines.Count > visibleRows)
        {
            var marker = $"{this.scrollOffset + 1}-{this.scrollOffset + count}/{lines.Count}";
            var size = Game1.smallFont.MeasureString(marker);
            b.DrawString(Game1.smallFont, marker, new Vector2(content.Right - size.X - 12, content.Bottom - size.Y - 8), Color.DimGray);
        }

        for (var i = 0; i < this.actionButtons.Count && i < this.actions.Count; i++)
            SVSAPMenuWidgets.DrawButton(b, this.actionButtons[i], tint: this.actions[i].IsEnabled() ? Color.White : Color.Gray * 0.7f);

        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    private List<string> GetWrappedLines()
    {
        var width = Math.Max(160, this.ContentBounds.Width - 32);
        var result = new List<string>();
        foreach (var line in this.getLines())
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add(string.Empty);
                continue;
            }

            result.AddRange(WrapLine(line, width));
        }

        return result;
    }

    private static IEnumerable<string> WrapLine(string line, int maxWidth)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            yield break;

        var current = words[0];
        for (var i = 1; i < words.Length; i++)
        {
            var candidate = current + " " + words[i];
            if (Game1.smallFont.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            yield return current;
            current = words[i];
        }

        yield return current;
    }
}

internal sealed class SVSAPMenuAction
{
    private readonly Func<bool>? isEnabled;

    public SVSAPMenuAction(string label, Func<string?> execute, Func<bool>? isEnabled = null)
    {
        this.Label = label;
        this.Execute = execute;
        this.isEnabled = isEnabled;
    }

    public string Label { get; }
    public Func<string?> Execute { get; }
    public bool IsEnabled() => this.isEnabled?.Invoke() ?? true;
}
