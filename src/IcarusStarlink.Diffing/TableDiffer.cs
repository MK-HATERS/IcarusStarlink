using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>
/// Diffs one DataTable-shaped JSON file (row name -&gt; {field -&gt; value}) against a modified
/// copy of it, at (item, field) granularity — matching the real EXMODZ samples this was
/// designed against, which never diff into a compound field's own internals.
///
/// Only rows the modded copy defines are visited: rows the modder's file doesn't mention at all
/// are left untouched (no invented "row deletion" semantics — there's no evidence that's a real
/// modding pattern, so it's simply out of scope). A row missing from base but present in modded
/// is a new item; a field missing from one side of an existing row is a real field-level change.
/// </summary>
public static class TableDiffer
{
    public static IReadOnlyList<FieldChange> Diff(
        JsonObject baseTable,
        JsonObject moddedTable,
        string currentFile,
        ISemanticClassifier classifier,
        MergeReport? report = null)
    {
        var changes = new List<FieldChange>();

        foreach (var (itemName, moddedValue) in moddedTable)
        {
            if (moddedValue is not JsonObject moddedRow)
            {
                // A row that isn't a JSON object (or is JSON null) is malformed input, not "every
                // field removed" — skip and flag it rather than silently blanking the row.
                report?.AddWarning($"Skipped {currentFile}:{itemName} — modded row is not a JSON object.");
                continue;
            }

            var baseRow = baseTable[itemName] as JsonObject;
            var isNewItem = baseRow is null;

            var fieldNames = isNewItem
                ? moddedRow.Select(kv => kv.Key)
                : moddedRow.Select(kv => kv.Key).Union(baseRow!.Select(kv => kv.Key));

            foreach (var fieldName in fieldNames)
            {
                // Presence must be tracked separately from value: JsonNode represents both "key
                // absent" and "key present with an explicit JSON null" as C# null, so the value
                // alone can't tell "unchanged" (both absent) apart from "newly nulled" (present
                // on one side only, with a null value).
                var baseHasField = baseRow?.ContainsKey(fieldName) ?? false;
                var moddedHasField = moddedRow.ContainsKey(fieldName);
                var baseFieldValue = baseHasField ? baseRow![fieldName] : null;
                var moddedFieldValue = moddedHasField ? moddedRow[fieldName] : null;

                if (baseHasField == moddedHasField && JsonNode.DeepEquals(baseFieldValue, moddedFieldValue))
                {
                    continue;
                }

                var semantic = classifier.Classify(currentFile, fieldName, moddedFieldValue);
                changes.Add(new FieldChange(
                    currentFile,
                    itemName,
                    fieldName,
                    baseFieldValue?.DeepClone(),
                    moddedFieldValue?.DeepClone(),
                    semantic,
                    isNewItem,
                    IsFieldRemoved: !moddedHasField));
            }
        }

        return changes;
    }
}
