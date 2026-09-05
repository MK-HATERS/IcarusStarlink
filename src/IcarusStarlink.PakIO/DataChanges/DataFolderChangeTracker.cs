using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// Compares two Data-folder snapshots (the previous "Update data folder" extraction against the
/// just-completed one) file by file, reusing TableDiffer — the same engine Phase 1 built for
/// base-vs-modded diffing generalizes directly to old-week-vs-new-week diffing, since both are
/// "two versions of the same DataTable JSON."
///
/// TableDiffer only ever iterates the *new* side's own rows (see its own doc comment) — a row the
/// old snapshot had that the new one doesn't mention at all is invisible to it, since that's
/// exactly the right behavior for a mod's sparse diff (mods don't "delete" base rows) but wrong
/// for this use case, where a whole row disappearing between game patches is a real, meaningful
/// signal. Computed as a separate supplementary pass here instead, rather than changing
/// TableDiffer itself and risking its already-tested mod-diff behavior.
/// </summary>
public static class DataFolderChangeTracker
{
    public static WeeklyChangeReport Compute(
        string previousDataFolder, string currentDataFolder, DateTimeOffset previousUpdateAt, DateTimeOffset currentUpdateAt)
    {
        var classifier = new DefaultSemanticClassifier();
        var previousFiles = ListJsonFiles(previousDataFolder);
        var currentFiles = ListJsonFiles(currentDataFolder);

        var changedFiles = new List<ChangedDataFile>();
        foreach (var relativePath in previousFiles.Keys.Union(currentFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var hasPrevious = previousFiles.TryGetValue(relativePath, out var previousAbsolutePath);
            var hasCurrent = currentFiles.TryGetValue(relativePath, out var currentAbsolutePath);

            JsonObject? previousTable;
            JsonObject? currentTable;
            try
            {
                previousTable = hasPrevious ? DataTableJson.RowsToKeyedObject(ReadJsonObject(previousAbsolutePath!)) : null;
                currentTable = hasCurrent ? DataTableJson.RowsToKeyedObject(ReadJsonObject(currentAbsolutePath!)) : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A single malformed/truncated/locked JSON file must not abort this whole weekly-
                // change computation — UnrealPakService.ExtractDataPakAsync's own caller throws away
                // the freshly-extracted new data and never commits it if THIS method throws, which
                // would permanently block "Update data folder" from ever succeeding again: nothing
                // about retrying fixes a file that's already corrupted on disk in the OLD extraction
                // being diffed against. Skipped the same way every other per-item batch in this
                // codebase already tolerates one bad entry (ImmExtractedMods.Parse,
                // JsonFileStore.LoadList, WeeklyChangeReportStore.LoadHistory).
                continue;
            }

            // reportEmptyNewFile: false — an empty file appearing between game patches is noise,
            // not a real content change worth showing in Weekly Changes. See DataTableFileDiffer's
            // own doc comment.
            var changed = DataTableFileDiffer.Diff(relativePath, previousTable, currentTable, classifier, reportEmptyNewFile: false);
            if (changed is not null)
            {
                changedFiles.Add(changed);
            }
        }

        return new WeeklyChangeReport(
            previousUpdateAt, currentUpdateAt, [.. changedFiles.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)]);
    }

    private static Dictionary<string, string> ListJsonFiles(string rootDirectory) =>
        Directory.GetFiles(rootDirectory, "*.json", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(rootDirectory, path).Replace('\\', '/'), path => path, StringComparer.OrdinalIgnoreCase);

    // DuplicateTolerantJson, not a plain JsonNode.Parse: these are real extracted DataTable JSON
    // files (the same "Update data folder" snapshots RebuildService's own ReadBaseTables reads),
    // confirmed elsewhere to sometimes repeat a JSON key — which JsonNode.Parse throws on and
    // would abort this whole weekly-change computation instead of degrading gracefully.
    private static JsonObject ReadJsonObject(string path) => DuplicateTolerantJson.Parse(File.ReadAllText(path))?.AsObject() ?? [];
}
