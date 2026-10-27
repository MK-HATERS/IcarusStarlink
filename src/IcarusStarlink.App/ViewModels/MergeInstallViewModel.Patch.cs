using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Catalog;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Export and Import patch (a profile shared with friends), and a classic IMM mod list loaded as the queue.</summary>
public sealed partial class MergeInstallViewModel
{
    // --- Export/Import patch (6.3; format 2 with sources, paks, UE4SS mods and their settings, 2026-10-27) ---
    [ObservableProperty]
    private bool _isExportingPatch;

    /// <summary>Bound to the Export patch button's own IsEnabled — a real, if slow, network + file
    /// operation (catalog fetch, then a SaveFileDialog), so a fast double-click could otherwise
    /// start two overlapping exports.</summary>
    public bool IsNotExportingPatch => !IsExportingPatch;

    partial void OnIsExportingPatchChanged(bool value) => OnPropertyChanged(nameof(IsNotExportingPatch));

    [ObservableProperty]
    private string? _patchStatusMessage;

    /// <summary>Export carries the UE4SS mods' config files (keys, values) along with the mods. On by default: a friend wants the setup as it runs.</summary>
    [ObservableProperty]
    private bool _exportIncludesConfigs = true;

    /// <summary>The game's UE4SS Mods folder, when the game path is set and the UE4SS services were given.</summary>
    private string? Ue4ssGameModsFolder =>
        _ue4ssStateService is not null && _settingsService.Current.IcarusContentPath is { Length: > 0 } content
            ? Ue4ssGamePaths.ResolveModsFolder(content)
            : null;

