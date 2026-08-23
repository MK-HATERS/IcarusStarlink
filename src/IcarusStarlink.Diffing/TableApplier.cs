using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>
/// Mirror image of TableDiffer: replays a set of FieldChanges onto a fresh copy of the base
/// table (fresh meaning "as of the current game version", which may have moved on since the
/// changes were diffed). A change whose row no longer exists and wasn't itself a new-item change
/// is skipped with a warning rather than failing the whole merge — the game likely removed or
/// renamed that row in a later patch.
/// </summary>
public static class TableApplier
{
    public static JsonObject Apply(JsonObject baseTable, IEnumerable<FieldChange> changes, MergeReport? report = null)
    {
        var result = baseTable.DeepClone()!.AsObject();

        // Reported once per item rather than once per field: an EXMOD-sourced change always
        // carries IsNewItem: true (the format doesn't record whether the row existed when the mod
        // was made), so without this a mod whose target row a game patch RENAMED OR REMOVED
        // silently gets a half-formed row invented for it — no error, just content that quietly
        // doesn't work in game. Surfacing it is the only way a user can tell "this mod adds new
        // content" from "this mod is out of date for this game version".
        var createdItems = new HashSet<string>(StringComparer.Ordinal);
        var createdItemFieldCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var change in changes)
        {
            if (result[change.ItemName] is not JsonObject row)
            {
                if (!change.IsNewItem)
                {
                    report?.AddWarning(
                        $"Skipped {change.CurrentFile}:{change.ItemName}.{change.FieldName} — item no longer exists in base data.");
                    continue;
                }

                row = new JsonObject();
                result[change.ItemName] = row;
                createdItems.Add($"{change.CurrentFile}|{change.ItemName}");
            }

            var createdKey = $"{change.CurrentFile}|{change.ItemName}";
            if (createdItems.Contains(createdKey))
            {
                createdItemFieldCounts[createdKey] = createdItemFieldCounts.GetValueOrDefault(createdKey) + 1;
            }

            // IsFieldRemoved (not NewValue == null — see FieldChange) distinguishes "remove the
            // key" from "set it to an explicit JSON null".
            if (change.IsFieldRemoved)
            {
                row.Remove(change.FieldName);
            }
            else
            {
                row[change.FieldName] = change.NewValue?.DeepClone();
            }
        }

        foreach (var createdKey in createdItems)
        {
            var separator = createdKey.IndexOf('|');
            var currentFile = createdKey[..separator];
            var itemName = createdKey[(separator + 1)..];
            var fieldCount = createdItemFieldCounts.GetValueOrDefault(createdKey);

            report?.AddNote(StaleItemHeuristic.BuildNote(currentFile, itemName, fieldCount));
        }

        return result;
    }
}
