using System.Text.Json;
using System.Text.Json.Nodes;

namespace IcarusStarlink.App.ViewModels;

/// <summary>How a diff row differs: on both sides, only the mod's, only the game's, or the same line with another value.</summary>
public enum DiffSide
{
    Same,
    OnlyMod,
    OnlyGame,
    Changed,
}

/// <param name="After">The row as it will be after the chosen fix (null: not there).</param>
public sealed record DiffRow(string? Mod, string? Game, string? After, DiffSide Side)
{
    public bool IsOnlyMod => Side == DiffSide.OnlyMod;

    public bool IsOnlyGame => Side == DiffSide.OnlyGame;

    public bool IsChanged => Side == DiffSide.Changed;

    public string ModMark => Mod is null ? "" : Side is DiffSide.OnlyMod or DiffSide.Changed ? "+" : "";

    public string GameMark => Game is null ? "" : Side is DiffSide.OnlyGame or DiffSide.Changed ? "+" : "";
}

/// <summary>
/// The fix window's side-by-side view, kdiff-style: a list (tags, linked rows, recipe items) as one
/// aligned row per entry by name, so the eye only goes to the coloured ones; any other value as its
/// lines aligned on what both sides share, the differing lines coloured.
/// </summary>
public static class ValueDiff
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>True when both sides are lists of named entries (shown entry by entry).</summary>
    public static bool IsList(JsonNode? mod, JsonNode? game) => Entries(mod) is not null && Entries(game) is not null;

    public static IReadOnlyList<DiffRow> Build(JsonNode? mod, JsonNode? game, JsonNode? after)
    {
        if (Entries(mod) is { } modEntries && Entries(game) is { } gameEntries)
        {
            return ListRows(modEntries, gameEntries, Entries(after) ?? (after is null ? [] : gameEntries));
        }

        var afterIsMod = JsonNode.DeepEquals(after, mod) && !JsonNode.DeepEquals(after, game);
        return LineRows(Lines(mod), Lines(game), afterIsMod);
    }

    /// <summary>A list as entry names: tags, linked rows ("Crafting_Bench"), recipe items ("Wood ×3"), plain values.</summary>
    public static List<string>? Entries(JsonNode? value) => value switch
    {
        JsonObject o when o["GameplayTags"] is JsonArray tags => [.. tags.Select(t => t?["TagName"]?.ToString() ?? "")],
        JsonArray a when a.All(e => e is JsonValue) => [.. a.Select(e => e!.ToString())],
        JsonArray a when a.All(e => e is JsonObject o && (o["RowName"] ?? o["TagName"]) is JsonValue) => [.. a.Select(e => (e!["RowName"] ?? e["TagName"])!.ToString())],
        JsonArray a when a.All(e => e is JsonObject o && o["Element"]?["RowName"] is JsonValue) =>
            [.. a.Select(e => e!["Element"]!["RowName"]!.ToString() + (e["Count"] is JsonValue count ? $" ×{count}" : ""))],
        _ => null,
    };

    private static List<DiffRow> ListRows(List<string> mod, List<string> game, List<string> after)
    {
        var rows = new List<DiffRow>();
        var modLeft = mod.ToList();
        var afterLeft = after.ToList();

        bool Take(List<string> from, string entry)
        {
            var i = from.FindIndex(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                return false;
            }

            from.RemoveAt(i);
            return true;
        }

        // The game's order first (what the game has now), then what only the mod has.
        foreach (var entry in game)
        {
            var inMod = Take(modLeft, entry);
            rows.Add(new DiffRow(inMod ? entry : null, entry, Take(afterLeft, entry) ? entry : null, inMod ? DiffSide.Same : DiffSide.OnlyGame));
        }

        foreach (var entry in modLeft)
        {
            rows.Add(new DiffRow(entry, null, Take(afterLeft, entry) ? entry : null, DiffSide.OnlyMod));
        }

        return rows;
    }

    private static string[] Lines(JsonNode? value) =>
        value is null ? ["(not set: the mod deletes it)"] : value.ToJsonString(Indented).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    /// <summary>Lines aligned on their longest common run (LCS), the rest paired up as changed or left one-sided.</summary>
    private static List<DiffRow> LineRows(string[] mod, string[] game, bool afterIsMod)
    {
        var lcs = new int[mod.Length + 1, game.Length + 1];
        for (var i = mod.Length - 1; i >= 0; i--)
        {
            for (var j = game.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = mod[i] == game[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var rows = new List<DiffRow>();
        var pendingMod = new List<string>();
        var pendingGame = new List<string>();

        void Flush()
        {
            // A run of differing lines on both sides reads best paired up, line against line.
            for (var k = 0; k < Math.Max(pendingMod.Count, pendingGame.Count); k++)
            {
                var m = k < pendingMod.Count ? pendingMod[k] : null;
                var g = k < pendingGame.Count ? pendingGame[k] : null;
                var side = m is not null && g is not null ? DiffSide.Changed : m is not null ? DiffSide.OnlyMod : DiffSide.OnlyGame;
                rows.Add(new DiffRow(m, g, afterIsMod ? m : g, side));
            }

            pendingMod.Clear();
            pendingGame.Clear();
        }

        int a = 0, b = 0;
        while (a < mod.Length || b < game.Length)
        {
            if (a < mod.Length && b < game.Length && mod[a] == game[b])
            {
                Flush();
                rows.Add(new DiffRow(mod[a], game[b], mod[a], DiffSide.Same));
                a++;
                b++;
            }
            else if (b >= game.Length || (a < mod.Length && lcs[a + 1, b] >= lcs[a, b + 1]))
            {
                pendingMod.Add(mod[a++]);
            }
            else
            {
                pendingGame.Add(game[b++]);
            }
        }

        Flush();
        return rows;
    }
}