    /// <summary>
    /// Exports whatever's currently in Queue (not a fresh reload of the saved profile), matching
    /// SaveProfile's own live-state semantics. A mod that came from Nexus or the community catalog
    /// is referenced by its ids (a friend downloads it themselves); one found in the catalog by
    /// name and author is referenced too; anything else (a local edit, a hand-made mod, a pak
    /// from nowhere) is bundled, since a friend genuinely couldn't get it elsewhere. The UE4SS mods
    /// on in the game come along the same way, with their settings when the box is ticked.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ExportPatchAsync()
    {
        if (IsExportingPatch)
        {
            return;
        }

        if (SelectedProfileName is not { } profileName)
        {
            PatchStatusMessage = "Select or save a profile first.";
            return;
        }

        if (Queue.Count == 0)
        {
            PatchStatusMessage = "Add at least one mod to the queue first.";
            return;
        }

        IsExportingPatch = true;
        PatchStatusMessage = "Checking which mods are in the community catalog…";

        try
        {
            // Isolated per-source, same as Downloads' own catalog refresh — a transient blip in
            // either source shouldn't fail the whole export; a mod from the source that failed
            // just gets treated as "not in catalog" (bundled) instead.
            var failedSources = new List<string>();
            var daedalusTask = CatalogSourceFetch.FetchAsync(_daedalusClient.FetchAsync, "Daedalus", failedSources);
            var jimk72Task = CatalogSourceFetch.FetchAsync(_jimk72Client.FetchAsync, "Jimk72", failedSources);
            await Task.WhenAll(daedalusTask, jimk72Task);
            var catalogKeys = daedalusTask.Result.Concat(jimk72Task.Result)
                .Select(e => CatalogKey.Normalize(e.Name, e.Author))
                .ToHashSet();

            var mods = new List<PatchModEntry>();
            var bundledExmods = new Dictionary<string, ExmodPackageContents>();
            var bundledPaks = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var entry in Queue)
            {
                if (entry.IsMissingFromLibrary)
                {
                    continue;
                }

                var hasSource = entry.NexusModId is not null || entry.CatalogEntryId is not null
                    || catalogKeys.Contains(CatalogKey.Normalize(entry.Name, entry.Author));
                var mod = new PatchModEntry
                {
                    FolderName = entry.FolderName, Name = entry.Name, Author = entry.Author, Version = entry.Version,
                    Bundled = !hasSource, NexusModId = entry.NexusModId, NexusFileId = entry.NexusFileId, CatalogEntryId = entry.CatalogEntryId,
                    IsPak = entry.IsOpaquePak,
                };
                if (entry.IsOpaquePak)
                {
                    var paks = PakFilesOf(entry);
                    mod.PakFileNames = [.. paks.Select(Path.GetFileName)!];
                    if (!hasSource)
                    {
                        bundledPaks[entry.FolderName] = paks;
                    }
                }
                else if (!hasSource)
                {
                    bundledExmods[entry.FolderName] = ExmodFolder.Read(_libraryRepository.GetFolderPath(entry.FolderName));
                }

                mods.Add(mod);
            }

            var (ue4ssMods, bundledUe4ss) = CollectUe4ssForPatch();
            if (mods.Count == 0 && ue4ssMods.Count == 0)
            {
                PatchStatusMessage = "Nothing to export — every queued mod is missing from your Library.";
                return;
            }

            var bundle = new PatchBundle(bundledExmods, bundledPaks, bundledUe4ss);
            var extension = bundle.IsEmpty ? "json" : "zip";
            // The picker blocks until the user responds, so the "Checking…" message above would
            // otherwise sit on screen — stale-looking — for as long as they take to pick a location.
            PatchStatusMessage = "Choose where to save…";
            if (_dialogs.PickSaveFile(
                    "Export patch",
                    extension == "json" ? "IcarusStarlink patch (*.json)|*.json" : "IcarusStarlink patch (*.zip)|*.zip",
                    $"ISL-Patch-{profileName}.{extension}") is not { } exportPath)
            {
                PatchStatusMessage = null;
                return;
            }

            var manifest = new PatchManifest
            {
                FormatVersion = PatchManifest.CurrentFormatVersion,
                ProfileName = profileName,
                ExportedAtUtc = DateTime.UtcNow,
                StarlinkVersion = typeof(MergeInstallViewModel).Assembly.GetName().Version?.ToString(3),
                Mods = mods,
                Options = BuildGameplayOptionsFromUi(),
                Ue4ssMods = ue4ssMods,
                IncludesConfigs = ExportIncludesConfigs && ue4ssMods.Any(m => m.Configs.Count > 0),
            };
            await _patchService.ExportAsync(manifest, bundle, exportPath);

            var bundledCount = bundledExmods.Count + bundledPaks.Count + bundledUe4ss.Count;
            var parts = new List<string> { $"{mods.Count} data mod(s)" };
            if (ue4ssMods.Count > 0)
            {
                parts.Add($"{ue4ssMods.Count} UE4SS mod(s){(manifest.IncludesConfigs ? " with their settings" : "")}");
            }

            PatchStatusMessage = $"Exported '{profileName}' to '{exportPath}' — {string.Join(" and ", parts)}; "
                + (bundledCount > 0
                    ? $"{bundledCount} bundled (nowhere else to get them), the rest referenced by their Nexus or catalog source."
                    : "every one referenced by its Nexus or catalog source, nothing bundled.");

            if (failedSources.Count > 0)
            {
                // Not a failure — a mod from the unreachable source was conservatively treated as
                // "not in catalog" and bundled instead, so the export is still complete and correct,
                // just possibly larger than it needed to be.
                PatchStatusMessage += $" ({string.Join(", ", failedSources)} couldn't be reached — affected mods were bundled rather than referenced.)";
            }

            _activityLog.Log($"Exported '{profileName}' to '{exportPath}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Export failed: {ex.Message}";
            _activityLog.Log(PatchStatusMessage, ActivityEntryKind.Warning);
        }
        finally
        {
            IsExportingPatch = false;
        }
    }

    /// <summary>A pak mod's files: the ones its entry names, else every pak in its folder.</summary>
    private IReadOnlyList<string> PakFilesOf(LibraryEntry entry)
    {
        var folder = _libraryRepository.GetFolderPath(entry.FolderName);
        var named = entry.PakFileNames.Select(n => Path.Combine(folder, n)).Where(File.Exists).ToList();
        return named.Count > 0 ? named : Directory.Exists(folder) ? [.. Directory.GetFiles(folder, "*.pak").Order(StringComparer.OrdinalIgnoreCase)] : [];
    }

    /// <summary>
    /// The UE4SS mods on in the game (the framework's own left out): a Nexus id to reference them
    /// by, else bundled from the game folder; their config files when the box is ticked.
    /// </summary>
    private (List<PatchUe4ssEntry> Mods, Dictionary<string, string> Bundled) CollectUe4ssForPatch()
    {
        var mods = new List<PatchUe4ssEntry>();
        var bundled = new Dictionary<string, string>();
        if (Ue4ssGameModsFolder is not { } modsFolder || _ue4ssStateService is null)
        {
            return (mods, bundled);
        }

        foreach (var state in _ue4ssStateService.GetAll(modsFolder).Where(s => s.IsEnabled && s.IsInGame && !Ue4ssServerFiles.FrameworkMods.Contains(s.Name)))
        {
            var folder = Path.Combine(modsFolder, state.Name);
            var meta = _ue4ssMetaStore?.Load(state.Name);
            var entry = new PatchUe4ssEntry
            {
                Name = state.Name, NexusName = meta?.NexusName, NexusModId = meta?.NexusModId, NexusFileId = meta?.NexusFileId,
                Version = Ue4ssModVersion.TryRead(folder), Enabled = true, Bundled = meta?.NexusModId is null,
            };
            if (entry.Bundled)
            {
                bundled[state.Name] = folder;
            }

            if (ExportIncludesConfigs)
            {
                foreach (var config in Ue4ssModConfigFiles.Find(folder).Where(c => !c.IsDefaults))
                {
                    try
                    {
                        entry.Configs.Add(new PatchConfigFile { Path = config.RelativePath.Replace('\\', '/'), Text = File.ReadAllText(Path.Combine(folder, config.RelativePath)) });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A config that can't be read right now is left out; the mod still travels.
                    }
                }
            }

            mods.Add(entry);
        }

        return (mods, bundled);
    }

