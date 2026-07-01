using System.Globalization;
using System.Text.Json;
using StardewModdingAPI;

namespace SVSAP;

internal static class ModText
{
    public const string Chinese = "zh";
    public const string English = "en";

    private static readonly Dictionary<string, string> active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> fallback = new(StringComparer.OrdinalIgnoreCase);

    public static string CurrentLanguage { get; private set; } = Chinese;

    public static void Load(IModHelper helper, string? language, IMonitor monitor)
    {
        var normalized = NormalizeLanguage(language);
        CurrentLanguage = normalized;

        fallback.Clear();
        foreach (var pair in ReadLanguageFile(helper, Chinese, monitor))
            fallback[pair.Key] = pair.Value;

        active.Clear();
        foreach (var pair in normalized == Chinese ? fallback : ReadLanguageFile(helper, normalized, monitor))
            active[pair.Key] = pair.Value;
    }

    public static string NormalizeLanguage(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.StartsWith("en", StringComparison.Ordinal) ? English : Chinese;
    }

    public static string FormatLanguage(string value)
    {
        return NormalizeLanguage(value) == English ? "English" : "中文";
    }

    public static string Get(string key)
    {
        if (active.TryGetValue(key, out var activeValue))
            return activeValue;

        if (fallback.TryGetValue(key, out var fallbackValue))
            return fallbackValue;

        return key;
    }

    public static string Get(string key, string fallbackText)
    {
        if (active.TryGetValue(key, out var activeValue))
            return activeValue;

        if (fallback.TryGetValue(key, out var fallbackValue))
            return fallbackValue;

        return fallbackText;
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }

    private static Dictionary<string, string> ReadLanguageFile(IModHelper helper, string language, IMonitor monitor)
    {
        var path = Path.Combine(helper.DirectoryPath, "Lang", language + ".json");
        try
        {
            if (!File.Exists(path))
            {
                monitor.Log($"SVSAP language file not found: {path}", LogLevel.Warn);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return data is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            monitor.Log($"Could not read SVSAP language file '{path}': {ex.Message}", LogLevel.Warn);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
