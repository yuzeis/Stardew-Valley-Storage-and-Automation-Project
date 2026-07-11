using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SVSAP.Models;
using SVSAP.Services;
using StardewValley;
using StardewValley.Menus;

namespace SVSAP.UI;

internal static class SVSAPMenuWidgets
{
    public const int Cell = 68;
    public const int IconInset = 2;
    public const int FramePad = 28;
    public const int FrameHeaderHeight = 40;
    public const int FrameContentInset = 12;
    public const int FrameBevelWidth = 2;
    public static int VisualContentGutter => FrameContentInset - FrameBevelWidth;
    public const int Pad = FramePad + FrameContentInset;
    public const int HeaderTopOffset = FramePad + 8;
    public const int ContentTopOffset = FramePad + FrameHeaderHeight + FrameContentInset;
    public const int BackpackColumns = 12;

    private static readonly Rectangle PanelSource = new(0, 256, 60, 60);
    private static readonly Rectangle PanelCenterSource = new(12, 268, 36, 36);
    private static readonly List<string> SlotGeometryViolations = new();
    private static int qualityOverlayDrawCount;

    public static void DrawPixelStatusLight(SpriteBatch b, int x, int y, PixelStatus status)
    {
        var lightColor = GetStatusColor(status);

        // Draw 8x8 round status light
        // Draw shadow/outline first
        b.Draw(Game1.staminaRect, new Rectangle(x - 1, y - 1, 10, 10), Color.Black * 0.45f);
        // Draw base color
        b.Draw(Game1.staminaRect, new Rectangle(x, y, 8, 8), lightColor);
        // Draw inner glow/specular dot
        b.Draw(Game1.staminaRect, new Rectangle(x + 1, y + 1, 2, 2), Color.White * 0.7f);
    }

    public static void DrawSlotStatusLine(SpriteBatch b, Rectangle slotBounds, PixelStatus status)
    {
        var lightColor = GetStatusColor(status);
        b.Draw(Game1.staminaRect, new Rectangle(slotBounds.X + 2, slotBounds.Bottom - 4, slotBounds.Width - 4, 2), lightColor * 0.85f);
    }

    public static void DrawStardewAE2Frame(SpriteBatch b, Rectangle panel)
    {
        // 1. Draw outer stardew wood panel
        DrawPanel(b, panel);
        // 2. Draw inner metal tech frame (cool grey background with bevel)
        var inner = new Rectangle(
            panel.X + FramePad,
            panel.Y + FramePad + FrameHeaderHeight,
            panel.Width - FramePad * 2,
            panel.Height - FramePad * 2 - FrameHeaderHeight);
        if (inner.Width > 0 && inner.Height > 0)
        {
            DrawWorkspaceInset(b, inner, new Color(202, 212, 218));
        }
    }

    private static void DrawWorkspaceInset(SpriteBatch b, Rectangle bounds, Color tint)
    {
        DrawInsetBox(b, bounds, tint);

        var center = new Rectangle(
            bounds.X + FrameBevelWidth,
            bounds.Y + FrameBevelWidth,
            Math.Max(1, bounds.Width - FrameBevelWidth * 2),
            Math.Max(1, bounds.Height - FrameBevelWidth * 2));
        b.Draw(Game1.menuTexture, center, PanelCenterSource, tint);
    }

    public static Rectangle GetFrameContentBounds(Rectangle panel)
    {
        return new Rectangle(
            panel.X + Pad,
            panel.Y + ContentTopOffset,
            Math.Max(1, panel.Width - Pad * 2),
            Math.Max(1, panel.Height - ContentTopOffset - Pad));
    }

    private static Color GetStatusColor(PixelStatus status)
    {
        return status switch
        {
            PixelStatus.Idle => Color.Gray * 0.6f,
            PixelStatus.Ready => Color.LimeGreen,
            PixelStatus.Processing => Color.Yellow,
            PixelStatus.Warning => Color.Orange,
            PixelStatus.Error => Color.Crimson,
            PixelStatus.Offline => Color.Red * 0.6f,
            PixelStatus.Disabled => Color.DarkGray * 0.4f,
            _ => Color.Gray
        };
    }

