using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// The game's stat vocabulary, shared by the Editor page and the mod editor: stats live in
/// objects keyed (Value="BaseOxygenSlots_+"), each a whole number. Names in plain words,
/// what kind of number each is, and the game's list of stats an item can grant.
/// </summary>
public static partial class GameStats
{
    [GeneratedRegex("""^\(Value="(?<stat>.+)"\)$""")]
    private static partial Regex StatKey();

    /// <summary>
    /// "BaseOxygenSlots_+" → "Oxygen slots"; "BaseMovementSpeed_+%" → "Movement speed +%"; "BaseColdResistance_%" → "Cold resistance %".
    /// The game has both a flat and a percent version of many stats (BaseMovementSpeed_+ and _+%),
    /// so the suffix keeps them apart.
    /// </summary>
    public static string StatLabel(string stat)
    {
        var name = StatKey().Match(stat) is { Success: true } m ? m.Groups["stat"].Value : stat;
        var suffix = name.EndsWith("_+%", StringComparison.Ordinal) ? " +%" : name.EndsWith("_%", StringComparison.Ordinal) ? " %" : "";
        name = name.TrimEnd('+', '%', '_');
        if (name.StartsWith("Base", StringComparison.Ordinal) && name.Length > 4 && char.IsUpper(name[4]))
        {
            name = name[4..];
        }

        return GameItemEditor.Humanize(name) + suffix;
    }

    /// <summary>A stats object: every key is a stat ("(Value=\"BaseOxygenSlots_+\")": 1). An empty object counts when its name says so ("ArmourStats").</summary>
    public static bool IsStatsObject(string field, JsonNode? value) =>
        value is JsonObject obj && (obj.Count == 0 ? field.EndsWith("Stats", StringComparison.OrdinalIgnoreCase) : obj.All(kv => StatKey().IsMatch(kv.Key)));

    /// <summary>The stat name inside a stats key: "(Value=\"BaseOxygenSlots_+\")" → "BaseOxygenSlots_+".</summary>
    public static string StatNameOf(string key) => StatKey().Match(key) is { Success: true } m ? m.Groups["stat"].Value : key;

    /// <summary>
    /// Plain description of one stat for a row, from its label, plus the line players read in the
    /// game when it's known ("In game: +N kg Weight Capacity", D_Stats' PositiveDescription with
    /// {0} as N). LearnGameText reads those lines once from the game data.
    /// </summary>
    public static string StatHint(string statName) =>
        s_gameText.TryGetValue(statName, out var inGame) ? $"{StatDescription(StatLabel(statName))} In game: \"{inGame}\"." : StatDescription(StatLabel(statName));

    private static System.Collections.Concurrent.ConcurrentDictionary<string, string> s_gameText = new(StringComparer.Ordinal);
    private static string? s_gameTextFrom;

    /// <summary>
    /// Reads every stat's in-game line from D_Stats (PositiveDescription, else Title) for StatHint;
    /// once per data folder per run (the wording seldom changes; the next start reads it again).
    /// Quiet when the table isn't there: the hints keep their plain words.
    /// </summary>
    public static void LearnGameText(GameTableIndex game)
    {
        if (game.CurrentFileFor("D_Stats") is not { } statsFile || string.Equals(s_gameTextFrom, game.DataFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var text = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, node) in game.Rows(statsFile) ?? [])
        {
            if (node is JsonObject row
                && (NsLocText.TextOf(row["PositiveDescription"]?.GetValue<string>()) ?? NsLocText.TextOf(row["Title"]?.GetValue<string>())) is { Length: > 0 } line
                && line != "None")
            {
                text[name] = line.Replace("{0}", "N", StringComparison.Ordinal).Trim();
            }
        }

        s_gameText = text;
        s_gameTextFrom = game.DataFolder;
    }

    /// <summary>What kind of number a stat is, in words, for the stat picker.</summary>
    public static string StatKind(string statName) =>
        statName.EndsWith("_+%", StringComparison.Ordinal) ? "percent bonus (10 = +10%)"
        : statName.EndsWith("_%", StringComparison.Ordinal) ? "percent (10 = 10%)"
        : "flat amount (10 = +10)";

    /// <summary>The stats an item can grant (the game's "Base…" stats), for the stat picker.</summary>
    public static IReadOnlyList<StatChoice> StatChoices(GameTableIndex game) =>
        game.CurrentFileFor("D_Stats") is { } stats
            ? [.. (game.Rows(stats) ?? []).Select(kv => kv.Key)
                .Where(n => n.StartsWith("Base", StringComparison.Ordinal))
                .Select(n => new StatChoice(n, StatLabel(n), StatKind(n)))
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)]
            : [];

    /// <summary>Whether a key is a stat key ("(Value=\"BaseOxygenSlots_+\")").</summary>
    public static bool IsStatKey(string key) => StatKey().IsMatch(key);

    /// <summary>
    /// Stat values are whole numbers in the game (an int per stat: all 15,333 stat values in the
    /// game's tables are), so 5.5 would lose its fraction or not load. Returns the first stat
    /// anywhere under node with a fractional value ("Cold resistance: 5.5"), or null.
    /// </summary>
    public static string? FractionalStat(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (IsStatKey(key) && value is JsonValue v && ScaleRules.TryReadNumber(v, out var n) && n != Math.Floor(n))
                    {
                        return $"{StatLabel(key)}: {n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    }

                    if (FractionalStat(value) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            case JsonArray array:
                return array.Select(FractionalStat).FirstOrDefault(found => found is not null);
            default:
                return null;
        }
    }

    /// <summary>The key a stat has in a stats object: "BaseColdResistance_%" → "(Value=\"BaseColdResistance_%\")".</summary>
    public static string StatKeyFor(string statName) => StatKey().IsMatch(statName) ? statName : $"(Value=\"{statName}\")";

    /// <summary>What a stat does, in words, from its label ("Cold resistance %").</summary>
    internal static string StatDescription(string label) =>
        label.EndsWith(" +%", StringComparison.Ordinal)
            ? $"Percent bonus (10 = +10%). Higher = more {label[..^3].ToLowerInvariant()}, lower = less; 0 = no bonus."
            : label.EndsWith(" %", StringComparison.Ordinal)
                ? $"Percent (10 = 10%). Higher = more {label[..^2].ToLowerInvariant()}, lower = less; 0 = none."
                : $"Flat amount. Higher = more {label.ToLowerInvariant()}, lower = less; 0 = no bonus.";
}
