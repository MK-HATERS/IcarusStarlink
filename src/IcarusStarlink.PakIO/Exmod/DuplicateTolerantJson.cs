using System.Text.Json;
using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Parses JSON that may contain the same key twice inside one object — which real mod files
/// genuinely do. Two confirmed examples from real mods: a mod whose header lists
/// "description" twice (which made the whole mod unreadable and invisible in the Library), and a
/// Jimk72-authored file whose item lists "ResourceCostMultipliers" twice.
///
/// JsonNode.Parse can't represent those: it builds a property dictionary per object and throws
/// ArgumentException the moment a duplicate is queried. JsonDocument tolerates them, so this walks
/// a JsonDocument and rebuilds a clean JsonNode tree, keeping the LAST occurrence of a repeated
/// key — the same rule every mainstream JSON parser applies, and the one that matches what these
/// files evidently mean (the later value is the one classic IMM ends up using too).
/// </summary>
internal static class DuplicateTolerantJson
{
    /// <summary>A hand-edited file may carry a trailing comma or a // comment; neither changes what it means.</summary>
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static JsonNode? Parse(string json)
    {
        // One copy of the whole document, made before the pooled original is returned: every scalar
        // below wraps an element of this copy.
        using var document = JsonDocument.Parse(json, Options);
        return Convert(document.RootElement.Clone());
    }

    private static JsonNode? Convert(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    // Plain indexer assignment is what makes last-wins work: a repeated key
                    // overwrites the earlier entry instead of throwing.
                    obj[property.Name] = Convert(property.Value);
                }

                return obj;

            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Convert(item));
                }

                return array;

            case JsonValueKind.Null:
                return null;

            default:
                // The whole document was cloned once in Parse, so this Clone returns the element
                // itself (no copy) and it outlives the pooled original.
                return JsonValue.Create(element.Clone());
        }
    }
}
