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
}
