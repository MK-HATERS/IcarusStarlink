using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>JSON values compared the way the game reads them, as text that can key a dictionary.</summary>
internal static class JsonText
{
    /// <summary>
    /// One text per value as the game reads it: an object's keys in a fixed order, numbers by value
    /// (2 and 2.0 are one number), arrays in their own order. Two entries with the same canonical
    /// text are the same entry, however each mod wrote it.
    /// </summary>
    public static string Canonical(JsonNode? node)
    {
        var text = new StringBuilder();
        Append(node, text);
        return text.ToString();
    }

    private static void Append(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case null:
                text.Append("null");
                break;
            case JsonObject obj:
                text.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        text.Append(',');
                    }

                    first = false;
                    text.Append(JsonSerializer.Serialize(key)).Append(':');
                    Append(value, text);
                }

                text.Append('}');
                break;
            case JsonArray array:
                text.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        text.Append(',');
                    }

                    Append(array[i], text);
                }

                text.Append(']');
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number
                && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                text.Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                text.Append(node.ToJsonString());
                break;
        }
    }
}
