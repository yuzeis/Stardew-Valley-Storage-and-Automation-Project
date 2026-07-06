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
    public const int Pad = 28;
    public const int BackpackColumns = 12;

    private static readonly Rectangle PanelSource = new(0, 256, 60, 60);

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

    public static void DrawSearchBox(SpriteBatch b, Rectangle bounds, string value)
    {
        DrawInsetBox(b, bounds);
        var searchText = string.IsNullOrEmpty(value) ? ModText.Get("ui.search") : value;
        var searchColor = string.IsNullOrEmpty(value) ? Color.Gray : Game1.textColor;
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
        Utility.drawTextWithShadow(b, header, Game1.smallFont, new Vector2(x + 16, y + 12), Game1.textColor);
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

    public static void DrawItemCount(SpriteBatch b, Rectangle cell, long count)
    {
        if (count <= 0)
            return;

        var text = FormatCount(count);
        var scale = text.Length <= 3 ? 0.98f : 0.9f;
        var size = Game1.smallFont.MeasureString(text) * scale;
        var width = Math.Min(cell.Width - 4, (int)Math.Ceiling(size.X + 14));
        var height = (int)Math.Ceiling(size.Y + 7);
        var box = new Rectangle(cell.Right - width - 2, cell.Bottom - height - 2, width, height);
        b.Draw(Game1.staminaRect, new Rectangle(box.X - 1, box.Y - 1, box.Width + 2, box.Height + 2), Color.Black * 0.95f);
        b.Draw(Game1.staminaRect, box, new Color(35, 31, 24) * 0.96f);
        Utility.drawTextWithShadow(
            b,
            text,
            Game1.smallFont,
            new Vector2(box.Right - 7 - size.X, box.Y + 1),
            new Color(255, 245, 168),
            scale);
    }

    public static void DrawSlotBackground(SpriteBatch b, Rectangle cell, bool disabled = false)
    {
        b.Draw(Game1.staminaRect, cell, Color.Black * 0.52f);
        var inner = new Rectangle(cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4);
        b.Draw(Game1.staminaRect, inner, disabled ? Color.Black * 0.18f : Color.White * 0.30f);
        b.Draw(Game1.staminaRect, new Rectangle(inner.X, inner.Y, inner.Width, 2), Color.White * 0.28f);
        b.Draw(Game1.staminaRect, new Rectangle(inner.X, inner.Y, 2, inner.Height), Color.White * 0.18f);
    }

    public static string FormatCount(long n)
    {
        if (n < 1000)
            return n.ToString();
        if (n < 1_000_000)
            return (n / 1000d).ToString("0.#") + "K";
        if (n < 1_000_000_000)
            return (n / 1_000_000d).ToString("0.#") + "M";
        return (n / 1_000_000_000d).ToString("0.#") + "B";
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

        var size = Game1.smallFont.MeasureString(text);
        var maxWidth = Math.Max(1, bounds.Width - horizontalPad * 2);
        var scale = size.X > maxWidth ? Math.Max(0.62f, maxWidth / size.X) : 1f;
        var x = bounds.X + (bounds.Width - size.X * scale) / 2f;
        var y = bounds.Y + (bounds.Height - size.Y * scale) / 2f;
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(x, y), color, scale);
    }
}

internal sealed class SVSAPIconGrid<T> where T : class
{
    public Rectangle Bounds { get; private set; }
    public int Columns { get; private set; } = 1;
    public int Rows { get; private set; } = 1;
    public int ScrollOffset { get; set; }

    public void SetBounds(Rectangle bounds)
    {
        this.Bounds = bounds;
        this.Columns = Math.Max(1, bounds.Width / SVSAPMenuWidgets.Cell);
        this.Rows = Math.Max(1, bounds.Height / SVSAPMenuWidgets.Cell);
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

        var c = (x - this.Bounds.X) / SVSAPMenuWidgets.Cell;
        var r = (y - this.Bounds.Y) / SVSAPMenuWidgets.Cell;
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
                    this.Bounds.X + c * SVSAPMenuWidgets.Cell,
                    this.Bounds.Y + r * SVSAPMenuWidgets.Cell,
                    SVSAPMenuWidgets.Cell - 4,
                    SVSAPMenuWidgets.Cell - 4);
                var entry = entries[idx];
                var disabled = isDisabled?.Invoke(entry) == true;
                SVSAPMenuWidgets.DrawSlotBackground(b, cell, disabled);
                var item = getItem(entry);
                item?.drawInMenu(
                    b,
                    new Vector2(cell.X + SVSAPMenuWidgets.IconInset, cell.Y + SVSAPMenuWidgets.IconInset),
                    1f,
                    1f,
                    0.86f,
                    StackDrawType.Hide,
                    disabled ? Color.White * 0.45f : Color.White,
                    true);

                var badge = getBadge?.Invoke(entry);
                if (!string.IsNullOrWhiteSpace(badge))
                {
                    var badgeBounds = new Rectangle(cell.X + 4, cell.Y + 4, 24, 20);
                    b.Draw(Game1.staminaRect, new Rectangle(badgeBounds.X - 1, badgeBounds.Y - 1, badgeBounds.Width + 2, badgeBounds.Height + 2), Color.Black * 0.9f);
                    b.Draw(Game1.staminaRect, badgeBounds, disabled ? Color.Firebrick * 0.92f : Color.DarkSlateGray * 0.92f);
                    b.DrawString(Game1.smallFont, badge, new Vector2(badgeBounds.X + 6, badgeBounds.Y + 1), Color.White, 0f, Vector2.Zero, 0.68f, SpriteEffects.None, 1f);
                }

                SVSAPMenuWidgets.DrawItemCount(b, cell, getCount(entry));
            }
        }
    }
}

internal sealed class SVSAPBackpackGrid
{
    public Rectangle Bounds { get; private set; }
    public int Columns { get; private set; } = SVSAPMenuWidgets.BackpackColumns;
    public int Rows { get; private set; } = 1;

    public void SetBounds(Rectangle bounds)
    {
        this.Bounds = bounds;
        this.Columns = GetColumnCount(bounds.Width);
        this.Rows = Math.Max(1, (int)Math.Ceiling(Game1.player.Items.Count / (double)this.Columns));
    }

    public static int GetColumnCount(int availableWidth)
    {
        return Math.Clamp(Math.Max(1, availableWidth / SVSAPMenuWidgets.Cell), 1, SVSAPMenuWidgets.BackpackColumns);
    }

    public static int GetHeight(int columns)
    {
        return Math.Max(1, (int)Math.Ceiling(Game1.player.Items.Count / (double)Math.Max(1, columns)))
            * SVSAPMenuWidgets.Cell;
    }

    public int HitTest(int x, int y)
    {
        if (!this.Bounds.Contains(x, y))
            return -1;

        var c = (x - this.Bounds.X) / SVSAPMenuWidgets.Cell;
        var r = (y - this.Bounds.Y) / SVSAPMenuWidgets.Cell;
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
                this.Bounds.X + c * SVSAPMenuWidgets.Cell,
                this.Bounds.Y + r * SVSAPMenuWidgets.Cell,
                SVSAPMenuWidgets.Cell - 4,
                SVSAPMenuWidgets.Cell - 4);
            SVSAPMenuWidgets.DrawSlotBackground(b, cell);

            var item = Game1.player.Items[i];
            item?.drawInMenu(
                b,
                new Vector2(cell.X + SVSAPMenuWidgets.IconInset, cell.Y + SVSAPMenuWidgets.IconInset),
                1f,
                1f,
                0.86f,
                StackDrawType.Draw,
                Color.White,
                true);
        }
    }
}
