using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Forgiving reads of save-file values. JsonNode.GetValue&lt;T&gt; throws when a game update stores
/// a field in another shape (3.0 or "3" where 3 was, an object where a string was), and on the
/// Saves page one such throw used to fail the whole slot. These return null instead, so the caller
/// falls back to its default: numbers accept 3, 3.0 and "3"; bools accept true, "true" and 1;
/// strings accept a string (or a number/bool, as its text).
/// </summary>
internal static class SaveJson
{
    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        // Through the JSON text, not TryGetValue<long>: a value built in code from an int (not
        // parsed from a file) only converts to its own CLR type, so TryGetValue<long> says no.
        return value.GetValueKind() switch
        {
            JsonValueKind.Number => ParseWhole(value.ToJsonString()),
            JsonValueKind.String => ParseWhole(value.GetValue<string>().Trim()),
            _ => null,
        };
    }

    private static long? ParseWhole(string text)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            && real == Math.Floor(real) && real is >= long.MinValue and <= long.MaxValue
            ? (long)real
            : null;
    }

    public static int? Int(JsonNode? node) =>
        Long(node) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    public static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => Long(value) is { } n ? n != 0 : null,
            JsonValueKind.String => value.GetValue<string>().Trim() is var s && bool.TryParse(s, out var b) ? b
                : s == "1" ? true : s == "0" ? false : null,
            _ => null,
        };
    }

    public static string? String(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null,
        };
    }

    /// <summary>Non-negative ints from an array of numbers (flag ids); anything unreadable is skipped.</summary>
    public static List<int> IdArray(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(Int).Where(i => i is >= 0).Select(i => i!.Value)] : [];

    /// <summary>
    /// The flag ids that are on now, in the save's own order for the ones it already had and
    /// ascending after them for newly-set ones: the minimal-diff write of an UnlockedFlags list.
    /// </summary>
    public static JsonArray MergeIds(JsonNode? original, IEnumerable<int> nowOn)
    {
        var originally = IdArray(original);
        var now = nowOn.ToHashSet();
        var array = new JsonArray();
        foreach (var id in originally.Where(now.Contains))
        {
            array.Add(id);
        }

        foreach (var id in now.Except(originally).OrderBy(i => i))
        {
            array.Add(id);
        }

        return array;
    }
}

/// <summary>
/// A save's Talents list ({RowName, Rank} entries: a character's, or the profile's research and
/// missions). Rank 0 is the format's own "doesn't have it", so it's never written.
/// </summary>
internal static class TalentArray
{
    /// <summary>RowName -> Rank, in the save's order.</summary>
    public static Dictionary<string, int> Read(JsonNode? talents)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (talents is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                if (SaveJson.String(entry["RowName"]) is { } rowName)
                {
                    ranks[rowName] = SaveJson.Int(entry["Rank"]) ?? 0;
                }
            }
        }

        return ranks;
    }

    /// <summary>What the save has first (its order), then every other row offered, at rank 0 — granting one is ranking it up.</summary>
    public static IEnumerable<(string RowName, int Rank)> LearnedThenOthers(IReadOnlyDictionary<string, int> ranks, IEnumerable<string> others)
    {
        foreach (var (rowName, rank) in ranks)
        {
            yield return (rowName, rank);
        }

        foreach (var rowName in others)
        {
            if (!ranks.ContainsKey(rowName))
            {
                yield return (rowName, 0);
            }
        }
    }

    /// <summary>
    /// The minimal-diff write: entries the save already had keep their place (rank updated),
    /// rank-0 ones drop out, newly-learned ones are appended in <paramref name="ranks"/>' order.
    /// </summary>
    public static JsonArray Write(JsonNode? existing, IReadOnlyCollection<(string RowName, int Rank)> ranks)
    {
        var byRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, rank) in ranks)
        {
            byRow.TryAdd(rowName, rank);
        }

        var array = new JsonArray();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing is JsonArray entries)
        {
            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (SaveJson.String(entry["RowName"]) is { } rowName && byRow.GetValueOrDefault(rowName) > 0)
                {
                    array.Add(new JsonObject { ["RowName"] = rowName, ["Rank"] = byRow[rowName] });
                    written.Add(rowName);
                }
            }
        }

        foreach (var (rowName, rank) in ranks)
        {
            if (rank > 0 && written.Add(rowName))
            {
                array.Add(new JsonObject { ["RowName"] = rowName, ["Rank"] = rank });
            }
        }

        return array;
    }
}
