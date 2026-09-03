using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// The core "what changed for THIS one file" decision every diff surface in this codebase needs
/// once it has an old/new keyed-table pair for one file key — previously reimplemented separately
/// in DataFolderChangeTracker.Compute, ModVersionComparer.CompareExmodFolders, and
/// PakCompareService.Compare.
///
/// reportEmptyNewFile controls the one place those three previously disagreed, silently: a
/// brand-new file whose own TableDiffer.Diff against an empty base finds nothing (zero rows, or
/// rows with no fields) — DataFolderChangeTracker said no (an empty file appearing between game
/// patches is noise, not a real content change worth showing in Weekly Changes);
/// ModVersionComparer and PakCompareService said yes ("a new file exists" is itself meaningful to
/// someone checking what an author changed, or whether two paks are equivalent, even if that file
/// happens to be empty right now). Both existing behaviors are kept exactly as they were — this
/// makes the difference an explicit, named choice per caller instead of an unexplained divergence,
/// rather than picking one as more "correct" and silently changing the other two.
/// </summary>
internal static class DataTableFileDiffer
{
    public static ChangedDataFile? Diff(
        string relativePath, JsonObject? oldTable, JsonObject? newTable, ISemanticClassifier classifier, bool reportEmptyNewFile)
    {
        if (oldTable is not null && newTable is null)
        {
            return new ChangedDataFile(
                relativePath, IsNewFile: false, IsRemovedFile: true,
                RemovedRowNames: [.. oldTable.Select(kv => kv.Key)], FieldChanges: []);
        }

        if (oldTable is null && newTable is not null)
        {
            var newFileChanges = TableDiffer.Diff(new JsonObject(), newTable, relativePath, classifier);
            return reportEmptyNewFile || newFileChanges.Count > 0
                ? new ChangedDataFile(relativePath, IsNewFile: true, IsRemovedFile: false, RemovedRowNames: [], newFileChanges)
                : null;
        }

        if (oldTable is not null && newTable is not null)
        {
            var fieldChanges = TableDiffer.Diff(oldTable, newTable, relativePath, classifier);
            var removedRowNames = oldTable.Select(kv => kv.Key).Except(newTable.Select(kv => kv.Key)).ToList();
            return fieldChanges.Count > 0 || removedRowNames.Count > 0
                ? new ChangedDataFile(relativePath, IsNewFile: false, IsRemovedFile: false, removedRowNames, fieldChanges)
                : null;
        }

        // Both null: no caller currently invokes this for a key present in neither side, but
        // there's nothing meaningful to report if one ever did.
        return null;
    }
}