    /// <summary>
    /// Loads a classic-IMM merge list (LastMergedMods.txt / IMM_Merged_Mod.txt — or this app's own
    /// ISL-Merged.txt, same format) and rebuilds the queue from it, in the list's own order (merge
    /// priority). REPLACES the current queue, matching classic IMM's own "Load Merge list"
    /// semantics — the file IS the intended list, not an addition to whatever happened to be
    /// queued. Names that don't match any Library entry land in the Warnings list rather than
    /// being silently dropped, so the user can resolve them (import the missing mod, or Rename/
    /// Link an existing one) and re-import.
    ///
    /// Public and called only by Settings' own classic-IMM migration (which finishes the job it
    /// starts: bringing the mods over is only half a migration — the user's actual merge list, in
    /// its real order, is the other half) — this page no longer has its own separate file-dialog
    /// entry point for it (removed as a redundant-looking near-duplicate of the Settings flow).
    /// </summary>
    public void LoadModListFromFile(string modListPath)
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        try
        {
            var names = ModListText.ParseNames(File.ReadAllText(modListPath));
            if (names.Count == 0)
            {
                StatusMessage = "That file has no mod names in it.";
                return;
            }

            var result = ModListMatcher.Match(names, _libraryRepository.GetAll());

            Queue.Clear();
            foreach (var entry in result.Matched.Where(e => !IsIslBuiltPack(e)))
            {
                Queue.Add(entry);
            }

            Warnings.Clear();
            foreach (var name in result.Unmatched)
            {
                Warnings.Add($"No Library match for '{name}' — import it first, then re-import this list.");
            }

            foreach (var pack in result.Matched.Where(IsIslBuiltPack))
            {
                Warnings.Add($"'{pack.Name}' is a pack Merge & Install built, so it wasn't ticked — tick the mods it was built from instead.");
            }

            StatusMessage = result.Unmatched.Count == 0
                ? $"Queued all {result.Matched.Count} mod(s) from the list."
                : $"Queued {result.Matched.Count} of {names.Count} — {result.Unmatched.Count} not in your Library yet (see warnings below).";
            _activityLog.Log($"Imported IMM mod list — {result.Matched.Count} of {names.Count} matched.",
                result.Unmatched.Count == 0 ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
        }
        catch (Exception ex)
        {
            // Same UI boundary as ImportPatchAsync below — an unreadable/locked file shows a
            // status message rather than crashing the app.
            StatusMessage = $"Couldn't import the list: {ex.Message}";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// "Import + Sync reinstalls listed mods (force refresh; does not skip same version)" per the
    /// spec — a bundled mod always overwrites whatever's already in the Library under the same
    /// folder name, rather than skipping it as "already have it", since the whole point is syncing
    /// exactly what the patch carries. A referenced mod is matched by its Nexus id, then its
    /// catalog id, then (Name, Author) against the local Library; if it's missing, this reports
    /// it rather than failing the whole import — same SkipWithWarning philosophy as everywhere else
    /// the merge pipeline can hit a gap. A bundled UE4SS mod lands in staging; a referenced one is
    /// looked for among the game's and the staged ones; either gets the patch's settings written
    /// into its own config (the local file is backed up first). ImportAsync itself doesn't need
    /// the network (unlike Export), but this still awaits it properly rather than blocking the UI
    /// thread on a zip read/extract that can take real time for a patch with bundled content.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportPatchAsync()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (_dialogs.PickFile("Import a patch", "IcarusStarlink patch (*.json;*.zip)|*.json;*.zip") is not { } patchPath)
        {
            return;
        }

        try
        {
            using var contents = await _patchService.ImportAsync(patchPath);
            var resolvedFolderNames = new List<string>();
            var missing = new List<string>();

            foreach (var mod in contents.Manifest.Mods)
            {
                if (mod.Bundled && mod.IsPak && contents.BundledPakFolders.TryGetValue(mod.FolderName, out var pakFolder))
                {
                    resolvedFolderNames.Add(ImportBundledPak(mod, pakFolder));
                    continue;
                }

                if (mod.Bundled && contents.BundledMods.TryGetValue(mod.FolderName, out var exmod))
                {
                    resolvedFolderNames.Add(ImportBundledMod(exmod));
                    continue;
                }

                if (FindLibraryMatch(mod) is { } existing)
                {
                    resolvedFolderNames.Add(existing.FolderName);
                }
                else
                {
                    missing.Add($"{mod.Name} by {mod.Author}{(mod.NexusModId is { } id ? $" (Nexus #{id})" : "")}");
                }
            }

            var ue4ss = ImportUe4ssFromPatch(contents);

            // A pending auto-save of the profile in use is written first: written after the import,
            // it put the old queue back over a patch imported under the same profile name.
            SaveWorkingState();
            _profileStore.Save(new Profile { Name = contents.Manifest.ProfileName, MergeQueueFolderNames = resolvedFolderNames, Options = contents.Manifest.Options });
            _messenger.Send(new LibraryChangedMessage());
            var importedName = contents.Manifest.ProfileName;
            if (string.Equals(SelectedProfileName, importedName, StringComparison.OrdinalIgnoreCase))
            {
                // Already selected, so setting it again changes nothing: load it here, or the page
                // kept showing (and the next auto-save wrote back) the queue from before the import.
                _loadingProfile = true;
                try
                {
                    LoadProfileIntoUi(SelectedProfileName!, verb: "Imported");
                }
                finally
                {
                    _loadingProfile = false;
                }
            }
            else
            {
                ReloadProfileNames();
                // Triggers OnSelectedProfileNameChanged, which populates Queue from the just-saved
                // profile and reports its own "N mod(s) no longer in your Library" count — reused as-is
                // rather than duplicating that population logic here.
                SelectedProfileName = ProfileNames.FirstOrDefault(n => string.Equals(n, importedName, StringComparison.OrdinalIgnoreCase)) ?? importedName;
            }

            var summary = new List<string>();
            if (missing.Count > 0)
            {
                summary.Add($"{missing.Count} mod(s) missing, get them from the Downloads page first: {string.Join(", ", missing)}");
            }

            summary.AddRange(ue4ss);
            PatchStatusMessage = summary.Count == 0
                ? $"Imported '{contents.Manifest.ProfileName}'."
                : $"Imported '{contents.Manifest.ProfileName}' — {string.Join(". ", summary)}.";
            _activityLog.Log(PatchStatusMessage, missing.Count > 0 ? ActivityEntryKind.Warning : ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log(PatchStatusMessage, ActivityEntryKind.Warning);
        }
    }

    /// <summary>The Library copy a referenced mod stands for: the same Nexus mod, else the same catalog entry, else the same name and author (a format 1 patch has only that).</summary>
    private LibraryEntry? FindLibraryMatch(PatchModEntry mod)
    {
        var entries = _libraryRepository.GetAll();
        if (mod.NexusModId is { } nexusId && entries.FirstOrDefault(e => e.NexusModId == nexusId) is { } byNexus)
        {
            return byNexus;
        }

        if (mod.CatalogEntryId is { Length: > 0 } catalogId
            && entries.FirstOrDefault(e => string.Equals(e.CatalogEntryId, catalogId, StringComparison.OrdinalIgnoreCase)) is { } byCatalog)
        {
            return byCatalog;
        }

        var modKey = CatalogKey.Normalize(mod.Name, mod.Author);
        return entries.FirstOrDefault(e => CatalogKey.Normalize(e.Name, e.Author) == modKey);
    }

    private string ImportBundledMod(ExmodPackageContents contents)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.EXMODZ");
        try
        {
            ExmodzArchive.Write(tempPath, contents);

            var existing = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, contents.Package.FileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _libraryRepository.Delete(existing.FolderName);
            }

            return _libraryRepository.Import(tempPath).FolderName;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>A bundled pak mod: its pak files (in the order the patch names them) become one Library mod, replacing a same-named copy.</summary>
    private string ImportBundledPak(PatchModEntry mod, string pakFolder)
    {
        var onDisk = Directory.GetFiles(pakFolder, "*.pak");
        var ordered = mod.PakFileNames
            .Select(n => onDisk.FirstOrDefault(f => string.Equals(Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Concat(onDisk.Where(f => !mod.PakFileNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (ordered.Count == 0)
        {
            throw new FormatException($"The patch's bundled pak mod '{mod.Name}' has no pak files.");
        }

        var existing = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, mod.FolderName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _libraryRepository.Delete(existing.FolderName);
        }

        return _libraryRepository.ImportPaks(ordered, mod.Name, source: "Patch", nexusModId: mod.NexusModId, catalogEntryId: mod.CatalogEntryId).FolderName;
    }

    /// <summary>
    /// The patch's UE4SS mods: a bundled one goes into staging (replacing a same-named staged copy;
    /// one already in the game is kept and only gets the settings), a referenced one is looked for
    /// by name or Nexus id among the game's and the staged mods. Each found mod gets the patch's
    /// config files written into its own (the local file backed up first). Returns the lines for
    /// the status message.
    /// </summary>
    private List<string> ImportUe4ssFromPatch(PatchImportContents contents)
    {
        var lines = new List<string>();
        var wanted = contents.Manifest.Ue4ssMods;
        if (wanted.Count == 0)
        {
            return lines;
        }

        if (_ue4ssStateService is null || _ue4ssRepository is null || _ue4ssMetaStore is null)
        {
            lines.Add($"{wanted.Count} UE4SS mod(s) in the patch were skipped (UE4SS isn't set up here)");
            return lines;
        }

        var modsFolder = Ue4ssGameModsFolder ?? "";
        var states = _ue4ssStateService.GetAll(modsFolder).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var settingsApplied = 0;
        var landed = 0;
        var notTurnedOn = new List<string>();
        var configBackups = Path.Combine(_backupDirectory, "UE4SS-Mod-Configs");
        foreach (var mod in wanted)
        {
            var name = FindUe4ssMatch(mod, states.Values);
            if (name is null && mod.Bundled && contents.BundledUe4ssFolders.TryGetValue(mod.Name, out var bundledFolder))
            {
                var namesInGame = _ue4ssRepository.ListInstalledInGame(modsFolder);
                name = _ue4ssRepository.ImportFromFolder(Path.GetDirectoryName(bundledFolder)!, mod.Name, namesInGame);
                var meta = _ue4ssMetaStore.Load(name);
                meta.NexusModId ??= mod.NexusModId;
                meta.NexusFileId ??= mod.NexusFileId;
                meta.NexusName ??= mod.NexusName;
                _ue4ssMetaStore.Save(name, meta);
                states[name] = new Ue4ssModState(name, IsEnabled: false);
                landed++;
            }

            if (name is null)
            {
                missing.Add(mod.NexusName ?? mod.Name);
                continue;
            }

            var state = states[name];
            var folder = state.IsInGame ? Path.Combine(modsFolder, name) : _ue4ssRepository.GetFolderPath(name);
            foreach (var config in mod.Configs)
            {
                var relative = config.Path.Replace('/', Path.DirectorySeparatorChar);
                if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    continue; // a config path that leaves the mod folder isn't one
                }

                Ue4ssModConfigCarryOver.ApplyValues(folder, relative, config.Text, configBackups);
                settingsApplied++;
            }

            if (!state.IsEnabled)
            {
                notTurnedOn.Add(name);
            }
        }

        if (landed > 0)
        {
            lines.Add($"{landed} UE4SS mod(s) added to the Library");
        }

        if (settingsApplied > 0)
        {
            lines.Add($"settings written for {wanted.Count - missing.Count} UE4SS mod(s) (your old files are in Backups\\UE4SS-Mod-Configs)");
        }

        if (notTurnedOn.Count > 0)
        {
            lines.Add($"tick {string.Join(", ", notTurnedOn)} in the Library's UE4SS list and press Apply to turn them on");
        }

        if (missing.Count > 0)
        {
            lines.Add($"{missing.Count} UE4SS mod(s) missing, get them from Nexus first: {string.Join(", ", missing)}");
        }

        return lines;
    }

    /// <summary>The local UE4SS mod a patch entry stands for: the same folder name, else the same Nexus mod.</summary>
    private string? FindUe4ssMatch(PatchUe4ssEntry mod, IEnumerable<Ue4ssModState> states)
    {
        var byName = states.FirstOrDefault(s => string.Equals(s.Name, mod.Name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName.Name;
        }

        if (mod.NexusModId is { } nexusId && _ue4ssMetaStore is not null)
        {
            return states.FirstOrDefault(s => _ue4ssMetaStore.Load(s.Name).NexusModId == nexusId)?.Name;
        }

        return null;
    }
}
