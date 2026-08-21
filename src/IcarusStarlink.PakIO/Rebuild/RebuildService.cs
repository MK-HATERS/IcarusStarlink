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
        CancellationToken cancellationToken = default)
    {
        var report = new MergeReport();
        var classifier = new DefaultSemanticClassifier();

        var orderedModChanges = queuedMods
            .Select(mod => ExmodFieldChangeMapper.ToFieldChanges(mod.Package, classifier))
            .ToList();
        var resolvedChanges = MergeEngine.Merge(orderedModChanges, new MergeRuleRegistry());

        var requiredFiles = resolvedChanges.Select(c => c.CurrentFile)
            .Concat(GameplayOptionsApplier.RequiredCurrentFiles(gameplayOptions))
            .Distinct();
        var (baseTablesByFile, originalFileJsonByFile) = ReadBaseTables(requiredFiles, dataFolder, report);
        var mergedTables = new Dictionary<string, JsonObject>(MultiFileMerger.Apply(baseTablesByFile, resolvedChanges, report));

        // Gameplay options apply as a final pass over the already-merged result — matching classic
        // IMM's own documented behavior ("these new options are added after the mods are all
        // merged") — and can target a file no queued mod's own FieldChange touches at all (options
        // work with an empty queue too), so make sure those land here even though
        // MultiFileMerger.Apply only ever populates entries for files a FieldChange actually
        // touches.
        foreach (var file in GameplayOptionsApplier.RequiredCurrentFiles(gameplayOptions))
        {
            if (!mergedTables.ContainsKey(file) && baseTablesByFile.TryGetValue(file, out var baseTable))
            {
                mergedTables[file] = baseTable;
            }
        }
        GameplayOptionsApplier.Apply(gameplayOptions, mergedTables, report);

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"Rebuild_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            StageMergedTables(mergedTables, originalFileJsonByFile, stagingDirectory);
            StageAssets(queuedMods, stagingDirectory);

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
        var keyed = new Dictionary<string, JsonObject>();
        var original = new Dictionary<string, JsonObject>();

        foreach (var currentFile in currentFiles.Distinct())
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
            keyed[currentFile] = DataTableJson.RowsToKeyedObject(fileJson);
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
