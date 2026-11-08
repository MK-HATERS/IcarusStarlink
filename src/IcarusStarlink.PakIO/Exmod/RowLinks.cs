using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>Reads row links ({"RowName": "X"}) and texts from mod or game JSON without throwing on a value of the wrong kind (a mod can write a link as plain text or a number).</summary>
internal static class RowLinks
{
    /// <summary>The row a link names, as written ("None" included); null when it isn't an object with a text RowName.</summary>
    public static string? RowNameOf(JsonNode? link) =>
        (link as JsonObject)?["RowName"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The row a link points at; null for no row: not a link, empty, or "None" in any case (row names ignore case, like the game).</summary>
    public static string? LinkedRow(JsonNode? link) => RowNameOf(link) is { Length: > 0 } s && !IsNone(s) ? s : null;

    public static bool IsNone(string rowName) => string.Equals(rowName, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>A JSON string's text; null for any other value.</summary>
    public static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Every link's row name under node, at any depth (a link's own other keys are walked too).</summary>
    public static void CollectRowNames(JsonNode? node, ISet<string> names)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["RowName"] is JsonValue row && row.TryGetValue<string>(out var name))
                {
                    names.Add(name);
                }

                foreach (var (_, child) in o)
                {
                    CollectRowNames(child, names);
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    CollectRowNames(child, names);
                }

                break;
        }
    }
}