    public static void DrawPanel(SpriteBatch b, Rectangle panel)
    {
        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            PanelSource,
            panel.X,
            panel.Y,
            panel.Width,
            panel.Height,
            Color.White,
            1f,
            true);
    }

    public static void DrawInsetBox(SpriteBatch b, Rectangle bounds, Color? tint = null)
    {
        IClickableMenu.drawTextureBox(
            b,
            Game1.menuTexture,
            PanelSource,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            tint ?? Color.White,
            1f,
            false);
    }

    public static void PositionCloseButton(ClickableTextureComponent? closeButton, Rectangle panel)
    {
        if (closeButton is null)
            return;

        closeButton.bounds.X = panel.X + panel.Width - 64;
        closeButton.bounds.Y = panel.Y + 16;
    }

    internal static int GetTerminalItemGridBottom(int backpackTop) => backpackTop - 50;

    internal static int CalculateTerminalSearchWidth(int innerWidth, int reservedRightWidth)
    {
        innerWidth = Math.Max(1, innerWidth);
        var maximum = Math.Min(380, innerWidth);
        var minimum = Math.Min(120, maximum);
        return Math.Clamp(innerWidth - Math.Max(0, reservedRightWidth), minimum, maximum);
    }

    internal static int CalculateUniformButtonWidth(int availableWidth, int count, int gap, int maximumWidth = 100)
    {
        count = Math.Max(1, count);
        gap = Math.Max(0, gap);
        var fitWidth = (Math.Max(1, availableWidth) - gap * (count - 1)) / count;
        return Math.Clamp(fitWidth, 1, Math.Max(1, maximumWidth));
    }

    internal static int CalculateTerminalCellSize(
        int innerWidth,
        int gridTop,
        int bottomButtonY,
        int inventorySlotCount,
        int preferredCellSize = 52,
        int minimumCellSize = 40)
    {
        preferredCellSize = Math.Max(40, preferredCellSize);
        minimumCellSize = Math.Clamp(minimumCellSize, 40, preferredCellSize);
        for (var cellSize = preferredCellSize; cellSize >= minimumCellSize; cellSize--)
        {
            var columns = SVSAPBackpackGrid.GetColumnCount(innerWidth, cellSize);
            var rows = Math.Max(1, (int)Math.Ceiling(Math.Max(0, inventorySlotCount) / (double)columns));
            var inventoryTop = bottomButtonY - 18 - rows * cellSize;
            if (GetTerminalItemGridBottom(inventoryTop) - gridTop >= cellSize)
                return cellSize;
        }

        return minimumCellSize;
    }

    internal static (int Columns, int Rows, int CellSize) CalculatePatternProviderSlotLayout(
        int availableWidth,
        int availableHeight,
        int slotCount = 36,
        int preferredCellSize = 56,
        int minimumColumns = 4,
        int maximumColumns = 8)
    {
        availableWidth = Math.Max(1, availableWidth);
        availableHeight = Math.Max(1, availableHeight);
        slotCount = Math.Max(1, slotCount);
        minimumColumns = Math.Max(1, minimumColumns);
        maximumColumns = Math.Max(minimumColumns, maximumColumns);

        var bestColumns = minimumColumns;
        var bestRows = (int)Math.Ceiling(slotCount / (double)bestColumns);
        var bestCellSize = 1;
        for (var columns = minimumColumns; columns <= maximumColumns; columns++)
        {
            var rows = (int)Math.Ceiling(slotCount / (double)columns);
            var cellSize = Math.Min(preferredCellSize, Math.Min(availableWidth / columns, availableHeight / rows));
            if (cellSize <= bestCellSize)
                continue;

            bestColumns = columns;
            bestRows = rows;
            bestCellSize = cellSize;
        }

        return (bestColumns, bestRows, Math.Max(1, bestCellSize));
    }

    internal static bool TerminalLayoutFits(int menuWidth, int menuHeight, int inventorySlotCount, int cellSize = 52)
    {
        var innerWidth = Math.Max(1, menuWidth - Pad * 2);
        var gridTop = HeaderTopOffset + 136 + 50;
        var bottomY = menuHeight - Pad - 42;
        cellSize = CalculateTerminalCellSize(innerWidth, gridTop, bottomY, inventorySlotCount, cellSize);
        var columns = Math.Clamp(Math.Max(1, innerWidth / Math.Max(40, cellSize)), 1, BackpackColumns);
        var inventoryRows = Math.Max(1, (int)Math.Ceiling(Math.Max(0, inventorySlotCount) / (double)columns));
        var inventoryTop = bottomY - 18 - inventoryRows * cellSize;
        var gridHeight = GetTerminalItemGridBottom(inventoryTop) - gridTop;
        var lockedLineTop = inventoryTop - 42;
        return gridHeight >= cellSize
            && gridTop + gridHeight <= lockedLineTop
            && lockedLineTop + 30 <= inventoryTop - 10
            && inventoryTop > gridTop;
    }

    internal static (int JobRows, int PipelineRows) CalculateMonitorRowAllocation(
        int contentHeight,
        int jobCount,
        int pipelineCount,
        int preferredJobRows,
        int preferredPipelineRows)
    {
        contentHeight = Math.Max(0, contentHeight);
        jobCount = Math.Max(0, jobCount);
        pipelineCount = Math.Max(0, pipelineCount);

        var jobRows = Math.Min(jobCount, Math.Max(0, preferredJobRows));
        var pipelineRows = Math.Min(pipelineCount, Math.Max(0, preferredPipelineRows));
        var minimumJobRows = jobCount > 0 ? 1 : 0;
        var minimumPipelineRows = pipelineCount > 0 ? 1 : 0;

        while (MeasureMonitorRows(jobCount, pipelineCount, jobRows, pipelineRows) > contentHeight)
        {
            if (jobRows > minimumJobRows)
            {
                jobRows--;
                continue;
            }

            if (pipelineRows > minimumPipelineRows)
            {
                pipelineRows--;
                continue;
            }

            if (pipelineRows > 0)
            {
                pipelineRows--;
                continue;
            }

            if (jobRows > 0)
            {
                jobRows--;
                continue;
            }

            break;
        }

        return (jobRows, pipelineRows);
    }

    internal static int MeasureMonitorRows(int jobCount, int pipelineCount, int jobRows, int pipelineRows)
    {
        var height = Math.Max(0, jobRows) * 46;
        if (jobCount <= 0)
            height += 38;
        else if (jobRows < jobCount)
            height += 32;

        if (pipelineCount > 0)
            height += 16 + Math.Max(0, pipelineRows) * 42;

        return height;
    }

    internal static IReadOnlyList<Rectangle> CalculateRightAlignedButtonRow(
        int menuX,
        int menuWidth,
        int y,
        int buttonCount,
        int preferredWidth,
        int height,
        int gap,
        int horizontalMargin)
    {
        if (buttonCount <= 0)
            return Array.Empty<Rectangle>();

        horizontalMargin = Math.Max(0, horizontalMargin);
        var availableWidth = Math.Max(buttonCount, menuWidth - horizontalMargin * 2);
        gap = buttonCount == 1
            ? 0
            : Math.Min(Math.Max(0, gap), Math.Max(0, (availableWidth - buttonCount) / (buttonCount - 1)));
        var totalGap = gap * Math.Max(0, buttonCount - 1);
        var buttonWidth = Math.Max(1, Math.Min(preferredWidth, (availableWidth - totalGap) / buttonCount));
        var totalWidth = buttonWidth * buttonCount + totalGap;
        var x = menuX + menuWidth - horizontalMargin - totalWidth;
        var result = new Rectangle[buttonCount];
        for (var index = 0; index < buttonCount; index++)
            result[index] = new Rectangle(x + index * (buttonWidth + gap), y, buttonWidth, Math.Max(1, height));

        return result;
    }

    public static void DrawSearchBox(SpriteBatch b, Rectangle bounds, string value)
    {
        DrawInsetBox(b, bounds);
        var empty = string.IsNullOrEmpty(value);
        var searchText = empty ? ModText.Get("ui.search") : value;
        var maxWidth = Math.Max(1, bounds.Width - 28);
        searchText = empty
            ? Ellipsize(Game1.smallFont, searchText, maxWidth)
            : EllipsizeFromStart(Game1.smallFont, searchText, maxWidth);
        var searchColor = empty ? Color.Gray : Game1.textColor;
        b.DrawString(Game1.smallFont, searchText, new Vector2(bounds.X + 14, bounds.Y + 10), searchColor);
    }

    public static TextBox CreateSearchTextBox(Rectangle bounds, string value)
    {
        var search = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), Game1.staminaRect, Game1.smallFont, Game1.textColor)
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Text = NormalizeSearchText(value),
            Selected = true
        };
        search.textLimit = 40;
        Game1.keyboardDispatcher.Subscriber = search;
        return search;
    }

    public static bool SyncSearchText(TextBox searchInput, ref string value)
    {
        var normalized = NormalizeSearchText(searchInput.Text);
        if (string.Equals(normalized, value, StringComparison.Ordinal))
            return false;

        value = normalized;
        if (!string.Equals(searchInput.Text, normalized, StringComparison.Ordinal))
            searchInput.Text = normalized;

        return true;
    }

    public static void ReleaseSearchTextBox(TextBox searchInput)
    {
        if (Game1.keyboardDispatcher.Subscriber == searchInput)
            Game1.keyboardDispatcher.Subscriber = null!;

        searchInput.Selected = false;
    }

    public static void ActivateSearchTextBox(TextBox searchInput)
    {
        searchInput.Selected = true;
        Game1.keyboardDispatcher.Subscriber = searchInput;
    }

    public static void DrawTab(SpriteBatch b, ClickableComponent button, bool selected)
    {
        DrawInsetBox(b, button.bounds, selected ? Color.LightGreen : Color.White);
        DrawTextFit(b, button.label, button.bounds, Game1.textColor, 8);
    }

    public static void DrawButton(SpriteBatch b, ClickableComponent button, string? label = null, Color? tint = null)
    {
        DrawInsetBox(b, button.bounds, tint ?? Color.White);
        DrawTextFit(b, label ?? button.label, button.bounds, Game1.textColor, 10);
    }

    public static void DrawFittedLine(SpriteBatch b, string text, Rectangle bounds, Color color, int horizontalPadding = 4)
    {
        DrawTextFit(b, text, bounds, color, horizontalPadding);
    }

    public static void DrawFittedTitle(SpriteBatch b, string text, Rectangle bounds, Color color)
    {
        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        const float minScale = 0.62f;
        var value = text.Trim();
        var size = Game1.dialogueFont.MeasureString(value);
        var scale = Math.Min(1f, Math.Min(bounds.Width / Math.Max(1f, size.X), bounds.Height / Math.Max(1f, size.Y)));
        if (scale < minScale)
        {
            scale = minScale;
            value = Ellipsize(Game1.dialogueFont, value, bounds.Width / scale);
            size = Game1.dialogueFont.MeasureString(value);
        }

        var y = bounds.Y + Math.Max(0f, (bounds.Height - size.Y * scale) / 2f);
        Utility.drawTextWithShadow(b, value, Game1.dialogueFont, new Vector2(bounds.X, y), color, scale);
    }

    public static void DrawFittedLines(SpriteBatch b, IEnumerable<string> lines, Rectangle bounds, Color color)
    {
        var values = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (values.Count == 0 || bounds.Height <= 0)
            return;

        var lineHeight = Math.Max(20, Math.Min(28, bounds.Height / values.Count));
        for (var i = 0; i < values.Count && i * lineHeight < bounds.Height; i++)
            DrawFittedLine(b, values[i], new Rectangle(bounds.X, bounds.Y + i * lineHeight, bounds.Width, lineHeight), color);
    }

    public static void DrawSeparator(SpriteBatch b, Rectangle bounds, Color? color = null)
    {
        b.Draw(Game1.staminaRect, bounds, color ?? (Color.SaddleBrown * 0.4f));
    }

    public static void DrawTooltipBox(SpriteBatch b, int x, int y, string header, IReadOnlyList<string> lines)
    {
        var maxContentWidth = Math.Min(560, Math.Max(64, Game1.uiViewport.Width - 48));
        var displayLines = NormalizeTooltipLines(lines, maxContentWidth);
        var width = (int)Game1.smallFont.MeasureString(header).X;
        foreach (var line in displayLines)
            width = Math.Max(width, (int)Game1.smallFont.MeasureString(line).X);

        width = Math.Min(Game1.uiViewport.Width - 16, Math.Max(180, width + 32));
        var height = Math.Min(Math.Max(64, Game1.uiViewport.Height - 16), 42 + displayLines.Count * 24);

        if (x + width > Game1.uiViewport.Width)
            x = Game1.uiViewport.Width - width - 8;
        if (y + height > Game1.uiViewport.Height)
            y = Game1.uiViewport.Height - height - 8;
        x = Math.Max(8, x);
        y = Math.Max(8, y);

        DrawPanel(b, new Rectangle(x, y, width, height));
        DrawFittedLine(
            b,
            header,
            new Rectangle(x + 16, y + 8, Math.Max(1, width - 32), 28),
            Game1.textColor,
            horizontalPadding: 0);
        for (var i = 0; i < displayLines.Count; i++)
        {
            var lineY = y + 38 + i * 24;
            if (lineY + 20 > y + height - 8)
                break;

            b.DrawString(Game1.smallFont, displayLines[i], new Vector2(x + 16, lineY), Game1.textColor);
        }
    }

    private static List<string> NormalizeTooltipLines(IReadOnlyList<string> lines, int maxWidth)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            var normalized = (line ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var segment in normalized.Split('\n'))
                AddWrappedTooltipSegment(segment, maxWidth, result);
        }

        return result;
    }

    private static void AddWrappedTooltipSegment(string segment, int maxWidth, List<string> result)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            result.Add(string.Empty);
            return;
        }

        var words = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1)
        {
            AddWrappedTooltipToken(segment, maxWidth, result);
            return;
        }

        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Game1.smallFont.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
                result.Add(current);

            if (Game1.smallFont.MeasureString(word).X > maxWidth)
            {
                AddWrappedTooltipToken(word, maxWidth, result);
                current = string.Empty;
            }
            else
            {
                current = word;
            }
        }

        if (current.Length > 0)
            result.Add(current);
    }

    private static void AddWrappedTooltipToken(string text, int maxWidth, List<string> result)
    {
        if (Game1.smallFont.MeasureString(text).X <= maxWidth)
        {
            result.Add(text);
            return;
        }

        var current = string.Empty;
        foreach (var ch in text)
        {
            var candidate = current + ch;
            if (current.Length == 0 || Game1.smallFont.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            result.Add(current);
            current = ch.ToString();
        }

        if (current.Length > 0)
            result.Add(current);
    }

    public static void ResetSlotGeometryDiagnostics()
    {
        SlotGeometryViolations.Clear();
        qualityOverlayDrawCount = 0;
    }

    public static IReadOnlyList<string> GetSlotGeometryViolations()
    {
        return SlotGeometryViolations.ToArray();
    }

    public static int GetQualityOverlayDrawCount()
    {
        return qualityOverlayDrawCount;
    }

    public static void DrawItemInSlot(
        SpriteBatch b,
        Item? item,
        Rectangle cell,
        long count,
        float maximumScale = 1f,
        Color? tint = null,
        float layerDepth = 0.86f)
    {
        if (item is null || cell.Width <= 0 || cell.Height <= 0 || count <= 0)
            return;

        const int inset = 4;
        var innerWidth = Math.Max(1, cell.Width - inset * 2);
        var innerHeight = Math.Max(1, cell.Height - inset * 2);
        var scale = Math.Min(maximumScale, Math.Min(innerWidth / 64f, innerHeight / 64f));
        scale = Math.Max(0.1f, scale);
        var iconSize = 64f * scale;
        var iconCenter = new Vector2(cell.X + cell.Width / 2f, cell.Y + cell.Height / 2f);
        var iconPosition = iconCenter - new Vector2(iconSize / 2f);
        // drawInMenu treats location as the top-left of an unscaled 64px menu slot.
        var menuSlotPosition = iconCenter - new Vector2(32f);
        var iconBounds = new Rectangle(
            (int)Math.Floor(iconPosition.X),
            (int)Math.Floor(iconPosition.Y),
            (int)Math.Ceiling(iconSize),
            (int)Math.Ceiling(iconSize));
        RecordSlotGeometry(cell, iconBounds, "item");

        var originalStack = item.Stack;
        try
        {
            item.drawInMenu(
                b,
                menuSlotPosition,
                scale,
                1f,
                layerDepth,
                StackDrawType.Hide,
                tint ?? Color.White,
                true);
        }
        finally
        {
            item.Stack = originalStack;
        }

        DrawItemQuality(b, item, cell, scale, tint ?? Color.White);
        DrawItemCount(b, cell, count);
    }

    private static void DrawItemQuality(SpriteBatch b, Item item, Rectangle cell, float itemScale, Color tint)
    {
        if (item.Quality <= 0)
            return;

        var quality = Math.Clamp(item.Quality, 1, 4);
        var pulse = quality == 4
            ? (float)((Math.Cos(Game1.currentGameTime.TotalGameTime.TotalMilliseconds * Math.PI / 512d) + 1d) * 0.05d)
            : 0f;
        var starScale = 3f * itemScale * (1f + pulse);
        var starSize = 8f * starScale;
        const int inset = 3;
        var center = new Vector2(
            cell.Left + inset + starSize / 2f,
            cell.Bottom - inset - starSize / 2f);
        var starBounds = new Rectangle(
            (int)Math.Floor(center.X - starSize / 2f),
            (int)Math.Floor(center.Y - starSize / 2f),
            (int)Math.Ceiling(starSize),
            (int)Math.Ceiling(starSize));
        RecordSlotGeometry(cell, starBounds, "quality");
        qualityOverlayDrawCount++;

        b.Draw(
            Game1.mouseCursors,
            center,
            new Rectangle(338 + (quality - 1) * 8, 400, 8, 8),
            tint,
            0f,
            new Vector2(4f),
            starScale,
            SpriteEffects.None,
            0.98f);
    }

    public static void DrawItemCount(SpriteBatch b, Rectangle cell, long count)
    {
        if (count <= 1)
            return;

        if (count < 1000)
        {
            var value = (int)count;
            var digitScale = cell.Width >= 60 && cell.Height >= 60 ? 3f : 2f;
            var width = Utility.getWidthOfTinyDigitString(value, digitScale);
            while (digitScale > 1f && width > cell.Width - 8)
            {
                digitScale -= 0.25f;
                width = Utility.getWidthOfTinyDigitString(value, digitScale);
            }

            var height = (int)Math.Ceiling(8f * digitScale);
            var countBounds = new Rectangle(cell.Right - width - 4, cell.Bottom - height - 5, width, height);
            RecordSlotGeometry(cell, countBounds, "count");
            Utility.drawTinyDigits(value, b, new Vector2(countBounds.X, countBounds.Y), digitScale, 0.99f, Color.White);
            return;
        }

        var text = FormatCount(count);
        var preferredScale = cell.Width >= 60 ? 0.68f : 0.54f;
        var naturalSize = Game1.smallFont.MeasureString(text);
        var scale = Math.Min(preferredScale, Math.Min((cell.Width - 8) / naturalSize.X, (cell.Height - 8) / naturalSize.Y));
        scale = Math.Max(0.28f, scale);
        var size = Game1.smallFont.MeasureString(text) * scale;
        var position = new Vector2(cell.Right - size.X - 4, cell.Bottom - size.Y - 4);
        RecordSlotGeometry(
            cell,
            new Rectangle((int)Math.Floor(position.X), (int)Math.Floor(position.Y), (int)Math.Ceiling(size.X), (int)Math.Ceiling(size.Y)),
            "compact count");
        Utility.drawTextWithShadow(
            b,
            text,
            Game1.smallFont,
            position,
            Color.White,
            scale);
    }

    private static void RecordSlotGeometry(Rectangle cell, Rectangle content, string contentKind)
    {
        if (content.Left >= cell.Left
            && content.Top >= cell.Top
            && content.Right <= cell.Right
            && content.Bottom <= cell.Bottom)
        {
            return;
        }

        SlotGeometryViolations.Add($"{contentKind} {content} exceeds slot {cell}");
    }

    public static void DrawSlotBackground(SpriteBatch b, Rectangle cell, bool disabled = false)
    {
        b.Draw(Game1.staminaRect, cell, Color.Black * 0.52f);
        var inner = new Rectangle(cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4);
        b.Draw(Game1.staminaRect, inner, disabled ? Color.Black * 0.18f : Color.White * 0.30f);
        b.Draw(Game1.staminaRect, new Rectangle(inner.X, inner.Y, inner.Width, 2), Color.White * 0.28f);
        b.Draw(Game1.staminaRect, new Rectangle(inner.X, inner.Y, 2, inner.Height), Color.White * 0.18f);
    }

    public static void DrawGhostUpgradeSlot(SpriteBatch b, Rectangle slot, string ghostType)
    {
        DrawSlotBackground(b, slot, true);
        var center = new Vector2(slot.Center.X, slot.Center.Y);
        // Draw very faint placeholder indicators
        if (ghostType == "speed")
        {
            // Faint lightning bolt
            b.Draw(Game1.staminaRect, new Rectangle(slot.X + slot.Width / 2 - 2, slot.Y + 8, 4, 16), Color.Yellow * 0.15f);
            b.Draw(Game1.staminaRect, new Rectangle(slot.X + slot.Width / 2 - 6, slot.Y + 14, 12, 4), Color.Yellow * 0.15f);
        }
        else if (ghostType == "capacity")
        {
            // Faint up arrow
            b.Draw(Game1.staminaRect, new Rectangle(slot.X + slot.Width / 2 - 2, slot.Y + 10, 4, 14), Color.DeepSkyBlue * 0.15f);
            b.Draw(Game1.staminaRect, new Rectangle(slot.X + slot.Width / 2 - 6, slot.Y + 10, 12, 4), Color.DeepSkyBlue * 0.15f);
        }
        else if (ghostType == "cell")
        {
            // Faint memory chip/card outlines
            b.Draw(Game1.staminaRect, new Rectangle(slot.X + 8, slot.Y + 8, slot.Width - 16, slot.Height - 16), Color.LimeGreen * 0.12f);
        }
        else if (ghostType == "module")
        {
            // Faint module circle
            b.Draw(Game1.staminaRect, new Rectangle(slot.X + 12, slot.Y + 12, slot.Width - 24, slot.Height - 24), Color.Orchid * 0.15f);
        }
    }

    public static string FormatCount(long n)
    {
        if (n < 1000)
            return n.ToString();
        if (n < 1_000_000)
        {
            var thousands = Math.Round(n / 1000d, 1, MidpointRounding.AwayFromZero);
            if (thousands >= 1000d)
                return "1M";

            return thousands.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K";
        }

        return (n / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
    }

    private static string NormalizeSearchText(string? value)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ');

        return normalized.Length <= 40 ? normalized : normalized[..40];
    }

    public static Item? CreateIconItem(string qualifiedItemId, string serializedPrototype = "", int stack = 1)
    {
        try
        {
            var item = !string.IsNullOrWhiteSpace(serializedPrototype)
                ? SerializedItemCodec.CreateItem(serializedPrototype, Math.Max(1, stack))
                : ItemRegistry.Create(qualifiedItemId);
            item.Stack = Math.Max(1, stack);
            return item;
        }
        catch
        {
            return null;
        }
    }

    public static Item? CreateIconItem(NetworkItemRequest? request, int stack = 1)
    {
        if (request is null)
            return null;

        if (!string.IsNullOrWhiteSpace(request.SerializedItemPrototype))
            return CreateIconItem(request.QualifiedItemId ?? string.Empty, request.SerializedItemPrototype, stack);

        return string.IsNullOrWhiteSpace(request.QualifiedItemId)
            ? null
            : CreateIconItem(request.QualifiedItemId, stack: stack);
    }

    private static void DrawTextFit(SpriteBatch b, string text, Rectangle bounds, Color color, int horizontalPad)
    {
        if (string.IsNullOrEmpty(text))
            return;

        const float minScale = 0.62f;
        var value = text;
        var size = Game1.smallFont.MeasureString(value);
        var maxWidth = Math.Max(1, bounds.Width - horizontalPad * 2);
        var maxHeight = Math.Max(1, bounds.Height - 4);
        var scale = Math.Min(1f, Math.Min(maxWidth / Math.Max(1f, size.X), maxHeight / Math.Max(1f, size.Y)));
        if (scale < minScale)
        {
            scale = minScale;
            value = Ellipsize(Game1.smallFont, value, maxWidth / scale);
            size = Game1.smallFont.MeasureString(value);
        }

        var x = bounds.X + (bounds.Width - size.X * scale) / 2f;
        var y = bounds.Y + (bounds.Height - size.Y * scale) / 2f;
        Utility.drawTextWithShadow(b, value, Game1.smallFont, new Vector2(x, y), color, scale);
    }

    private static string Ellipsize(SpriteFont font, string text, float maxWidth)
    {
        const string suffix = "...";
        if (font.MeasureString(text).X <= maxWidth)
            return text;

        if (font.MeasureString(suffix).X >= maxWidth)
            return suffix;

        var value = text;
        while (value.Length > 1 && font.MeasureString(value + suffix).X > maxWidth)
            value = value[..^1];

        return value + suffix;
    }

    private static string EllipsizeFromStart(SpriteFont font, string text, float maxWidth)
    {
        const string prefix = "...";
        if (font.MeasureString(text).X <= maxWidth)
            return text;

        if (font.MeasureString(prefix).X >= maxWidth)
            return prefix;

        var value = text;
        while (value.Length > 1 && font.MeasureString(prefix + value).X > maxWidth)
            value = value[1..];

        return prefix + value;
    }
}

