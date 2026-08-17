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
        var result = new Dictionary<string, JsonObject>();

        foreach (var fileGroup in resolvedChanges.GroupBy(c => c.CurrentFile))
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
