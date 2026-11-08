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

    /// <summary>CloneAs for a new row made from this one: its texts get keys under the new name (a text key must stay unique per row).</summary>
    public ExmodFileItem DuplicateAs(string name)
    {
        var copy = CloneAs(name);
        copy.MoveTextKeys(Name, name);
        return copy;
    }

    /// <summary>
    /// Texts keyed under <paramref name="oldRow"/> ("&lt;oldRow&gt;-DisplayName") move to
    /// <paramref name="newRow"/>, their words unchanged. A text under another key stays: reusing the
    /// game's own key keeps the game's translations.
    /// </summary>
    public void MoveTextKeys(string oldRow, string newRow)
    {
        var prefix = oldRow + "-";
        foreach (var (field, value) in Fields.ToList())
        {
            if (RowLinks.StringOf(value) is { } text
                && NsLocText.TryParse(text, out _, out var key, out _)
                && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && NsLocText.WithKey(text, newRow + key[oldRow.Length..]) is { } moved)
            {
                Fields[field] = moved;
            }
        }
    }

    /// <summary>An item from a table row's JSON (a game row, an EXMOD File_Items entry): every field but its "Name", each copied.</summary>
    public static ExmodFileItem FromRow(string name, JsonObject row) =>
        new() { Name = name, Fields = DeepCloneFields(row.Where(kv => kv.Key != "Name")) };

    /// <summary>
    /// A row listed more than once in one table entry, the way the merge sees it: every copy's
    /// fields together, a field set in two copies taking the later copy's value. Named like the
    /// last copy. The values are the copies' own nodes (not cloned): for reading only.
    /// </summary>
    public static ExmodFileItem Combine(IReadOnlyList<ExmodFileItem> copies)
    {
        if (copies.Count == 1)
        {
            return copies[0];
        }

        var fields = new Dictionary<string, JsonNode?>();
        foreach (var copy in copies)
        {
            foreach (var (key, value) in copy.Fields)
            {
                fields[key] = value;
            }
        }

        return new() { Name = copies[^1].Name, Fields = fields };
    }

    /// <summary>Each row name's copies in one table entry combined (Combine), names compared ignoring case like the game.</summary>
    public static IEnumerable<ExmodFileItem> CombineByName(IEnumerable<ExmodFileItem> items) =>
        items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => Combine(g.ToList()));

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
