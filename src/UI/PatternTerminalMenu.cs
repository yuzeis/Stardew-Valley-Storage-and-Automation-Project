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
    private readonly TextBox searchInput;

    public PatternTerminalMenu(PatternEncodingService patternEncodingService)
        : base(
            x: Math.Max(0, (Game1.uiViewport.Width - GetMenuWidth()) / 2),
            y: Math.Max(0, (Game1.uiViewport.Height - GetMenuHeight()) / 2),
            width: GetMenuWidth(),
            height: GetMenuHeight(),
            showUpperRightCloseButton: true)
    {
        this.patternEncodingService = patternEncodingService;
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

        var buttonY = this.yPositionOnScreen + this.height - 54;
        this.modeButtons.Add(new ClickableComponent(new Rectangle(innerX, buttonY, 120, 42), PatternKind.Crafting.ToString(), ModText.Get("patternTerminal.mode.crafting")));
        this.modeButtons.Add(new ClickableComponent(new Rectangle(innerX + 132, buttonY, 132, 42), PatternKind.Processing.ToString(), ModText.Get("patternTerminal.mode.processing")));

        var gridTop = top + 108;
        var gridBottom = buttonY - 18;
        this.gridArea = new Rectangle(innerX, gridTop, innerW, Math.Max(SVSAPMenuWidgets.Cell, gridBottom - gridTop));
        this.patternGrid.SetBounds(this.gridArea);
    }

    public override void draw(SpriteBatch b)
    {
        this.SyncSearchFromInput();
        SVSAPMenuWidgets.DrawStardewAE2Frame(b, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));

        var patterns = this.GetVisiblePatterns();
        this.patternGrid.ClampScroll(patterns.Count);
        var innerX = this.xPositionOnScreen + SVSAPMenuWidgets.Pad;
        var top = this.yPositionOnScreen + 24;
        b.DrawString(Game1.dialogueFont, ModText.Format("patternTerminal.title", FormatPatternKind(this.mode), patterns.Count), new Vector2(innerX + 12, top), Game1.textColor);
        SVSAPMenuWidgets.DrawSearchBox(b, this.searchBox, this.search);
        b.DrawString(Game1.smallFont, ModText.Get("patternTerminal.help"), new Vector2(this.searchBox.Right + 24, this.searchBox.Y + 10), Game1.textColor);

        this.patternGrid.Draw(
            b,
            patterns,
            pattern => SVSAPMenuWidgets.CreateIconItem(pattern.Outputs.FirstOrDefault(), pattern.Outputs.FirstOrDefault()?.Count ?? 1),
            pattern => pattern.Outputs.FirstOrDefault()?.Count ?? 1,
            getBadge: pattern => pattern.Kind == PatternKind.Processing ? ModText.Get("patternTerminal.badge.processing") : null);

        if (patterns.Count == 0)
        {
            b.DrawString(
                Game1.smallFont,
                ModText.Get("patternTerminal.empty"),
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
        this.patternGrid.ScrollOffset += direction > 0 ? -1 : 1;
        this.patternGrid.ClampScroll(this.GetVisiblePatterns().Count);
    }

    private List<PatternData> GetVisiblePatterns()
    {
        this.SyncSearchFromInput();
        var patterns = this.mode == PatternKind.Crafting
            ? this.patternEncodingService.GetCraftingPatterns()
            : this.patternEncodingService.GetProcessingPatterns();

        if (string.IsNullOrWhiteSpace(this.search))
            return patterns;

        return patterns
            .Where(pattern => PatternDisplayNames.Get(pattern).Contains(this.search, StringComparison.CurrentCultureIgnoreCase)
                || pattern.Inputs.Any(input => input.DisplayKey.Contains(this.search, StringComparison.OrdinalIgnoreCase))
                || pattern.Outputs.Any(output => output.DisplayKey.Contains(this.search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void ResetScroll()
    {
        this.patternGrid.ResetScroll();
    }

    private void SyncSearchFromInput()
    {
        if (SVSAPMenuWidgets.SyncSearchText(this.searchInput, ref this.search))
            this.ResetScroll();
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
                ? ModText.Format("patternTerminal.tooltip.processing", FormatSpeedClass(pattern.SpeedClass), pattern.ProcessingMinutes)
                : ModText.Get("patternTerminal.tooltip.crafting"),
            ModText.Format("patternTerminal.tooltip.inputs", FormatRequests(pattern.Inputs)),
            ModText.Format("patternTerminal.tooltip.outputs", FormatRequests(pattern.Outputs))
        };
        if (!string.IsNullOrWhiteSpace(pattern.MachineQualifiedItemId))
        {
            var machineName = pattern.MachineQualifiedItemId;
            try
            {
                machineName = ItemRegistry.Create(pattern.MachineQualifiedItemId).DisplayName;
            }
            catch {}
            lines.Add(ModText.Format("patternTerminal.tooltip.machine", machineName));
        }

        SVSAPMenuWidgets.DrawTooltipBox(b, mx + 28, my + 28, PatternDisplayNames.Get(pattern), lines);
    }

    private static string FormatRequests(IReadOnlyList<NetworkItemRequest> requests)
    {
        if (requests.Count == 0)
            return ModText.Get("common.none");

        return string.Join(", ", requests.Take(4).Select(request => $"{ItemDisplayService.GetRequestDisplayName(request)} x{request.Count:N0}"))
            + (requests.Count > 4 ? $" +{requests.Count - 4:N0}" : string.Empty);
    }

    private static string FormatPatternKind(PatternKind kind)
    {
        return kind == PatternKind.Processing ? ModText.Get("patternTerminal.mode.processing") : ModText.Get("patternTerminal.mode.crafting");
    }

    private static string FormatSpeedClass(ProcessingSpeedClass speedClass)
    {
        return speedClass switch
        {
            ProcessingSpeedClass.Slow => ModText.Get("patternTerminal.speed.slow"),
            ProcessingSpeedClass.Medium => ModText.Get("patternTerminal.speed.medium"),
            _ => ModText.Get("patternTerminal.speed.fast")
        };
    }
}
