using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
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
public sealed partial class MergeInstallViewModel : IPatchImporter
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

            // The UE4SS mods on in the game (the framework's own left out).
            var ue4ssMods = Ue4ssGameModsFolder is { } modsFolder ? PatchBuilder.Ue4ssModsOnIn(modsFolder) : [];
            var (manifest, bundle, skipped) = new PatchBuilder(_libraryRepository, _ue4ssMetaStore)
                .Build(profileName, [.. Queue], BuildGameplayOptionsFromUi(), ue4ssMods, ExportIncludesConfigs, catalogKeys);
            if (manifest.Mods.Count == 0 && manifest.Ue4ssMods.Count == 0)
            {
                PatchStatusMessage = "Nothing to export — every queued mod is missing from your Library.";
                return;
            }

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

            await _patchService.ExportAsync(manifest, bundle, exportPath);

            var bundledCount = bundle.Exmods.Count + bundle.PakFolders.Count + bundle.Ue4ssFolders.Count;
            var parts = new List<string> { $"{manifest.Mods.Count} data mod(s)" };
            if (manifest.Ue4ssMods.Count > 0)
            {
                parts.Add($"{manifest.Ue4ssMods.Count} UE4SS mod(s){(manifest.IncludesConfigs ? " with their settings" : "")}");
            }

            PatchStatusMessage = $"Exported '{profileName}' to '{exportPath}' — {string.Join(" and ", parts)}; "
                + (bundledCount > 0
                    ? $"{bundledCount} bundled (nowhere else to get them), the rest referenced by their Nexus or catalog source."
                    : "every one referenced by its Nexus or catalog source, nothing bundled.");

            if (skipped.Count > 0)
            {
                PatchStatusMessage += $" Left out (no source and no pak files to bundle): {string.Join(", ", skipped)}.";
            }

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

    /// <summary>A mod the patch names that isn't in the Library: what it's called and where it could come from.</summary>
    private sealed record MissingPatchMod(string Label, int? NexusModId, string? CatalogEntryId)
    {
        public bool CanFetch(IPatchModFetcher fetcher) => (NexusModId is not null && fetcher.CanFetchNexus) || CatalogEntryId is not null;
    }

    /// <summary>What ImportUe4ssFromPatch found and did.</summary>
    private sealed record Ue4ssImportOutcome(List<string> Lines, List<string> ToTurnOn, List<MissingPatchMod> Missing);

    /// <summary>
    /// "Import + Sync reinstalls listed mods (force refresh; does not skip same version)" per the
    /// spec — a bundled mod always overwrites whatever's already in the Library under the same
    /// folder name, rather than skipping it as "already have it", since the whole point is syncing
    /// exactly what the patch carries. A referenced mod is matched by its Nexus id, then its
    /// catalog id, then (Name, Author) against the local Library; the ones that are missing are
    /// offered as one download (through the Downloads page's own Nexus and Database pipelines),
    /// and what still isn't there is reported rather than failing the whole import — same
    /// SkipWithWarning philosophy as everywhere else the merge pipeline can hit a gap. A copy
    /// whose version isn't the patch's is kept and noted in the warnings. A bundled UE4SS mod
    /// lands in staging; a referenced one is looked for among the game's and the staged ones;
    /// either gets the patch's settings written into its own config (the local file is backed up
    /// first), and the ones not on in the game are offered to be turned on (the game closed).
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
            await ImportPatchContentsAsync(contents);
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log(PatchStatusMessage, ActivityEntryKind.Warning);
        }
    }

    /// <summary>The Library side of an import, for this page's own Import patch and the Server page's "Apply a patch to this server" (IPatchImporter).</summary>
    public async Task<string?> ImportPatchContentsAsync(PatchImportContents contents)
    {
        if (RefuseWhileRebuilding())
        {
            return null;
        }

        try
        {
            var manifest = contents.Manifest;
            var versionNotes = new List<string>();
            // The bundled mods this import put in the Library, by the patch's folder name: the
            // second pass (after downloads) finds them instead of importing them again.
            var imported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var replaced = new List<string>();
            var (resolvedFolderNames, missing) = ResolvePatchMods(contents, versionNotes, imported, replaced);
            var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ue4ss = ImportUe4ssFromPatch(contents, configured, versionNotes);
            var downloaded = 0;
            var notFetched = new List<string>();

            // One question for everything the patch names that isn't here yet and can be fetched.
            var fetchable = missing.Concat(ue4ss.Missing).Where(m => _patchFetcher is not null && m.CanFetch(_patchFetcher)).ToList();
            if (fetchable.Count > 0 && _patchFetcher is { } fetcher
                && _dialogs.Confirm(
                    $"{fetchable.Count} mod(s) in '{manifest.ProfileName}' aren't in your Library:\n\n{string.Join("\n", fetchable.Select(m => $"• {m.Label}"))}\n\n"
                    + "Download them now? Each comes through the Downloads page from Nexus or the IMM Database and is added to your Library.",
                    "Get the patch's mods", ThemedConfirmSeverity.Question))
            {
                foreach (var mod in fetchable)
                {
                    try
                    {
                        if (mod.NexusModId is { } nexusId && fetcher.CanFetchNexus)
                        {
                            await fetcher.FetchNexusAsync(nexusId, mod.Label);
                        }
                        else if (mod.CatalogEntryId is { } catalogId && !await fetcher.FetchCatalogAsync(catalogId, mod.Label))
                        {
                            notFetched.Add($"{mod.Label} (no longer in the IMM Database)");
                        }
                    }
                    catch (Exception ex)
                    {
                        notFetched.Add($"{mod.Label} ({ex.Message})");
                    }
                }

                // What landed is matched the same way again; the UE4SS mods that just arrived get their settings now.
                var before = missing.Count + ue4ss.Missing.Count;
                (resolvedFolderNames, missing) = ResolvePatchMods(contents, versionNotes: null, imported, replaced);
                var again = ImportUe4ssFromPatch(contents, configured, versionNotes: null);
                ue4ss = new Ue4ssImportOutcome(
                    [.. ue4ss.Lines.Concat(again.Lines).Distinct(StringComparer.Ordinal)],
                    [.. ue4ss.ToTurnOn.Concat(again.ToTurnOn).Distinct(StringComparer.OrdinalIgnoreCase)],
                    again.Missing);
                downloaded = before - (missing.Count + ue4ss.Missing.Count);
            }

            // A pending auto-save of the profile in use is written first: written after the import,
            // it put the old queue back over a patch imported under the same profile name.
            SaveWorkingState();
            _profileStore.Save(new Profile { Name = manifest.ProfileName, MergeQueueFolderNames = resolvedFolderNames, Options = manifest.Options });
            _messenger.Send(new LibraryChangedMessage());
            var importedName = manifest.ProfileName;
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

            // The copies whose version isn't the patch's: kept, listed where the build warnings go.
            Warnings.Clear();
            foreach (var note in versionNotes)
            {
                Warnings.Add(note);
            }

            var turnedOn = await OfferToTurnOnUe4ssAsync(ue4ss.ToTurnOn);

            var summary = new List<string>();
            if (downloaded > 0)
            {
                summary.Add($"{downloaded} mod(s) downloaded");
            }

            var stillMissing = missing.Select(m => m.Label).Concat(ue4ss.Missing.Select(m => m.Label)).ToList();
            if (stillMissing.Count > 0)
            {
                summary.Add($"{stillMissing.Count} mod(s) missing, get them from the Downloads page first: {string.Join(", ", stillMissing)}");
            }

            if (notFetched.Count > 0)
            {
                summary.Add($"couldn't download {string.Join(", ", notFetched)}");
            }

            if (versionNotes.Count > 0)
            {
                summary.Add($"{versionNotes.Count} mod(s) are a different version from the patch's (see the warnings below)");
            }

            if (replaced.Count > 0)
            {
                summary.Add($"your copy of {string.Join(", ", replaced)} was replaced by the patch's (backed up first: Restore latest backup in the Library brings yours back)");
            }

            summary.AddRange(ue4ss.Lines);
            summary.AddRange(turnedOn);
            PatchStatusMessage = summary.Count == 0
                ? $"Imported '{manifest.ProfileName}'."
                : $"Imported '{manifest.ProfileName}' — {string.Join(". ", summary)}.";
            _activityLog.Log(PatchStatusMessage, stillMissing.Count > 0 || notFetched.Count > 0 ? ActivityEntryKind.Warning : ActivityEntryKind.Success);
            return PatchStatusMessage;
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log(PatchStatusMessage, ActivityEntryKind.Warning);
            return PatchStatusMessage;
        }
    }

    /// <summary>
    /// The patch's data mods against the Library: a bundled one is imported (replacing a same-named
    /// copy), a referenced one matched; the queue's folder names in the patch's order, and what's
    /// missing. <paramref name="versionNotes"/> (when given) collects the matches whose version
    /// differs from the patch's. A second call (after downloads) doesn't import the bundled
    /// mods again: it finds the copies the first call made.
    /// </summary>
    private (List<string> Resolved, List<MissingPatchMod> Missing) ResolvePatchMods(
        PatchImportContents contents, List<string>? versionNotes, Dictionary<string, string> imported, List<string> replaced)
    {
        var resolved = new List<string>();
        var missing = new List<MissingPatchMod>();
        foreach (var mod in contents.Manifest.Mods)
        {
            if (mod.Bundled && mod.IsPak && contents.BundledPakFolders.TryGetValue(mod.FolderName, out var pakFolder))
            {
                resolved.Add(imported.TryGetValue(mod.FolderName, out var pakCopy) ? pakCopy : imported[mod.FolderName] = ImportBundledPak(mod, pakFolder, replaced));
                continue;
            }

            if (mod.Bundled && contents.BundledMods.TryGetValue(mod.FolderName, out var exmod))
            {
                resolved.Add(imported.TryGetValue(mod.FolderName, out var exmodCopy) ? exmodCopy : imported[mod.FolderName] = ImportBundledMod(exmod, replaced));
                continue;
            }

            if (FindLibraryMatch(mod) is { } existing)
            {
                resolved.Add(existing.FolderName);
                if (versionNotes is not null && !UpdateVersionComparer.AreEquivalent(existing.Version, mod.Version))
                {
                    versionNotes.Add($"'{existing.Name}' is {existing.Version} in your Library; the patch was made with {mod.Version}. Get update in the Library if you want to match it.");
                }
            }
            else
            {
                missing.Add(new MissingPatchMod(
                    $"{mod.Name} by {mod.Author}{(mod.NexusModId is { } id ? $" (Nexus #{id})" : mod.CatalogEntryId is not null ? " (IMM Database)" : "")}",
                    mod.NexusModId, mod.CatalogEntryId));
            }
        }

        return (resolved, missing);
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

    private string ImportBundledMod(ExmodPackageContents contents, List<string> replaced)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.EXMODZ");
        try
        {
            ExmodzArchive.Write(tempPath, contents);

            // Only an EXMOD mod is the same mod: the patch's file name can't take a pak mod's place.
            var existing = _libraryRepository.GetAll().FirstOrDefault(e => !e.IsOpaquePak && string.Equals(e.FolderName, contents.Package.FileName, StringComparison.OrdinalIgnoreCase));
            return ReplaceForPatch(existing, replaced, () => _libraryRepository.Import(tempPath).FolderName);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// A bundled mod takes the place of the Library copy under the same folder name. That copy can be
    /// the player's own (a hand edit, notes, a Nexus link): it was deleted with no backup, before
    /// the import that could still fail. Now it's backed up first (no backup, no replace), its
    /// Library info kept, and put back when the import fails.
    /// </summary>
    private string ReplaceForPatch(LibraryEntry? existing, List<string> replaced, Func<string> import)
    {
        if (existing is null)
        {
            return import();
        }

        try
        {
            _libraryRepository.BackupMod(existing.FolderName);
        }
        catch (Exception ex)
        {
            throw new IOException($"couldn't back up your copy of '{existing.Name}', so it wasn't replaced: {ex.Message}", ex);
        }

        string? snapshot = null;
        try
        {
            snapshot = _libraryRepository.ReadMetaSnapshot(existing.FolderName);
        }
        catch (Exception)
        {
            // Without it the restore below still brings the folder back.
        }

        _libraryRepository.Delete(existing.FolderName);
        try
        {
            var folderName = import();
            replaced.Add($"'{existing.Name}'");
            return folderName;
        }
        catch (Exception)
        {
            try
            {
                if (_libraryRepository.RestoreLatestModBackup(existing.FolderName) && snapshot is not null)
                {
                    _libraryRepository.WriteMetaSnapshot(existing.FolderName, snapshot);
                }
            }
            catch (Exception restoreEx)
            {
                Serilog.Log.Warning(restoreEx, "Couldn't put '{Folder}' back after a failed patch import", existing.FolderName);
            }

            throw;
        }
    }

    /// <summary>A bundled pak mod: its pak files (in the order the patch names them) become one Library mod, replacing a same-named copy.</summary>
    private string ImportBundledPak(PatchModEntry mod, string pakFolder, List<string> replaced)
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

        // Only a pak mod is the same mod: the patch's folder name can't take an EXMOD mod's place.
        var existing = _libraryRepository.GetAll().FirstOrDefault(e => e.IsOpaquePak && string.Equals(e.FolderName, mod.FolderName, StringComparison.OrdinalIgnoreCase));
        return ReplaceForPatch(existing, replaced,
            () => _libraryRepository.ImportPaks(ordered, mod.Name, source: "Patch", nexusModId: mod.NexusModId, catalogEntryId: mod.CatalogEntryId).FolderName);
    }

    /// <summary>
    /// The patch's UE4SS mods: a bundled one goes into staging (a same-named copy already here is
    /// kept and only gets the settings), a referenced one is looked for by name or Nexus id among
    /// the game's and the staged mods. Each found mod gets the patch's config files written into
    /// its own (the local file backed up first), once (<paramref name="configured"/> remembers).
    /// </summary>
    private Ue4ssImportOutcome ImportUe4ssFromPatch(PatchImportContents contents, HashSet<string> configured, List<string>? versionNotes)
    {
        var lines = new List<string>();
        var toTurnOn = new List<string>();
        var missing = new List<MissingPatchMod>();
        var wanted = contents.Manifest.Ue4ssMods;
        if (wanted.Count == 0)
        {
            return new Ue4ssImportOutcome(lines, toTurnOn, missing);
        }

        if (_ue4ssStateService is null || _ue4ssRepository is null || _ue4ssMetaStore is null)
        {
            lines.Add($"{wanted.Count} UE4SS mod(s) in the patch were skipped (UE4SS isn't set up here)");
            return new Ue4ssImportOutcome(lines, toTurnOn, missing);
        }

        var modsFolder = Ue4ssGameModsFolder ?? "";
        var states = _ue4ssStateService.GetAll(modsFolder).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        // Each installed mod's Nexus id, read once: the fallback match for a renamed folder.
        var byNexusId = new Dictionary<int, string>();
        foreach (var state in states.Values)
        {
            if (_ue4ssMetaStore.Load(state.Name).NexusModId is { } id)
            {
                byNexusId.TryAdd(id, state.Name);
            }
        }

        var settingsApplied = 0;
        var landed = 0;
        var configBackups = Path.Combine(_backupDirectory, "UE4SS-Mod-Configs");
        foreach (var mod in wanted)
        {
            var name = FindUe4ssMatch(mod, states, byNexusId);
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
                missing.Add(new MissingPatchMod($"{mod.NexusName ?? mod.Name} (UE4SS mod{(mod.NexusModId is { } id ? $", Nexus #{id}" : "")})", mod.NexusModId, null));
                continue;
            }

            var state = states[name];
            var folder = state.IsInGame ? Path.Combine(modsFolder, name) : _ue4ssRepository.GetFolderPath(name);
            if (versionNotes is not null && mod.Version is { Length: > 0 } wantedVersion && Ue4ssModVersion.TryRead(folder) is { } have
                && !UpdateVersionComparer.AreEquivalent(have, wantedVersion))
            {
                versionNotes.Add($"UE4SS mod '{name}' is {have} here; the patch was made with {wantedVersion}.");
            }

            if (configured.Add(name))
            {
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
            }

            if (mod.Enabled && !state.IsEnabled)
            {
                toTurnOn.Add(name);
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

        return new Ue4ssImportOutcome(lines, toTurnOn, missing);
    }

    /// <summary>
    /// The UE4SS mods the patch runs that are off here: one question, then Apply puts them in the
    /// game (the same move the Library's Apply makes, backed up the same way). Not while the game
    /// runs: its Mods folder is in use. Returns the lines for the status message.
    /// </summary>
    private async Task<List<string>> OfferToTurnOnUe4ssAsync(List<string> toTurnOn)
    {
        var lines = new List<string>();
        if (toTurnOn.Count == 0 || _ue4ssStateService is null || Ue4ssGameModsFolder is not { } modsFolder)
        {
            return lines;
        }

        var names = string.Join(", ", toTurnOn);
        if (_gameProcessChecker?.IsRunning() == true)
        {
            lines.Add($"the patch runs {names}: close the game, then tick them in the Library's UE4SS list and press Apply");
            return lines;
        }

        if (!_dialogs.Confirm(
                $"The patch runs these UE4SS mods, which are off in your game:\n\n{string.Join("\n", toTurnOn.Select(n => $"• {n}"))}\n\nTurn them on now? They go into the game's Mods folder (the same as Apply in the Library); the game must be closed.",
                "Turn on the patch's UE4SS mods", ThemedConfirmSeverity.Question))
        {
            lines.Add($"tick {names} in the Library's UE4SS list and press Apply to turn them on");
            return lines;
        }

        var service = _ue4ssStateService;
        var backups = _backupDirectory;
        var failures = await Task.Run(() => service.Apply(modsFolder, toTurnOn.ToDictionary(n => n, _ => true, StringComparer.OrdinalIgnoreCase), backups));
        _messenger.Send(new LibraryChangedMessage());
        var on = toTurnOn.Where(n => failures.All(f => !string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (on.Count > 0)
        {
            lines.Add($"turned on {string.Join(", ", on)} in the game");
        }

        lines.AddRange(failures.Select(f => $"couldn't turn on {f.Name}: {f.Reason}"));
        return lines;
    }

    /// <summary>The local UE4SS mod a patch entry stands for: the same folder name, else the same Nexus mod.</summary>
    private static string? FindUe4ssMatch(PatchUe4ssEntry mod, IReadOnlyDictionary<string, Ue4ssModState> states, IReadOnlyDictionary<int, string> byNexusId) =>
        states.TryGetValue(mod.Name, out var byName) ? byName.Name
        : mod.NexusModId is { } nexusId && byNexusId.TryGetValue(nexusId, out var byId) ? byId
        : null;
}
