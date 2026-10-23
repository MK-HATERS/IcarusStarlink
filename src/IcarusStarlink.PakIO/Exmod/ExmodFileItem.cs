using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// One "File_Items" entry: a DataTable row name plus whichever fields the mod changed on it.
/// Fields is open-ended by design — real samples show only the properties that actually
/// changed, never a full row copy — so it's a plain dictionary rather than a fixed POCO shape.
/// </summary>
public sealed class ExmodFileItem
{
    public required string Name { get; set; }

    public Dictionary<string, JsonNode?> Fields { get; set; } = [];

    /// <summary>
    /// A copy under another name with its own copies of every value: a JsonNode can only belong
    /// to one parent, and a shared one would let an edit of the copy change the original.
    /// </summary>
    public ExmodFileItem CloneAs(string name) => new() { Name = name, Fields = DeepCloneFields(Fields) };

    /// <summary>An item from a table row's JSON (a game row, an EXMOD File_Items entry): every field but its "Name", each copied.</summary>
    public static ExmodFileItem FromRow(string name, JsonObject row) =>
        new() { Name = name, Fields = DeepCloneFields(row.Where(kv => kv.Key != "Name")) };

    private static Dictionary<string, JsonNode?> DeepCloneFields(IEnumerable<KeyValuePair<string, JsonNode?>> fields)
    {
        var copy = new Dictionary<string, JsonNode?>();
        foreach (var (key, value) in fields)
        {
            copy[key] = value?.DeepClone();
        }

        return copy;
    }
}
