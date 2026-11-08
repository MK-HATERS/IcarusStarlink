using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// Compares two Data-folder snapshots (the previous "Refresh game data" extraction against the
/// just-completed one) file by file, reusing TableDiffer — the same engine
/// base-vs-modded diffing uses generalizes directly to old-week-vs-new-week diffing, since both are
/// "two versions of the same DataTable JSON."
///
/// TableDiffer only ever iterates the *new* side's own rows (see its own doc comment) — a row the
/// old snapshot had that the new one doesn't mention at all is invisible to it, since that's
/// exactly the right behavior for a mod's sparse diff (mods don't "delete" base rows) but wrong
/// for this use case, where a whole row disappearing between game patches is a real, meaningful
/// signal. Computed as a separate supplementary pass here instead, rather than changing
/// TableDiffer itself and risking its already-tested mod-diff behavior. A table's Defaults and
/// RowStruct are compared here too (ChangedDataFile.ChangedTableParts), since the rows alone
/// don't show them.
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

            JsonObject? previousFile;
            JsonObject? currentFile;
            JsonObject? previousTable;
            JsonObject? currentTable;
            try
            {
                previousFile = hasPrevious ? ReadJsonObject(previousAbsolutePath!) : null;
                currentFile = hasCurrent ? ReadJsonObject(currentAbsolutePath!) : null;
                previousTable = previousFile is null ? null : DataTableJson.RowsToKeyedObject(previousFile);
                currentTable = currentFile is null ? null : DataTableJson.RowsToKeyedObject(currentFile);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A single malformed/truncated/locked JSON file must not abort this whole weekly-
                // change computation — UnrealPakService.ExtractDataPakAsync's own caller throws away
                // the freshly-extracted new data and never commits it if THIS method throws, which
                // would permanently block "Refresh game data" from ever succeeding again: nothing
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

            // The rows alone miss a game update that changes only a table's Defaults or RowStruct.
            if (previousFile is not null && currentFile is not null && ChangedTableParts(previousFile, currentFile) is { Count: > 0 } parts)
            {
                changed = (changed ?? new ChangedDataFile(relativePath, IsNewFile: false, IsRemovedFile: false, RemovedRowNames: [], FieldChanges: []))
                    with { ChangedTableParts = parts };
            }

            if (changed is not null)
            {
                changedFiles.Add(changed);
            }
        }

        return new WeeklyChangeReport(
            previousUpdateAt, currentUpdateAt, [.. changedFiles.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// The top-level parts other than Rows ("Defaults", "RowStruct", …) that differ between two
    /// versions of a table, in the new file's order. Property order inside a part doesn't count.
    /// Only for files that are tables on both sides (a Rows list): other JSON isn't compared here.
    /// </summary>
    private static List<string> ChangedTableParts(JsonObject previousFile, JsonObject currentFile)
    {
        if (previousFile["Rows"] is not JsonArray || currentFile["Rows"] is not JsonArray)
        {
            return [];
        }

        var names = currentFile.Select(pair => pair.Key)
            .Concat(previousFile.Select(pair => pair.Key))
            .Where(name => name != "Rows")
            .Distinct(StringComparer.Ordinal);
        return [.. names.Where(name =>
            previousFile.ContainsKey(name) != currentFile.ContainsKey(name)
            || !JsonNode.DeepEquals(previousFile[name], currentFile[name]))];
    }

    private static Dictionary<string, string> ListJsonFiles(string rootDirectory) =>
        Directory.GetFiles(rootDirectory, "*.json", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(rootDirectory, path).Replace('\\', '/'), path => path, StringComparer.OrdinalIgnoreCase);

    // DuplicateTolerantJson, not a plain JsonNode.Parse: these are real extracted DataTable JSON
    // files (the same "Refresh game data" snapshots RebuildService's own ReadBaseTables reads),
    // confirmed elsewhere to sometimes repeat a JSON key — which JsonNode.Parse throws on and
    // would abort this whole weekly-change computation instead of degrading gracefully.
    // AsObject throws InvalidOperationException for a file whose top level is a list; caught above.
    private static JsonObject ReadJsonObject(string path) => DuplicateTolerantJson.Parse(File.ReadAllText(path))?.AsObject() ?? [];
}
