using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// The game's localized text: NSLOCTEXT("D_Itemable", "Item_Envirosuit-DisplayName", "Xigo Envirosuit").
/// The key ("&lt;Row&gt;-&lt;Field&gt;") must stay unique per row; the text is what players see, with
/// quotes and backslashes escaped.
/// </summary>
public static partial class NsLocText
{
    [GeneratedRegex("""^NSLOCTEXT\("(?<table>[^"]*)",\s*"(?<key>[^"]*)",\s*"(?<text>(?:[^"\\]|\\.)*)"\)$""")]
    private static partial Regex Pattern();

    public static bool TryParse(string? value, out string table, out string key, out string text)
    {
        table = key = text = "";
        if (value is null || Pattern().Match(value.Trim()) is not { Success: true } m)
        {
            return false;
        }

        (table, key, text) = (m.Groups["table"].Value, m.Groups["key"].Value, Unescape(m.Groups["text"].Value));
        return true;
    }

    public static string Make(string table, string key, string text) => $"NSLOCTEXT(\"{table}\", \"{key}\", \"{Escape(text)}\")";

    /// <summary>The text players see, or null when the value isn't localized text.</summary>
    public static string? TextOf(string? value) => TryParse(value, out _, out _, out var text) ? text : null;

    public static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Unescape(string text) => Regex.Replace(text, @"\\(.)", "$1");
}
