using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Rebuild;

/// <summary>
/// Turns a prebuilt/opaque pak's own DataTable JSON into real FieldChanges by diffing it against
/// current base game data — the missing piece that lets a prebuilt pak become a genuine
/// field-level MergeEngine participant instead of unconditionally overwriting whatever it
/// collides with. Reuses BaseDataFileReader (the same base-file read+key+warn sequence
/// RebuildService's own ReadBaseTables and the EXMOD editor's ExmodBaseDiffer already use) for
/// both sides of the diff — pointed at dataFolder for the real base value, and at the prebuilt
/// pak's own already-extracted "data" scratch subfolder for its "modded" value — since both are
/// exactly the same "resolve CurrentFile within some root, parse, key" operation, just against
/// different roots.
/// </summary>
public static class PrebuiltPakFieldChangeExtractor
{
    /// <summary>
    /// scratchExtractDirectory must already hold the prebuilt pak's own full extraction — the
    /// caller extracts it once and reuses the result both for this diff and for copying through
    /// whatever isn't field-diffable afterward, so a prebuilt pak is never extracted twice. Returns
    /// the paths (relative to scratchExtractDirectory itself, e.g. "data/Traits/D_Fuel.json") that
    /// were successfully diffed, so the caller — which enumerates scratchExtractDirectory, not its
    /// "data" subfolder, since it also has to copy through non-"data" files — knows not to also
    /// raw-copy them; StageMergedTables's own output already replaces them correctly. Only files
    /// under the pak's own "data/" folder (the convention StageMergedTables/ReadBaseTables both
    /// already write/read) are diffable; everything else (binary assets, a data/*.json file with no
    /// matching base table at all, or one that isn't valid DataTable JSON) has no field structure
    /// to reconcile and is left for the caller's own raw-copy-through pass.
    /// </summary>
    public static (IReadOnlyList<FieldChange> Changes, IReadOnlySet<string> DiffedScratchRelativePaths) Extract(
        string scratchExtractDirectory, string dataFolder, string pakName, ISemanticClassifier classifier, MergeReport report)
    {
        var dataRoot = Path.Combine(scratchExtractDirectory, "data");
        if (!Directory.Exists(dataRoot))
        {
            // Genuinely no "data" folder — a pure-asset pak — but also, confirmed live against the
            // real bundled UnrealPak.exe, the shape of a pak containing exactly ONE file: -Extract
            // flattens that lone file straight into the output root instead of preserving its
            // subfolder (an already-known UnrealPak quirk, harmless for a real prebuilt pak, which
            // always carries many files). Either way there's nothing under "data/" to diff — the
            // caller's own raw copy-through still picks up and packs the file correctly, just
            // without a field-level merge for it.
            return ([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var changes = new List<FieldChange>();
        var diffedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(dataRoot, "*.json", SearchOption.AllDirectories))
        {
            var realRelativePath = Path.GetRelativePath(dataRoot, file).Replace('\\', '/');
            var currentFile = realRelativePath.Replace('/', '-');

            var baseKeyed = BaseDataFileReader.ReadKeyedTable(dataFolder, currentFile, report);
            if (baseKeyed is null)
            {
                // No matching base file — either genuinely new content this pak adds wholesale, or
                // a stale reference to a since-removed table. Either way there's nothing to diff
                // against (already warned above); the caller's own raw copy-through handles it.
                continue;
            }

            JsonObject moddedKeyed;
            try
            {
                // dataRoot, not scratchExtractDirectory — currentFile's real relative path
                // ("Crafting/D_ProcessorRecipes.json") is rooted at the pak's own "data/" folder,
                // the same way dataFolder is already rooted at the real extracted Data folder on
                // the base side above, not at some parent of it.
                var keyed = BaseDataFileReader.ReadKeyedTable(dataRoot, currentFile, report);
                if (keyed is null)
                {
                    // The file we're iterating demonstrably exists, so this can't actually happen —
                    // stay safe rather than throw if ReadKeyedTable's own guard ever disagrees.
                    continue;
                }

                moddedKeyed = keyed;
            }
            catch (JsonException)
            {
                // A prebuilt pak's own "data" folder isn't guaranteed to hold well-formed DataTable
                // JSON the way this app's own extracted game data always is — a broken third-party
                // mod tool's output shouldn't abort the whole Rebuild, just fall back to a raw copy
                // for this one file instead of a field-level merge.
                report.AddWarning(
                    $"Prebuilt pak '{pakName}''s own '{currentFile}' isn't valid JSON — copied through as-is instead of field-merged.");
                continue;
            }

            changes.AddRange(TableDiffer.Diff(baseKeyed, moddedKeyed, currentFile, classifier, report));
            diffedPaths.Add($"data/{realRelativePath}");
        }

        return (changes, diffedPaths);
    }
}
