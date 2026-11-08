using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>
/// The player's starting stats (Stats-D_CharacterStartingStats.json, Base_Stats.StatsGranted) merge
/// one stat at a time, as classic IMM has since 2.3.9 ("JSON in the StatsGranted section are now
/// considered seperate values. This allows mods to add 1 stat or many stats and not effect
/// unchanged stats or stats from other mods"). Mods rely on it: laanp's WaypointTeleport sends 3
/// of the 72 stats. Merged as one value, those 3 replaced all 72 (no health, stamina, oxygen…),
/// and two mods changing different stats were a conflict only one could win.
///
/// Split turns such a change into one change per stat, under a field name like
/// "StatsGranted › (Value=\"BaseMaximumHealth_+\")": MergeEngine then groups, compares with the
/// game and reports conflicts per stat, and TableApplier writes each stat into the row's list.
/// A stat a mod doesn't list keeps the game's value; one it sets to null is taken out.
/// </summary>
public static class StatEntries
{
    public const string Separator = " › ";

    private static readonly (string CurrentFile, string FieldName)[] PerEntryFields =
    [
        ("Stats-D_CharacterStartingStats.json", "StatsGranted"),
    ];

    /// <summary>Whether this field of this table merges entry by entry (the game reads field names ignoring case).</summary>
    public static bool IsPerEntry(string currentFile, string fieldName) => PerEntryFieldName(currentFile, fieldName) is not null;

    /// <summary>The game's spelling of a per-entry field ("statsGranted" → "StatsGranted"); null when the field isn't one.</summary>
    private static string? PerEntryFieldName(string currentFile, string fieldName)
    {
        foreach (var (file, field) in PerEntryFields)
        {
            if (string.Equals(file, currentFile, StringComparison.OrdinalIgnoreCase) && string.Equals(field, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    public static string Compose(string fieldName, string entryKey) => fieldName + Separator + entryKey;

    /// <summary>A field name Compose made, split back into the field and the entry's key.</summary>
    public static bool TryParse(string fieldName, out string field, out string entryKey)
    {
        var at = fieldName.IndexOf(Separator, StringComparison.Ordinal);
        if (at <= 0)
        {
            (field, entryKey) = (fieldName, "");
            return false;
        }

        (field, entryKey) = (fieldName[..at], fieldName[(at + Separator.Length)..]);
        return true;
    }

    /// <summary>"StatsGranted › BaseMaximumHealth_+" for a per-stat field name; any other name as it is.</summary>
    public static string Display(string fieldName) =>
        TryParse(fieldName, out var field, out var key)
            ? field + Separator + (key.StartsWith("(Value=\"", StringComparison.Ordinal) && key.EndsWith("\")", StringComparison.Ordinal) ? key[8..^2] : key)
            : fieldName;

    /// <summary>
    /// One mod's changes with every per-entry field split into one change per entry; the list
    /// itself when there's nothing to split. A change that takes the whole field out, or isn't an
    /// object, is left whole. Every split change is named with the game's spelling of the field, so
    /// a mod writing "statsGranted" meets the others' "StatsGranted" on each stat.
    /// </summary>
    public static IReadOnlyList<FieldChange> Split(IReadOnlyList<FieldChange> changes)
    {
        if (!changes.Any(c => IsPerEntry(c.CurrentFile, c.FieldName) && !c.IsFieldRemoved && c.NewValue is JsonObject))
        {
            return changes;
        }

        var result = new List<FieldChange>(changes.Count);
        foreach (var change in changes)
        {
            if (PerEntryFieldName(change.CurrentFile, change.FieldName) is not { } field || change.IsFieldRemoved || change.NewValue is not JsonObject entries)
            {
                result.Add(change);
                continue;
            }

            var original = change.OriginalValue as JsonObject;
            foreach (var (key, value) in entries)
            {
                result.Add(change with
                {
                    FieldName = Compose(field, key),
                    OriginalValue = original?[key]?.DeepClone(),
                    NewValue = value?.DeepClone(),
                    Semantic = ValueSemantic.Scalar,
                    IsFieldRemoved = value is null,
                });
            }
        }

        return result;
    }

    /// <summary>One entry's value in a row (the game's, for comparing): found only when the row's field (in any case) holds that key.</summary>
    public static (bool Found, JsonNode? Value) Get(JsonObject row, string fieldName)
    {
        if (!TryParse(fieldName, out var field, out var key) || RowField(row, field) is not { } rowField
            || row[rowField] is not JsonObject entries || !entries.ContainsKey(key))
        {
            return (false, null);
        }

        return (true, entries[key]);
    }

    /// <summary>Writes one entry into the row's field, under the spelling the row has (made when the row has none), or takes it out.</summary>
    public static void Apply(JsonObject row, FieldChange change)
    {
        TryParse(change.FieldName, out var field, out var key);
        field = RowField(row, field) ?? field;
        if (row[field] is not JsonObject entries)
        {
            entries = [];
            row[field] = entries;
        }

        if (change.IsFieldRemoved)
        {
            entries.Remove(key);
        }
        else
        {
            entries[key] = change.NewValue?.DeepClone();
        }
    }

    /// <summary>The row's own spelling of a field, found ignoring case (exact spelling first); null when the row has no such field.</summary>
    private static string? RowField(JsonObject row, string field)
    {
        if (row.ContainsKey(field))
        {
            return field;
        }

        foreach (var (name, _) in row)
        {
            if (string.Equals(name, field, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }
}
