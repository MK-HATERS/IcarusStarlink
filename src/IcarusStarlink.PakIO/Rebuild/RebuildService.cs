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
        IReadOnlyList<string> prebuiltPakFilePaths,
        IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? manualPicks = null,
        IProgress<RebuildStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new MergeReport();
        progress?.Report(new RebuildStageProgress("Merging queued mods…", 0));

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

            // Category 1 gameplay options (Speed/Player/XP Boost, Disable Temperatures — the ones
            // that write into one fixed row) become one more entry here, appended last so they stay
            // highest-priority — matching the "built-in wins" default this always had — but now as
            // a real MergeEngine participant: a queued mod also touching Base_Stats.StatsGranted
            // shows up as a genuine, visible conflict instead of a silent post-merge overwrite.
            var fixedOptionChanges = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(gameplayOptions, dataFolder, report);
            if (fixedOptionChanges.Count > 0)
            {
                orderedModChanges.Add(fixedOptionChanges);
            }

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

            // Category 2 gameplay options (Stacks/Slots/Craft Cost/Speed Crafting/Unlimited Ammo/
            // Remove Weight — GameplayOptionsApplier.Apply) apply as a genuinely final pass over the
            // already-merged result — matching classic IMM's own documented behavior ("these new
            // options are added after the mods are all merged") — and deliberately still work this
            // way after Phase 1 moved Category 1 (Speed/Player/XP Boost, Disable Temperatures) into
            // a real FieldChange: Category 2 is a COMPOUNDING operation (new = current merged value
            // × factor), not a "pick one candidate value" resolution, so routing it through
            // MergeEngine the same way would silently break that compounding (a queued mod also
            // touching e.g. MaxStack would get its own value overwritten outright instead of scaled).
            // Can target a file no queued mod's own FieldChange touches at all (options work with an
            // empty queue too), so make sure those land here even though MultiFileMerger.Apply only
            // ever populates entries for files a FieldChange actually touches.
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
            progress?.Report(new RebuildStageProgress("Staging merged tables and assets…", 25));
            await Task.Run(() =>
            {
                StageMergedTables(mergedTables, originalFileJsonByFile, stagingDirectory);
                StageAssets(queuedMods, stagingDirectory);
            }, cancellationToken);

            if (prebuiltPakFilePaths.Count > 0)
            {
                progress?.Report(new RebuildStageProgress("Folding in prebuilt paks…", 50));
            }

            // Opaque/prebuilt paks have no .EXMOD to field-merge — unpacked straight into the same
            // staging folder instead, so the -Create call below folds their contents into the one
            // final pak rather than producing a separate sidecar file. Confirmed live that
            // UnrealPak -Extract is additive against an already-populated folder, not destructive,
            // so this can safely run after the merge output above without disturbing it. Later
            // paks in the list win on a literal path collision (an actual UnrealPak -Extract
            // overwrite, not application logic) — same policy StageAssets already documents for
            // queued mods' own binary assets.
            //
            // A prebuilt pak carries no field-change data, so if it happens to touch a path the
            // merge output (or an earlier prebuilt pak) already wrote — most plausibly a DataTable
            // JSON another queued mod field-merged — there's no way to reconcile the two; its raw
            // bytes simply win via a literal file overwrite, silently discarding whatever was
            // there. Checked via -List (cheap, read-only) before each -Extract specifically so
            // that loss is a surfaced MergeReport warning instead of a silent one.
            foreach (var prebuiltPakPath in prebuiltPakFilePaths)
            {
                var pakName = Path.GetFileNameWithoutExtension(prebuiltPakPath);
                var pakContents = await unrealPakService.ListPakContentsAsync(unrealPakExePath, prebuiltPakPath, cancellationToken);
                var alreadyStaged = new HashSet<string>(
                    Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(stagingDirectory, f).Replace('\\', '/')),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var path in pakContents)
                {
                    if (alreadyStaged.Contains(path))
                    {
                        report.AddWarning(
                            $"Prebuilt pak '{pakName}' overwrites '{path}', which another queued mod (or an earlier "
                            + $"prebuilt pak) also touches — no field-level merge is possible for a prebuilt pak, so "
                            + $"'{pakName}' silently wins for this one file.");
                    }
                }

                await unrealPakService.ExtractPakAsync(unrealPakExePath, prebuiltPakPath, stagingDirectory, cancellationToken);
            }

            progress?.Report(new RebuildStageProgress("Packing…", 75));
            var packedFileCount = await unrealPakService.CreatePakAsync(unrealPakExePath, stagingDirectory, outputPakPath, cancellationToken);
            var manifestPath = WriteManifest(queuedMods, prebuiltPakFilePaths, outputPakPath);

            progress?.Report(new RebuildStageProgress("Done.", 100));
            return new RebuildResult(mergedTables.Count, packedFileCount, outputPakPath, manifestPath, report.Warnings, report.Notes);
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

            // DuplicateTolerantJson, not a plain JsonNode.Parse: a real base-game DataTable file
            // has been confirmed to contain a duplicate JSON key (a Jimk72-authored file's own
            // "ResourceCostMultipliers" appears twice), which JsonNode.Parse throws on — this is
            // the main Rebuild pipeline, so that would abort a whole Rebuild instead of degrading.
            var fileJson = IcarusStarlink.PakIO.Exmod.DuplicateTolerantJson.Parse(File.ReadAllText(basePath))!.AsObject();
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

    private static string WriteManifest(IReadOnlyList<ExmodPackageContents> queuedMods, IReadOnlyList<string> prebuiltPakFilePaths, string outputPakPath)
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

        // Now genuinely part of this same pak (folded in via ExtractPakAsync above), so they belong
        // in the "what's actually installed" record the same way a queued mod does — GetInstalledStateAsync
        // reads this back for the "Compare to installed" diff, which would otherwise not know they're there.
        foreach (var prebuiltPakPath in prebuiltPakFilePaths)
        {
            text.AppendLine(Path.GetFileNameWithoutExtension(prebuiltPakPath));
        }

        File.WriteAllText(manifestPath, text.ToString());
        return manifestPath;
    }
}