internal interface ISearchTextInputOwner
{
    void SuspendSearchInput();
    void ResumeSearchInput();
}

internal sealed class SVSAPItemIconCache
{
    private const int MaxEntries = 512;
    private readonly Dictionary<string, Item?> entries = new(StringComparer.Ordinal);

    public Item? GetOrCreate(string key, Func<Item?> factory)
    {
        if (this.entries.TryGetValue(key, out var cached))
            return cached;

        if (this.entries.Count >= MaxEntries)
            this.entries.Clear();

        var created = factory();
        this.entries[key] = created;
        return created;
    }

    public void Clear() => this.entries.Clear();
}

internal sealed class SVSAPIconGrid<T> where T : class
{
    private int cellSize;

    public SVSAPIconGrid(int cellSize = SVSAPMenuWidgets.Cell)
    {
        this.cellSize = Math.Max(40, cellSize);
    }

    public Rectangle Bounds { get; private set; }
    public int Columns { get; private set; } = 1;
    public int Rows { get; private set; } = 1;
    public int ScrollOffset { get; set; }

    public void SetCellSize(int value)
    {
        this.cellSize = Math.Max(40, value);
    }

    public void SetBounds(Rectangle bounds)
    {
        this.Bounds = bounds;
        this.Columns = Math.Max(1, bounds.Width / this.cellSize);
        this.Rows = Math.Max(1, bounds.Height / this.cellSize);
    }

