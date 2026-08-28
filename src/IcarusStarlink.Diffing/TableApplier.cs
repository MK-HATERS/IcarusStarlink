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
        //
        // The lookup key normalizes CurrentFile to uppercase-invariant — it denotes a real Windows
        // file path, and different EXMOD authors' extraction tools aren't guaranteed to emit it
        // with consistent casing (the same convention FieldChangeKeyComparer already applies for
        // MergeEngine/MultiFileMerger) — while ItemName stays exactly as-is (a real JSON row name,
        // not a file path). Without this, two changes to the "same" newly-created item that only
        // differ in CurrentFile casing would be tracked as two separate items, splitting one item's
        // real field count across two notes instead of reporting it once, correctly.
        var createdItemKeys = new HashSet<string>(StringComparer.Ordinal);
        var createdItemInfo = new Dictionary<string, (string CurrentFile, string ItemName, int FieldCount)>(StringComparer.Ordinal);

        foreach (var change in changes)
        {
            var createdKey = $"{change.CurrentFile.ToUpperInvariant()}|{change.ItemName}";

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
                createdItemKeys.Add(createdKey);
            }

            if (createdItemKeys.Contains(createdKey))
            {
                var (currentFile, itemName, fieldCount) = createdItemInfo.TryGetValue(createdKey, out var existing)
                    ? existing
                    : (change.CurrentFile, change.ItemName, 0);
                createdItemInfo[createdKey] = (currentFile, itemName, fieldCount + 1);
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

        foreach (var createdKey in createdItemKeys)
        {
            var (currentFile, itemName, fieldCount) = createdItemInfo[createdKey];
            report?.AddNote(StaleItemHeuristic.BuildNote(currentFile, itemName, fieldCount));
        }

        return result;
    }
}
