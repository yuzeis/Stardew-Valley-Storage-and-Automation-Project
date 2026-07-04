using SVSAP.Models;
using StardewValley;

namespace SVSAP.Services;

internal static class PatternDisplayNames
{
    private const string ItemArgumentPrefix = "item:";
    private const string TextArgumentPrefix = "text:";

    public static string ItemArg(string qualifiedItemId)
    {
        return ItemArgumentPrefix + qualifiedItemId;
    }

    public static string TextArg(string text)
    {
        return TextArgumentPrefix + text;
    }

    public static string Format(string key, params string[] arguments)
    {
        return ModText.Format(key, ResolveArguments(arguments));
    }

    public static PatternData Apply(PatternData pattern, string key, params string[] arguments)
    {
        pattern.DisplayNameKey = key;
        pattern.DisplayNameArguments = arguments.ToList();
        pattern.DisplayName = Format(key, arguments);
        return pattern;
    }

    public static string Get(PatternData pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern.DisplayNameKey))
            return pattern.DisplayName;

        try
        {
            return ModText.Format(pattern.DisplayNameKey, ResolveArguments(pattern.DisplayNameArguments));
        }
        catch
        {
            return string.IsNullOrWhiteSpace(pattern.DisplayName) ? pattern.DisplayNameKey : pattern.DisplayName;
        }
    }

    private static object[] ResolveArguments(IEnumerable<string> arguments)
    {
        return arguments.Select(ResolveArgument).ToArray();
    }

    private static object ResolveArgument(string value)
    {
        if (value.StartsWith(ItemArgumentPrefix, StringComparison.Ordinal))
        {
            var qualifiedItemId = value[ItemArgumentPrefix.Length..];
            try
            {
                return ItemRegistry.Create(qualifiedItemId).DisplayName;
            }
            catch
            {
                return qualifiedItemId;
            }
        }

        return value.StartsWith(TextArgumentPrefix, StringComparison.Ordinal)
            ? value[TextArgumentPrefix.Length..]
            : value;
    }
}