    public void ResetScroll() => this.ScrollOffset = 0;

    public void ClampScroll(int visibleCount)
    {
        var totalRows = (int)Math.Ceiling(visibleCount / (double)Math.Max(1, this.Columns));
        var maxOffset = Math.Max(0, totalRows - this.Rows);
        this.ScrollOffset = Math.Clamp(this.ScrollOffset, 0, maxOffset);
    }

    public T? HitTest(int x, int y, IReadOnlyList<T> entries)
    {
        if (!this.Bounds.Contains(x, y))
            return null;

        var c = (x - this.Bounds.X) / this.cellSize;
        var r = (y - this.Bounds.Y) / this.cellSize;
        if (c < 0 || c >= this.Columns || r < 0 || r >= this.Rows)
            return null;

        var idx = (this.ScrollOffset + r) * this.Columns + c;
        return idx >= 0 && idx < entries.Count ? entries[idx] : null;
    }

    public void Draw(
        SpriteBatch b,
        IReadOnlyList<T> entries,
        Func<T, Item?> getItem,
        Func<T, long> getCount,
        Func<T, bool>? isDisabled = null,
        Func<T, string?>? getBadge = null)
    {
        var startIndex = this.ScrollOffset * this.Columns;
        for (var r = 0; r < this.Rows; r++)
        {
            for (var c = 0; c < this.Columns; c++)
            {
                var idx = startIndex + r * this.Columns + c;
                if (idx >= entries.Count)
                    break;

                var cell = new Rectangle(
                    this.Bounds.X + c * this.cellSize,
                    this.Bounds.Y + r * this.cellSize,
                    this.cellSize - 4,
                    this.cellSize - 4);
                var entry = entries[idx];
                var disabled = isDisabled?.Invoke(entry) == true;
                SVSAPMenuWidgets.DrawSlotBackground(b, cell, disabled);
                var item = getItem(entry);
                var count = getCount(entry);
                if (item is not null)
                    SVSAPMenuWidgets.DrawItemInSlot(b, item, cell, count, tint: disabled ? Color.White * 0.45f : Color.White);

                var badge = getBadge?.Invoke(entry);
                if (!string.IsNullOrWhiteSpace(badge))
                {
                    var badgeBounds = new Rectangle(cell.X + 4, cell.Y + 4, 24, 20);
                    b.Draw(Game1.staminaRect, new Rectangle(badgeBounds.X - 1, badgeBounds.Y - 1, badgeBounds.Width + 2, badgeBounds.Height + 2), Color.Black * 0.9f);
                    b.Draw(Game1.staminaRect, badgeBounds, disabled ? Color.Firebrick * 0.92f : Color.DarkSlateGray * 0.92f);
                    b.DrawString(Game1.smallFont, badge, new Vector2(badgeBounds.X + 6, badgeBounds.Y + 1), Color.White, 0f, Vector2.Zero, 0.68f, SpriteEffects.None, 1f);
                }

                if (isDisabled is not null)
                    SVSAPMenuWidgets.DrawSlotStatusLine(b, cell, disabled ? PixelStatus.Error : PixelStatus.Ready);

            }
        }
    }
}

