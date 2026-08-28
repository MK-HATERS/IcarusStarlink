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
using IcarusStarlink.PakIO.Safety;

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

        // Each prebuilt/opaque pak is extracted to its own scratch folder exactly once, up front —
        // reused below both to diff its own DataTable JSON against real base data (so it can become
        // a genuine field-level MergeEngine participant instead of unconditionally overwriting
        // whatever file it collides with) and, later, to copy through whatever isn't
        // field-mergeable. Extracting via UnrealPak is real async process I/O, so it has to happen
        // here, before the synchronous merge computation below.
        var prebuiltPakScratchDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prebuiltPakPath in prebuiltPakFilePaths)
        {
            var prebuiltScratchDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"PrebuiltPakScratch_{Guid.NewGuid():N}");
            await unrealPakService.ExtractPakAsync(unrealPakExePath, prebuiltPakPath, prebuiltScratchDirectory, cancellationToken);
            prebuiltPakScratchDirectories[prebuiltPakPath] = prebuiltScratchDirectory;
        }

        try
        {
            var prebuiltPakDiffedPaths = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

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

                // A prebuilt pak carries no EXMOD-style declared changes, so its own DataTable JSON
                // is reverse-engineered into real FieldChanges by diffing it against current base
                // game data — the same technique ExmodBaseDiffer/PakCompareService/ModVersionComparer
                // already use elsewhere for "diff against base"/"diff pak vs pak". Appended after
                // EXMOD mods' own changes: this preserves the app's prior "a prebuilt pak always
                // wins on collision" precedent (previously an unconditional whole-file overwrite;
                // now a genuine field-level last-mod-wins, still overridable via manualPicks)
                // rather than silently changing which side wins a conflict.
                foreach (var prebuiltPakPath in prebuiltPakFilePaths)
                {
                    var pakName = Path.GetFileNameWithoutExtension(prebuiltPakPath);
                    var (changes, diffedPaths) = PrebuiltPakFieldChangeExtractor.Extract(
                        prebuiltPakScratchDirectories[prebuiltPakPath], dataFolder, pakName, classifier, report);
                    prebuiltPakDiffedPaths[prebuiltPakPath] = diffedPaths;
                    if (changes.Count > 0)
                    {
                        orderedModChanges.Add(changes);
                    }
                }

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

                // Read before merging (against the raw, pre-merge file set — a strict superset of
                // whatever survives resolution, since resolving only ever picks among a field's
                // existing candidates, never introduces a new file) so Merge can compare each
                // candidate against its real current base value — see MergeEngine.Merge's own doc
                // comment for why that matters (dropping whole-row-copy artifacts that would
                // otherwise out-rank a genuine edit purely by queue position).
                var requiredFiles = orderedModChanges.SelectMany(c => c).Select(c => c.CurrentFile)
                    .Concat(GameplayOptionsApplier.RequiredCurrentFiles(gameplayOptions))
                    .Distinct();
                var (baseTablesByFile, originalJson) = ReadBaseTables(requiredFiles, dataFolder, report);

                var resolvedChanges = MergeEngine.Merge(orderedModChanges, new MergeRuleRegistry(), manualPicks, baseTablesByFile);
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
                    StageMergedTables(mergedTables, originalFileJsonByFile, stagingDirectory, report);
                    StageAssets(queuedMods, stagingDirectory);
                }, cancellationToken);

                if (prebuiltPakFilePaths.Count > 0)
                {
                    progress?.Report(new RebuildStageProgress("Folding in prebuilt paks…", 50));
                }

                // Whatever wasn't field-mergeable above (binary assets, or a data/*.json file with no
                // matching base table at all) still needs to land in the final pak — copied through
                // from the same scratch extraction taken up front, so nothing is extracted twice. A
                // file the field-level merge above already produced is deliberately skipped here:
                // copying the prebuilt pak's own raw, unmerged copy of it back in would silently undo
                // that merge. Later paks in the list still win on a literal remaining collision (a
                // real file overwrite, not application logic) — same policy StageAssets already
                // documents for queued mods' own binary assets — surfaced as a MergeReport warning
                // rather than a silent one.
                foreach (var prebuiltPakPath in prebuiltPakFilePaths)
                {
                    var pakName = Path.GetFileNameWithoutExtension(prebuiltPakPath);
                    var prebuiltScratchDirectory = prebuiltPakScratchDirectories[prebuiltPakPath];
                    var diffedPaths = prebuiltPakDiffedPaths[prebuiltPakPath];

                    var alreadyStaged = new HashSet<string>(
                        Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                            .Select(f => Path.GetRelativePath(stagingDirectory, f).Replace('\\', '/')),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var sourceFile in Directory.GetFiles(prebuiltScratchDirectory, "*", SearchOption.AllDirectories))
                    {
                        var relativePath = Path.GetRelativePath(prebuiltScratchDirectory, sourceFile).Replace('\\', '/');
                        if (diffedPaths.Contains(relativePath))
                        {
                            continue;
                        }

                        if (alreadyStaged.Contains(relativePath))
                        {
                            report.AddWarning(
                                $"Prebuilt pak '{pakName}' overwrites '{relativePath}', which another queued mod (or "
                                + $"an earlier prebuilt pak) also touches and isn't a field-mergeable DataTable file "
                                + $"— '{pakName}' wins for this one file.");
                        }

                        var destPath = Path.Combine(stagingDirectory, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        File.Copy(sourceFile, destPath, overwrite: true);
                    }
                }

                progress?.Report(new RebuildStageProgress("Packing…", 75));
                var stagedRelativePaths = Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(stagingDirectory, f).Replace('\\', '/'))
                    .ToList();
                var packedFileCount = await unrealPakService.CreatePakAsync(unrealPakExePath, stagingDirectory, outputPakPath, cancellationToken);
                await VerifyEveryStagedFileWasActuallyPackedAsync(unrealPakExePath, outputPakPath, stagedRelativePaths, report, cancellationToken);
                var manifestPath = WriteManifest(queuedMods, prebuiltPakFilePaths, outputPakPath);

                progress?.Report(new RebuildStageProgress("Done.", 100));
                return new RebuildResult(mergedTables.Count, packedFileCount, outputPakPath, manifestPath, report.Warnings, report.Notes);
            }
            finally
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        finally
        {
            foreach (var prebuiltScratchDirectory in prebuiltPakScratchDirectories.Values)
            {
                if (Directory.Exists(prebuiltScratchDirectory))
                {
                    Directory.Delete(prebuiltScratchDirectory, recursive: true);
                }
            }
        }
    }

    public async Task<IReadOnlyList<(string PakName, IReadOnlyList<FieldChange> Changes)>> ComputePrebuiltPakFieldChangesAsync(
        IReadOnlyList<string> prebuiltPakFilePaths, string dataFolder, string unrealPakExePath, MergeReport report,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(string PakName, IReadOnlyList<FieldChange> Changes)>();
        var classifier = new DefaultSemanticClassifier();

        foreach (var prebuiltPakPath in prebuiltPakFilePaths)
        {
            var scratchDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"PrebuiltPakScratch_{Guid.NewGuid():N}");
            try
            {
                await unrealPakService.ExtractPakAsync(unrealPakExePath, prebuiltPakPath, scratchDirectory, cancellationToken);
                var pakName = Path.GetFileNameWithoutExtension(prebuiltPakPath);
                var (changes, _) = await Task.Run(
                    () => PrebuiltPakFieldChangeExtractor.Extract(scratchDirectory, dataFolder, pakName, classifier, report), cancellationToken);
                if (changes.Count > 0)
                {
                    results.Add((pakName, changes));
                }
            }
            finally
            {
                if (Directory.Exists(scratchDirectory))
                {
                    Directory.Delete(scratchDirectory, recursive: true);
                }
            }
        }

        return results;
    }

    public IReadOnlyDictionary<string, JsonObject> ReadKeyedBaseTables(IEnumerable<string> currentFiles, string dataFolder, MergeReport report) =>
        ReadBaseTables(currentFiles, dataFolder, report).Keyed;

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

            // CurrentFile is untrusted EXMOD content (an attacker-shared or downloaded mod), not
            // just an internal identifier — without this guard, a rooted or ".."-laden CurrentFile
            // would let Path.Combine below read an arbitrary file anywhere the app process can
            // access, via nothing more than the completely ordinary "queue a mod" workflow.
            string basePath;
            try
            {
                basePath = AssetPathGuard.ResolveWithinDirectory(dataFolder, realRelativePath);
            }
            catch (FormatException)
            {
                report.AddWarning(
                    $"Skipped '{currentFile}' — its path isn't a valid location inside the extracted game data.");
                continue;
            }

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
        IReadOnlyDictionary<string, JsonObject> mergedTables, IReadOnlyDictionary<string, JsonObject> originalFileJsonByFile, string stagingDirectory,
        MergeReport report)
    {
        var dataDirectory = Path.Combine(stagingDirectory, "data");

        foreach (var (currentFile, mergedKeyedTable) in mergedTables)
        {
            var realRelativePath = currentFile.Replace('-', '/');

            // Same untrusted-CurrentFile concern as ReadBaseTables above, mirrored on the write
            // side — without this, a rooted or ".."-laden CurrentFile would let this silently
            // overwrite an attacker-chosen file anywhere the app process can write, reachable via
            // the same ordinary "queue a mod, click Rebuild" workflow.
            string destPath;
            try
            {
                destPath = AssetPathGuard.ResolveWithinDirectory(dataDirectory, realRelativePath);
            }
            catch (FormatException)
            {
                report.AddWarning(
                    $"Skipped writing '{currentFile}' — its path isn't a valid location inside the staged output.");
                continue;
            }

            var fullFile = DataTableJson.KeyedObjectToRows(originalFileJsonByFile[currentFile], mergedKeyedTable);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.WriteAllText(destPath, fullFile.ToJsonString(JsonWriteOptions));
        }
    }

    /// <summary>
    /// Independently confirms every file this app just staged actually ended up retrievable from
    /// the pak it just built — reading the real pak's own contents back via -List, not trusting
    /// CreatePakAsync's own returned count (that number only ever reflects how many files were
    /// staged and handed to UnrealPak.exe, never how many it can be shown to actually contain
    /// afterward — CreatePakAsync has no way to know if UnrealPak silently dropped one). This is
    /// the "did the merge lose anything" question asked directly of the one artifact that actually
    /// matters — the finished pak — rather than only of the merge computation that led up to it.
    /// A missing UnrealPak.exe/pak read failure here surfaces as its own warning rather than
    /// throwing — a failed verification pass must never make an otherwise-successful Rebuild look
    /// like it failed outright.
    /// </summary>
    private async Task VerifyEveryStagedFileWasActuallyPackedAsync(
        string unrealPakExePath, string outputPakPath, IReadOnlyList<string> stagedRelativePaths, MergeReport report, CancellationToken cancellationToken)
    {
        try
        {
            var packedPaths = await unrealPakService.ListPakContentsAsync(unrealPakExePath, outputPakPath, cancellationToken);
            var packedPathSet = new HashSet<string>(packedPaths, StringComparer.OrdinalIgnoreCase);
            var missing = stagedRelativePaths.Where(p => !packedPathSet.Contains(p)).ToList();
            if (missing.Count > 0)
            {
                var shown = string.Join(", ", missing.Take(5));
                var suffix = missing.Count > 5 ? ", ..." : "";
                report.AddWarning(
                    $"{missing.Count} file(s) were staged for packing but didn't make it into the final pak "
                    + $"(UnrealPak.exe itself dropped them, not this app's own merge): {shown}{suffix}.");
            }
        }
        catch (Exception ex)
        {
            report.AddWarning($"Couldn't independently verify the pak's own contents after building it: {ex.Message}");
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
