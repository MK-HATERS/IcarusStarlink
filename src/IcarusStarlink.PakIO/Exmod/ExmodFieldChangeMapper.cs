using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>Converts between EXMOD's sparse on-disk row format and the FieldChange model TableApplier/MergeEngine operate on.</summary>
public static class ExmodFieldChangeMapper
{
    public static IReadOnlyList<FieldChange> ToFieldChanges(ExmodPackage package, ISemanticClassifier classifier)
    {
        var changes = new List<FieldChange>();

        foreach (var row in package.Rows)
        {
            foreach (var item in row.FileItems)
            {
                // Snapshot, not a live enumeration — matches the same defensive .ToList()
                // ExmodJson.RowToJsonObject/ExmodBaseDiffer.ToKeyedObject already use, for the same
                // reason: item.Fields is a plain mutable Dictionary the EXMOD editor writes into on
                // every keystroke, and this method isn't guaranteed to only ever run against an
                // already-settled, no-longer-edited package.
                foreach (var (fieldName, value) in item.Fields.ToList())
                {
                    changes.Add(new FieldChange(
                        row.CurrentFile,
                        item.Name,
                        fieldName,
                        OriginalValue: null, // not stored on disk — EXMOD only records the new value
                        NewValue: value?.DeepClone(),
                        classifier.Classify(row.CurrentFile, fieldName, value),
                        // EXMODZ files don't record whether the row existed in base data at
                        // extraction time. Defaulting true is the safer failure mode: if the row
                        // turns out to already exist in whatever base this gets applied to, the
                        // flag is never consulted; if it's genuinely missing, we create it rather
                        // than silently dropping the mod's content.
                        IsNewItem: true,
                        // A null value is only ever written here by our own FromFieldChanges to
                        // represent a removed field — there's no other reason a sparse EXMOD diff
                        // would list a field at all except to say "this changed". Treating it as
                        // a removal on read is what makes Serialize -> Parse -> ToFieldChanges
                        // round-trip a removed field correctly instead of silently turning it
                        // into "set to null".
                        IsFieldRemoved: value is null));
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Groups changes back into EXMOD's per-file, per-item row shape. A removed field (no way to
    /// represent that in the sparse format) is written as an explicit JSON null — the closest
    /// lossy approximation the format allows.
    /// </summary>
    public static List<ExmodFileRow> FromFieldChanges(IEnumerable<FieldChange> changes)
    {
        // OrdinalIgnoreCase on the outer (CurrentFile) key only — same reasoning as MergeEngine/
        // MultiFileMerger/RebuildService's own CurrentFile-keyed dictionaries (a real Windows file
        // path, not guaranteed consistent casing across EXMOD authors' extraction tools) — without
        // it, two FieldChanges referencing the same file under different casing would fragment into
        // two separate ExmodFileRow entries instead of merging into one. ItemName (the inner key)
        // stays case-sensitive, a real JSON property name rather than a file path.
        var itemsByFileThenName = new Dictionary<string, Dictionary<string, ExmodFileItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            ReservedFieldNames.EnsureFieldNameAllowed(change.ItemName, change.CurrentFile, change.FieldName);

            if (!itemsByFileThenName.TryGetValue(change.CurrentFile, out var itemsByName))
            {
                itemsByName = [];
                itemsByFileThenName[change.CurrentFile] = itemsByName;
            }

            if (!itemsByName.TryGetValue(change.ItemName, out var item))
            {
                item = new ExmodFileItem { Name = change.ItemName };
                itemsByName[change.ItemName] = item;
            }

            // IsFieldRemoved is authoritative, same as TableApplier — don't just trust NewValue
            // being null, since nothing enforces that invariant holds for every FieldChange.
            item.Fields[change.FieldName] = change.IsFieldRemoved ? null : change.NewValue?.DeepClone();
        }

        return itemsByFileThenName
            .Select(fileEntry => new ExmodFileRow
            {
                CurrentFile = fileEntry.Key,
                FileItems = [.. fileEntry.Value.Values],
            })
            .ToList();
    }
}
