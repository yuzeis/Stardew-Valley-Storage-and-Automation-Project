using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal sealed class PatternTerminalMenu : IClickableMenu
{
    private readonly PatternEncodingService patternEncodingService;
    private readonly List<ClickableComponent> modeButtons = new();
    private readonly SVSAPIconGrid<PatternData> patternGrid = new();
    private PatternKind mode = PatternKind.Crafting;
    private Rectangle searchBox;
    private Rectangle gridArea;
    private string search = string.Empty;

    public PatternTerminalMenu(PatternEncodingService patternEncodingService)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - 980) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - 680) / 2),
            width: 980,
            height: 680,
            showUpperRightCloseButton: true)
    {
        this.patternEncodingService = patternEncodingService;
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
        this.modeButtons.Add(new ClickableComponent(new Rectangle(innerX, buttonY, 120, 42), PatternKind.Crafting.ToString(), "合成"));
        this.modeButtons.Add(new ClickableComponent(new Rectangle(innerX + 132, buttonY, 132, 42), PatternKind.Processing.ToString(), "加工"));

        var gridTop = top + 108;
        var gridBottom = buttonY - 18;
        this.gridArea = new Rectangle(innerX, gridTop, innerW, Math.Max(SVSAPMenuWidgets.Cell, gridBottom - gridTop));
        this.patternGrid.SetBounds(this.gridArea);
    }

    public override void draw(SpriteBatch b)
    {
        SVSAPMenuWidgets.DrawPanel(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var patterns = this.GetVisiblePatterns();
        this.patternGrid.ClampScroll(patterns.Count);
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + 24;
        b.DrawString(Game1.dialogueFont, $"样板终端  -  {FormatPatternKind(this.mode)}  -  {patterns.Count:N0} 个选项", new Vector2(innerX, top), Game1.textColor);
        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);
        b.DrawString(Game1.smallFont, "点击图标，把配方写入空白样板。", new Vector2(this.searchBox.Right + 24, this.searchBox.Y + 10), Game1.textColor);

        this.patternGrid.Draw(
            b,
            patterns,
            pattern => SVSAPMenuWidgets.CreateIconItem(pattern.Outputs.FirstOrDefault(), pattern.Outputs.FirstOrDefault()?.Count ?? 1),
            pattern => pattern.Outputs.FirstOrDefault()?.Count ?? 1,
            getBadge: pattern => pattern.Kind == PatternKind.Processing ? "加" : null);

        if (patterns.Count == 0)
        {
            b.DrawString(
                Game1.smallFont,
                "没有匹配的样板选项。",
                new Vector2(this.gridArea.X + 8, this.gridArea.Y + 8),
                Color.DarkSlateGray);
        }

        foreach (var button in this.modeButtons)
            SVSAPMenuWidgets.DrawButton(b, button, tint: button.name == this.mode.ToString() ? Color.LightGreen : Color.White);

        this.DrawHoverTooltip(b, patterns);
        this.upperRightCloseButton?.draw(b);
        this.drawMouse(b);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        foreach (var button in this.modeButtons)
        {
            if (!button.containsPoint(x, y))
                continue;

            this.mode = Enum.Parse<PatternKind>(button.name);
            this.ResetScroll();
            Game1.playSound("smallSelect");
            return;
        }

        var patterns = this.GetVisiblePatterns();
        this.patternGrid.ClampScroll(patterns.Count);
        var pattern = this.patternGrid.HitTest(x, y, patterns);
        if (pattern is null)
            return;

        if (this.patternEncodingService.TryEncode(pattern, out var message))
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
        this.patternGrid.ScrollOffset += direction > 0 ? -1 : 1;
        this.patternGrid.ClampScroll(this.GetVisiblePatterns().Count);
    }

    private List<PatternData> GetVisiblePatterns()
    {
        var patterns = this.mode == PatternKind.Crafting
            ? this.patternEncodingService.GetCraftingPatterns()
            : this.patternEncodingService.GetProcessingPatterns();

        if (string.IsNullOrWhiteSpace(this.search))
            return patterns;

        return patterns
            .Where(pattern => pattern.DisplayName.Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || pattern.Inputs.Any(input => input.DisplayKey.Contains(this.search, StringComparison.OrdinalIgnoreCase))
                || pattern.Outputs.Any(output => output.DisplayKey.Contains(this.search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void ResetScroll()
    {
        this.patternGrid.ResetScroll();
    }

    private void DrawHoverTooltip(SpriteBatch b, IReadOnlyList<PatternData> patterns)
    {
        var mx = Game1.getMouseX();
        var my = Game1.getMouseY();
        var pattern = this.patternGrid.HitTest(mx, my, patterns);
        if (pattern is null)
            return;

        var lines = new List<string>
        {
            pattern.Kind == PatternKind.Processing
                ? $"加工 · {FormatSpeedClass(pattern.SpeedClass)} · {pattern.ProcessingMinutes:N0} 分钟"
                : "合成",
            "输入：" + FormatRequests(pattern.Inputs),
            "输出：" + FormatRequests(pattern.Outputs)
        };
        if (!string.IsNullOrWhiteSpace(pattern.MachineQualifiedItemId))
            lines.Add("机器：" + pattern.MachineQualifiedItemId);

        SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, pattern.DisplayName, lines);
    }

    private static string FormatRequests(IReadOnlyList<NetworkItemRequest> requests)
    {
        if (requests.Count == 0)
            return "无";

        return string.Join(", ", requests.Take(4).Select(request => $"{request.DisplayKey} x{request.Count:N0}"))
            + (requests.Count > 4 ? $" +{requests.Count - 4:N0}" : string.Empty);
    }

    private static string FormatPatternKind(PatternKind kind)
    {
        return kind == PatternKind.Processing ? "加工" : "合成";
    }

    private static string FormatSpeedClass(ProcessingSpeedClass speedClass)
    {
        return speedClass switch
        {
            ProcessingSpeedClass.Slow => "慢速",
            ProcessingSpeedClass.Medium => "中速",
            _ => "快速"
        };
    }
}