internal sealed class SVSAPBackpackGrid
{
    private int cellSize;

    public SVSAPBackpackGrid(int cellSize = SVSAPMenuWidgets.Cell)
    {
        this.cellSize = Math.Max(40, cellSize);
    }

    public Rectangle Bounds { get; private set; }
    public int Columns { get; private set; } = SVSAPMenuWidgets.BackpackColumns;
    public int Rows { get; private set; } = 1;

    public void SetCellSize(int value)
    {
        this.cellSize = Math.Max(40, value);
    }

    public void SetBounds(Rectangle bounds)
    {
        this.Bounds = bounds;
        this.Columns = GetColumnCount(bounds.Width, this.cellSize);
        this.Rows = Math.Max(1, (int)Math.Ceiling(Game1.player.Items.Count / (double)this.Columns));
    }

    public static int GetColumnCount(int availableWidth, int cellSize = SVSAPMenuWidgets.Cell)
    {
        return Math.Clamp(Math.Max(1, availableWidth / Math.Max(40, cellSize)), 1, SVSAPMenuWidgets.BackpackColumns);
    }

    public static int GetHeight(int columns, int cellSize = SVSAPMenuWidgets.Cell)
    {
        return Math.Max(1, (int)Math.Ceiling(Game1.player.Items.Count / (double)Math.Max(1, columns)))
            * Math.Max(40, cellSize);
    }

    public int HitTest(int x, int y)
    {
        if (!this.Bounds.Contains(x, y))
            return -1;

        var c = (x - this.Bounds.X) / this.cellSize;
        var r = (y - this.Bounds.Y) / this.cellSize;
        if (c < 0 || c >= this.Columns || r < 0 || r >= this.Rows)
            return -1;

        var idx = r * this.Columns + c;
        return idx < Game1.player.Items.Count ? idx : -1;
    }

    public void Draw(SpriteBatch b)
    {
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var r = i / this.Columns;
            var c = i % this.Columns;
            var cell = new Rectangle(
                this.Bounds.X + c * this.cellSize,
                this.Bounds.Y + r * this.cellSize,
                this.cellSize - 4,
                this.cellSize - 4);
            SVSAPMenuWidgets.DrawSlotBackground(b, cell);

            var item = Game1.player.Items[i];
            var maximumScale = this.cellSize <= 52 ? 0.68f : 1f;
            SVSAPMenuWidgets.DrawItemInSlot(b, item, cell, item?.Stack ?? 0, maximumScale);
        }
    }
}
