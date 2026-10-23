using System.Globalization;
using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// One way to show a game value as text and to read a typed number back, for the Editor page
/// and the mod editor (fields, stat rows, scale rules). Each had its own copy, and they had
/// started to differ.
/// </summary>
public static class EditorValueText
{
    /// <summary>A value as typed in a box: the text players see for game text (not NSLOCTEXT), a plain number, else the JSON.</summary>
    public static string Display(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => NsLocText.TextOf(s) ?? s,
        _ when ScaleRules.TryReadNumber(value, out var n) => FormatNumber(n),
        _ => value.ToJsonString(),
    };

    /// <summary>Like Display, but shorter for the "game: …" line: a link shows the row it points at, a stats list how many stats it has.</summary>
    public static string Plain(JsonNode? value) => value switch
    {
        JsonObject link when link.Count <= 2 && link["RowName"] is JsonValue row && (link.Count == 1 || link.ContainsKey("DataTableName")) => row.ToString(),
        JsonObject o when o.Count > 0 && o.All(kv => GameStats.StatNameOf(kv.Key) != kv.Key) => $"{o.Count} stat(s)",
        _ => Display(value),
    };

    /// <summary>"0.5", "100": up to six decimals, a dot whatever the PC's language.</summary>
    public static string FormatNumber(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A typed number (a dot for decimals); false for text, empty, infinity or NaN.</summary>
    public static bool TryParseNumber(string? text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>A typed whole number the game can hold in an int (a stat, a stack size); false for 5.5.</summary>
    public static bool TryParseWholeNumber(string? text, out long value)
    {
        value = 0;
        if (!TryParseNumber(text, out var number) || number != Math.Floor(number) || number is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        value = (long)number;
        return true;
    }
}
