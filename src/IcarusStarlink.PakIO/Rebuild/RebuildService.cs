using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.GameplayToggles;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Rebuild;

/// <summary>
/// Composes pieces built across earlier phases into one pipeline: ExmodFieldChangeMapper (Phase
/// 2) reads each queued mod's own sparse changes, MergeEngine/MultiFileMerger (Phase 1) resolve
/// and apply them against real base data, DataTableJson (Weekly Changes/Phase 5) bridges real
/// DataTable JSON's array shape to the keyed shape those expect, and IUnrealPakService (Phase 5,
/// extended for this) packs the result. Nothing here re-implements any of that — this is glue.
/// </summary>
public sealed class RebuildService(IUnrealPakService unrealPakService) : IRebuildService
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };

    public async Task<RebuildResult> RebuildAsync(
        IReadOnlyList<ExmodPackageContents> queuedMods, GameplayOptions gameplayOptions, string dataFolder, string unrealPakExePath, string outputPakPath,
        IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? manualPicks = null,
        CancellationToken cancellationToken = default)
    {
        var report = new MergeReport();

        // The merge computation (reading every required base-game JSON file, resolving field
        // conflicts, applying gameplay options) is synchronous and, for a large queue, not cheap —
        // offloaded via Task.Run so it doesn't block the calling (UI) thread the way running it
        // bare ahead of this method's first real await used to.
        var (mergedTables, originalFileJsonByFile) = await Task.Run(() =>
        {
            var classifier = new DefaultSemanticClassifier();

            var orderedModChanges = queuedMods
                .Select(mod => ExmodFieldChangeMapper.ToFieldChanges(mod.Package, classifier))
                .ToList();
            var resolvedChanges = MergeEngine.Merge(orderedModChanges, new MergeRuleRegistry(), manualPicks);

            var requiredFiles = resolvedChanges.Select(c => c.CurrentFile)
                .Concat(GameplayOptionsApplier.RequiredCurrentFiles(gameplayOptions))
                .Distinct();
            var (baseTablesByFile, originalJson) = ReadBaseTables(requiredFiles, dataFolder, report);
            // The Dictionary(IDictionary) copy constructor does NOT inherit the source's comparer, so
            // this has to be specified again explicitly — otherwise merged would silently revert
            // to case-sensitive keys even though baseTablesByFile/MultiFileMerger.Apply's own result
            // are both already case-insensitive.
            var merged = new Dictionary<string, JsonObject>(
                MultiFileMerger.Apply(baseTablesByFile, resolvedChanges, report), StringComparer.OrdinalIgnoreCase);

            // Gameplay options apply as a final pass over the already-merged result — matching classic
            // IMM's own documented behavior ("these new options are added after the mods are all
            // merged") — and can target a file no queued mod's own FieldChange touches at all (options
            // work with an empty queue too), so make sure those land here even though
            // MultiFileMerger.Apply only ever populates entries for files a FieldChange actually
            // touches.
            foreach (var file in GameplayOptionsApplier.RequiredCurrentFiles(gameplayOptions))
            {
                if (!merged.ContainsKey(file) && baseTablesByFile.TryGetValue(file, out var baseTable))
                {
                    merged[file] = baseTable;
                }
            }
            GameplayOptionsApplier.Apply(gameplayOptions, merged, report);

            return (Merged: merged, Original: originalJson);
        }, cancellationToken);

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"Rebuild_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            // Same reasoning as the merge computation above — writing every merged table plus every
            // queued mod's own binary assets to disk is synchronous file I/O, offloaded so it doesn't
            // block the UI thread either. Still inside this try/finally, so staging cleanup below is
            // guaranteed even if a write here fails.
            await Task.Run(() =>
            {
                StageMergedTables(mergedTables, originalFileJsonByFile, stagingDirectory);
                StageAssets(queuedMods, stagingDirectory);
            }, cancellationToken);

            var packedFileCount = await unrealPakService.CreatePakAsync(unrealPakExePath, stagingDirectory, outputPakPath, cancellationToken);
            var manifestPath = WriteManifest(queuedMods, outputPakPath);

            return new RebuildResult(mergedTables.Count, packedFileCount, outputPakPath, manifestPath, report.Warnings);
        }
        finally
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    /// <summary>
    /// EXMOD's own CurrentFile convention flattens the real folder path with dashes
    /// ("Traits-D_Fuel.json") — confirmed against dozens of real .EXMOD files, where a plain
    /// `.Replace('-', '/')` recovers the real extracted-data-relative path ("Traits/D_Fuel.json")
    /// with no ambiguity (no real DataTable filename contains an embedded dash).
    /// </summary>
    private static (Dictionary<string, JsonObject> Keyed, Dictionary<string, JsonObject> Original) ReadBaseTables(
        IEnumerable<string> currentFiles, string dataFolder, MergeReport report)
    {
        // OrdinalIgnoreCase throughout — CurrentFile denotes a real Windows file path, and
        // different EXMOD authors' extraction tools aren't guaranteed to emit it with consistent
        // casing (MergeEngine/MultiFileMerger key their own dictionaries the same case-insensitive
        // way for the same reason).
        var keyed = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var original = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var currentFile in currentFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var realRelativePath = currentFile.Replace('-', '/');
            var basePath = Path.Combine(dataFolder, realRelativePath);
            if (!File.Exists(basePath))
            {
                report.AddWarning(
                    $"Skipped '{currentFile}' — no matching file at '{realRelativePath}' in the extracted game data. "
                    + "Run Update data folder again if the game has updated since your last one.");
                continue;
            }

            var fileJson = JsonNode.Parse(File.ReadAllText(basePath))!.AsObject();
            original[currentFile] = fileJson;
            keyed[currentFile] = DataTableJson.RowsToKeyedObject(fileJson, duplicateName => report.AddWarning(
                $"'{currentFile}' has more than one row named '{duplicateName}' — only the last one was kept, so a merge against the others' baseline is invisible."));
        }

        return (keyed, original);
    }

    private static void StageMergedTables(
        IReadOnlyDictionary<string, JsonObject> mergedTables, IReadOnlyDictionary<string, JsonObject> originalFileJsonByFile, string stagingDirectory)
    {
        foreach (var (currentFile, mergedKeyedTable) in mergedTables)
        {
            var realRelativePath = currentFile.Replace('-', '/');
            var fullFile = DataTableJson.KeyedObjectToRows(originalFileJsonByFile[currentFile], mergedKeyedTable);

            var destPath = Path.Combine(stagingDirectory, "data", realRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllText(destPath, fullFile.ToJsonString(JsonWriteOptions));
        }
    }

    /// <summary>
    /// A mod's own asset paths are already pak-root-relative (confirmed against real mods' own
    /// extracted folders — e.g. "BP/Building/BP_Building_Beam.uasset", no per-mod prefix), so
    /// they're staged as-is. Later mods in the queue win on a literal path collision — same
    /// last-write-wins default the field-conflict resolution already uses.
    /// </summary>
    private static void StageAssets(IReadOnlyList<ExmodPackageContents> queuedMods, string stagingDirectory)
    {
        foreach (var mod in queuedMods)
        {
            foreach (var asset in mod.Assets)
            {
                var destPath = Path.Combine(stagingDirectory, asset.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.WriteAllBytes(destPath, asset.Content);
            }
        }
    }

    private static string WriteManifest(IReadOnlyList<ExmodPackageContents> queuedMods, string outputPakPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPakPath)!;
        // Not relying on CreatePakAsync having already created this as a side effect — that's an
        // implicit coupling between two methods that's easy to silently break later (e.g. a
        // different IUnrealPakService implementation that doesn't happen to do this).
        Directory.CreateDirectory(outputDirectory);
        var manifestPath = Path.Combine(outputDirectory, InstallManifestNames.PakManifest);

        var text = new StringBuilder();
        text.AppendLine("Includes the following mods:");
        foreach (var mod in queuedMods)
        {
            text.AppendLine(mod.Package.Name);
        }

        File.WriteAllText(manifestPath, text.ToString());
        return manifestPath;
    }
}
