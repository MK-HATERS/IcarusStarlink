using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>Fans a resolved change list out across the base tables it touches, one TableApplier.Apply per file.</summary>
public static class MultiFileMerger
{
    /// <param name="sourceOf">Which mod a change came from (its name), so the warnings can say; null when unknown.</param>
    public static IReadOnlyDictionary<string, JsonObject> Apply(
        IReadOnlyDictionary<string, JsonObject> baseTablesByFile,
        IReadOnlyList<FieldChange> resolvedChanges,
        MergeReport? report = null,
        Func<FieldChange, string?>? sourceOf = null)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        // CurrentFile denotes a real (case-insensitive) Windows file path — grouped the same way
        // MergeEngine already keys its own conflict groups, so two mods that both edited "the
        // same" file under different casing still land together here too.
        foreach (var fileGroup in resolvedChanges.GroupBy(c => c.CurrentFile, StringComparer.OrdinalIgnoreCase))
        {
            if (!baseTablesByFile.TryGetValue(fileGroup.Key, out var baseTable))
            {
                var mods = sourceOf is null ? [] : fileGroup.Select(sourceOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                var rows = fileGroup.Select(c => c.ItemName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                report?.AddWarning($"{(mods.Count == 0 ? "" : $"{string.Join(", ", mods)}: ")}the game has no table '{fileGroup.Key}', so the merge skipped "
                    + $"{(rows == 1 ? "its row" : $"its {rows} rows")}. The mod's fix window in the Library can move them when the game has that table under another folder.");
                continue;
            }

            result[fileGroup.Key] = TableApplier.Apply(baseTable, fileGroup, report, sourceOf);
        }

        return result;
    }
}
