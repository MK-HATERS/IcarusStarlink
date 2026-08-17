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

        return result;
    }
}
