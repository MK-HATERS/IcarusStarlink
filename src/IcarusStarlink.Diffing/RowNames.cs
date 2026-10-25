using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>
/// Row names are FNames in the game, so case doesn't matter: a mod's "pickaxe_mk2" is the game's
/// "Pickaxe_MK2". Before this the merge looked rows up by exact spelling and so added a second,
/// half-filled row beside the game's one, while the health check (which already ignored case)
/// said nothing was wrong.
/// </summary>
public static class RowNames
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private sealed record Index(Dictionary<string, string> Keys, int RowCount);

    private static readonly ConditionalWeakTable<JsonObject, Index> Indexes = new();

    /// <summary>
    /// The table's own spelling of a row name, or null when the table has no such row. For tables
    /// that aren't being added to (the base game tables): the case-insensitive index is built once
    /// per table and rebuilt only if the table's row count has changed since.
    /// </summary>
    public static string? FindKey(JsonObject table, string name)
    {
        if (table.ContainsKey(name))
        {
            return name;
        }

        if (!Indexes.TryGetValue(table, out var index) || index.RowCount != table.Count)
        {
            index = new Index(BuildIndex(table), table.Count);
            Indexes.AddOrUpdate(table, index);
        }

        return index.Keys.TryGetValue(name, out var key) && table.ContainsKey(key) ? key : null;
    }

    /// <summary>A table's row names, ignoring case, mapped to the table's own spelling (the first one when the table itself has two).</summary>
    public static Dictionary<string, string> BuildIndex(JsonObject table)
    {
        var index = new Dictionary<string, string>(Comparer);
        foreach (var (name, _) in table)
        {
            index.TryAdd(name, name);
        }

        return index;
    }
}
