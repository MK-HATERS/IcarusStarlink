using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>Fans a resolved change list out across the base tables it touches, one TableApplier.Apply per file.</summary>
public static class MultiFileMerger
{
    public static IReadOnlyDictionary<string, JsonObject> Apply(
        IReadOnlyDictionary<string, JsonObject> baseTablesByFile,
        IReadOnlyList<FieldChange> resolvedChanges,
        MergeReport? report = null)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        // CurrentFile denotes a real (case-insensitive) Windows file path — grouped the same way
        // MergeEngine already keys its own conflict groups, so two mods that both edited "the
        // same" file under different casing still land together here too.
        foreach (var fileGroup in resolvedChanges.GroupBy(c => c.CurrentFile, StringComparer.OrdinalIgnoreCase))
        {
            if (!baseTablesByFile.TryGetValue(fileGroup.Key, out var baseTable))
            {
                report?.AddWarning($"Skipped file '{fileGroup.Key}' — not present in base data.");
                continue;
            }

            result[fileGroup.Key] = TableApplier.Apply(baseTable, fileGroup, report);
        }

        return result;
    }
}
