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
    // --- Export/Import patch (format 2 with sources, paks, UE4SS mods and their settings) ---
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
    /// name and author is referenced too, with that entry's id. A mod edited or fixed in Starlink
    /// is bundled with its ids kept, so friends build with the changed copy; anything else (a
    /// hand-made mod, a pak from nowhere) is bundled, since a friend couldn't get it elsewhere.
    /// The UE4SS mods on in the game come along the same way, with their settings when the box is
    /// ticked. Reading the mods to bundle runs off the UI thread.
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
            var catalogEntries = daedalusTask.Result.Concat(jimk72Task.Result).ToList();
            var catalogKeys = catalogEntries.Select(e => CatalogKey.Normalize(e.Name, e.Author)).ToHashSet();
            // Both sources can list the same name and author: an entry a friend can download wins.
            var catalogIds = new Dictionary<(string Name, string Author), string>();
            foreach (var entry in catalogEntries.OrderBy(e => e.ExmodzUrl is not null || e.PakUrl is not null ? 0 : 1))
            {
                catalogIds.TryAdd(CatalogKey.Normalize(entry.Name, entry.Author), entry.Id);
            }

            // Taken here on the UI thread; the bundled mods and UE4SS configs are read off it.
            List<LibraryEntry> queued = [.. Queue];
            var options = BuildGameplayOptionsFromUi();
            var includeConfigs = ExportIncludesConfigs;
            var modsFolder = Ue4ssGameModsFolder;
            var winners = new Dictionary<string, string>(_winners, StringComparer.OrdinalIgnoreCase);
            var builder = new PatchBuilder(_libraryRepository, _ue4ssMetaStore);
            var built = await Task.Run(() =>
            {
                // The UE4SS mods on in the game (the framework's own left out).
                var ue4ssMods = modsFolder is null ? [] : PatchBuilder.Ue4ssModsOnIn(modsFolder);
                return builder.Build(profileName, queued, options, ue4ssMods, includeConfigs, catalogKeys, catalogIds, winners);
            });
            var (manifest, bundle, skipped) = built;
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

            // Writing the zip (every bundled pak) is slow; kept off the UI thread so the window doesn't freeze.
            await Task.Run(() => _patchService.ExportAsync(manifest, bundle, exportPath));

            var bundledCount = bundle.Exmods.Count + bundle.PakFolders.Count + bundle.Ue4ssFolders.Count;
            var parts = new List<string> { $"{manifest.Mods.Count} data mod(s)" };
            if (manifest.Ue4ssMods.Count > 0)
            {
                parts.Add($"{manifest.Ue4ssMods.Count} UE4SS mod(s){(manifest.IncludesConfigs ? " with their settings" : "")}");
            }

            PatchStatusMessage = $"Exported '{profileName}' to '{exportPath}' — {string.Join(" and ", parts)}; "
                + (bundledCount > 0
                    ? $"{bundledCount} bundled (nowhere else to get them, or changed in Starlink), the rest referenced by their Nexus or catalog source."
                    : "every one referenced by its Nexus or catalog source, nothing bundled.");

            if (built.ChangedCopies.Count > 0)
            {
                PatchStatusMessage += $" Your copy went in for the mods you changed in Starlink, so friends get your changes, not the Nexus original: {string.Join(", ", built.ChangedCopies)}.";
            }

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
    /// Loads a classic IMM mod list into a profile of its own (made when it doesn't exist, used
    /// again on a later run), so the profile in use and its list are left as they are. A later run
    /// replaces that profile's list, keeping the one from before as a backup point.
    /// </summary>
    internal void LoadModListIntoOwnProfile(string listPath, string profileName)
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        // Anything changed but not yet saved goes into the profile in use before it's left.
        SaveWorkingState();
        var existing = ProfileNames.FirstOrDefault(n => string.Equals(n, profileName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            try
            {
                _profileStore.Save(new Profile { Name = profileName });
            }
            catch (Exception ex)
            {
                // Settings has already brought the mods over: this must not read as the migration failing.
                StatusMessage = $"Couldn't make profile '{profileName}' ({ex.Message}), so the list wasn't loaded.";
                _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
                return;
            }

            ReloadProfileNames();
            existing = ProfileNames.FirstOrDefault(n => string.Equals(n, profileName, StringComparison.OrdinalIgnoreCase)) ?? profileName;
        }

        SelectedProfileName = existing;
        var loaded = LoadModListFromFile(listPath);
        StatusMessage = loaded
            ? $"Loaded the classic IMM list into profile '{existing}' (your other profiles weren't changed). {StatusMessage}"
            : $"Profile '{existing}': {StatusMessage}";
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
    /// The list it replaces is kept as a backup point first (the auto-save that follows makes
    /// none), so Restore profile backup brings it back; a list with nothing in the Library leaves
    /// the queue as it was. Settings repeats StatusMessage after its own line, so it names where
    /// things are rather than "below".
    ///
    /// Used by Settings' own classic-IMM migration, through LoadModListIntoOwnProfile (which picks
    /// the profile first): bringing the mods over is only half a migration — the user's actual
    /// merge list, in its real order, is the other half. This page has no file-dialog entry point
    /// of its own for it.
    /// </summary>
    /// <returns>True when the list replaced the queue.</returns>
    public bool LoadModListFromFile(string modListPath)
    {
        if (RefuseWhileRebuilding())
        {
            return false;
        }

        var profile = SelectedProfileName ?? "Default";
        if (IsUnloadable(profile))
        {
            // Its backup step would save the list shown (another profile's) into it first.
            StatusMessage = $"'{profile}' couldn't be loaded, so the list shown isn't its own and the list wasn't loaded into it. Fix or delete its file first, or pick another profile.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
            return false;
        }

        try
        {
            var names = ModListText.ParseNames(File.ReadAllText(modListPath));
            if (names.Count == 0)
            {
                StatusMessage = "That file has no mod names in it.";
                return false;
            }

            var result = ModListMatcher.Match(names, _libraryRepository.GetAll());

            Warnings.Clear();
            foreach (var name in result.Unmatched)
            {
                Warnings.Add($"No Library match for '{name}' — import it first, then re-import this list.");
            }

            foreach (var pack in result.Matched.Where(IsIslBuiltPack))
            {
                Warnings.Add($"'{pack.Name}' is a pack Merge & Install built, so it wasn't ticked — tick the mods it was built from instead.");
            }

            var toQueue = result.Matched.Where(e => !IsIslBuiltPack(e)).ToList();
            if (toQueue.Count == 0)
            {
                StatusMessage = (result.Matched.Count == 0
                    ? $"None of the {names.Count} mods on that list are in your Library, so the merge list was left as it was."
                    : "Nothing on that list can be ticked (only packs Merge & Install built), so the merge list was left as it was.")
                    + " They're listed under Warnings on Merge & Install.";
                _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
                return false;
            }

            var keptBefore = "";
            if (Queue.Count > 0)
            {
                try
                {
                    KeepListAsBackupPoint(profile);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Couldn't back up the list of profile '{profile}' first ({ex.Message}), so it wasn't replaced.";
                    _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
                    return false;
                }

                keptBefore = $" The list profile '{profile}' had before is kept: Profile ▾ › Restore profile backup brings it back.";
            }

            Queue.Clear();
            foreach (var entry in toQueue)
            {
                Queue.Add(entry);
            }

            StatusMessage = (result.Unmatched.Count == 0
                ? $"Queued all {result.Matched.Count} mod(s) from the list."
                : $"Queued {result.Matched.Count} of {names.Count} — {result.Unmatched.Count} not in your Library yet (listed under Warnings on Merge & Install).")
                + keptBefore;
            _activityLog.Log($"Imported IMM mod list — {result.Matched.Count} of {names.Count} matched.",
                result.Unmatched.Count == 0 ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
            return true;
        }
        catch (Exception ex)
        {
            // Same UI boundary as ImportPatchAsync below — an unreadable/locked file shows a
            // status message rather than crashing the app.
            StatusMessage = $"Couldn't import the list: {ex.Message}";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
            return false;
        }
    }

    /// <summary>
    /// Makes the list as it is now the profile's newest backup, before something replaces it: it's
    /// written first (an auto-save may still be pending), then saved again so the backup that
    /// second save makes is this list. Throws when either write fails.
    /// </summary>
    private void KeepListAsBackupPoint(string profile)
    {
        var current = CurrentWorkingProfile(profile);
        _profileStore.SaveWithoutBackup(current);
        _profileStore.Save(current);
        RefreshCanRestoreProfileBackup();
    }

    /// <summary>A mod the patch names that isn't in the Library: what it's called and where it could come from.</summary>
    private sealed record MissingPatchMod(string Label, int? NexusModId, string? CatalogEntryId, int? NexusFileId = null)
    {
        public bool CanFetch(IPatchModFetcher fetcher) => (NexusModId is not null && fetcher.CanFetchNexus) || CatalogEntryId is not null;
    }

    /// <summary>What ImportUe4ssFromPatch found and did.</summary>
    private sealed record Ue4ssImportOutcome(List<string> Lines, List<string> ToTurnOn, List<MissingPatchMod> Missing);

    /// <summary>
    /// Set while an import adds the patch's mods to the Library (off the UI thread, across awaits):
    /// a second import is refused meanwhile, and so is a build.
    /// </summary>
    private bool _isImportingPatch;

    private const string ImportAlreadyRunning = "An import is already running — wait for it to finish.";

    /// <summary>
    /// Clears what the app keeps per folder name (the pak preview cache, the saved source pak) when
    /// Import patch replaces a Library mod, the way the Library's own replacements do. Set where
    /// the app is put together; without it only the folder and its Library info are replaced.
    /// </summary>
    public LibraryModRemover? ModRemover { get; set; }

    /// <summary>
    /// Import + Sync reinstalls listed mods (force refresh; does not skip same version): a bundled
    /// mod replaces the Library copy of the same mod (same name and author, or a copy an earlier
    /// patch import made), rather than skipping it as "already have it", since the whole point is
    /// syncing exactly what the patch carries; the copy is backed up first and keeps its pin,
    /// favourite, notes and rename, and a copy edited in Starlink is asked about first. A
    /// different mod under the same folder name is left alone and the patch's copy is added
    /// beside it. A referenced mod is matched by its Nexus id, then its catalog id, then (Name,
    /// Author) against the local Library; the ones that are missing are offered as one download
    /// (through the Downloads page's own Nexus and Database pipelines), and what still isn't there
    /// is reported rather than failing the whole import — same SkipWithWarning philosophy as
    /// everywhere else the merge pipeline can hit a gap. A copy whose version isn't the patch's is
    /// kept and noted in the warnings. A bundled UE4SS mod lands in staging; a referenced one is
    /// looked for among the game's and the staged ones; either gets the patch's settings written
    /// into its own config (the local file is backed up first), and the ones not on in the game
    /// are offered to be turned on (the game closed).
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportPatchAsync()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (_isImportingPatch)
        {
            PatchStatusMessage = ImportAlreadyRunning;
            return;
        }

        if (_dialogs.PickFile("Import a patch", "IcarusStarlink patch (*.json;*.zip)|*.json;*.zip") is not { } patchPath)
        {
            return;
        }

        _isImportingPatch = true;
        try
        {
            // Unpacking the patch's bundled paks and UE4SS folders is slow; kept off the UI thread.
            using var contents = await Task.Run(() => _patchService.ImportAsync(patchPath));
            await ImportPatchContentsCoreAsync(contents);
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log(PatchStatusMessage, ActivityEntryKind.Warning);
        }
        finally
        {
            _isImportingPatch = false;
        }
    }

    /// <summary>The Library side of an import, for this page's own Import patch and the Server page's "Apply a patch to this server" (IPatchImporter).</summary>
    public async Task<string?> ImportPatchContentsAsync(PatchImportContents contents)
    {
        if (RefuseWhileRebuilding())
        {
            return null;
        }

        if (_isImportingPatch)
        {
            PatchStatusMessage = ImportAlreadyRunning;
            return PatchStatusMessage;
        }

        _isImportingPatch = true;
        try
        {
            return await ImportPatchContentsCoreAsync(contents);
        }
        finally
        {
            _isImportingPatch = false;
        }
    }

    /// <summary>
    /// ImportPatchContentsAsync without the busy check. The Library work (copying paks, backing
    /// up mods, staging UE4SS mods) runs off the UI thread; the questions, the profile save and
    /// every property change stay on it.
    /// </summary>
    private async Task<string?> ImportPatchContentsCoreAsync(PatchImportContents contents)
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
            var outcome = new PatchLibraryOutcome();
            AskBeforeReplacingEditedCopies(contents, outcome);
            PatchStatusMessage = "Importing the patch's mods…";
            var (resolvedFolderNames, missing, localFolders) = await Task.Run(() => ResolvePatchMods(contents, versionNotes, imported, outcome));
            var configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ue4ss = await Task.Run(() => ImportUe4ssFromPatch(contents, configured, versionNotes));
            var downloaded = 0;
            var notFetched = new List<string>();
            string? stoppedFetchingAt = null;

            // One question for everything the patch names that isn't here yet and can be fetched.
            var fetchable = missing.Concat(ue4ss.Missing).Where(m => _patchFetcher is not null && m.CanFetch(_patchFetcher)).ToList();
            if (fetchable.Count > 0 && _patchFetcher is { } fetcher
                && _dialogs.Confirm(
                    $"{fetchable.Count} mod(s) in '{manifest.ProfileName}' aren't in your Library:\n\n{string.Join("\n", fetchable.Select(m => $"• {m.Label}"))}\n\n"
                    + "Download them now? Each comes through the Downloads page from Nexus or the IMM Database and is added to your Library.",
                    "Get the patch's mods", ThemedConfirmSeverity.Question))
            {
                // One at a time: a free account's Nexus page opens only after the last one's download came back.
                var turn = 0;
                foreach (var mod in fetchable)
                {
                    turn++;
                    PatchStatusMessage = $"Getting '{mod.Label}' ({turn} of {fetchable.Count})…";
                    try
                    {
                        if (mod.NexusModId is { } nexusId && fetcher.CanFetchNexus)
                        {
                            if (!await fetcher.FetchNexusAsync(nexusId, mod.NexusFileId, mod.Label))
                            {
                                stoppedFetchingAt = mod.Label;
                                break;
                            }
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
                (resolvedFolderNames, missing, localFolders) = await Task.Run(() => ResolvePatchMods(contents, versionNotes: null, imported, outcome));
                var again = await Task.Run(() => ImportUe4ssFromPatch(contents, configured, versionNotes: null));
                ue4ss = new Ue4ssImportOutcome(
                    [.. ue4ss.Lines.Concat(again.Lines).Distinct(StringComparer.Ordinal)],
                    [.. ue4ss.ToTurnOn.Concat(again.ToTurnOn).Distinct(StringComparer.OrdinalIgnoreCase)],
                    again.Missing);
                downloaded = before - (missing.Count + ue4ss.Missing.Count);
            }

            // A pending auto-save of the profile in use is written first: written after the import,
            // it put the old queue back over a patch imported under the same profile name.
            SaveWorkingState();
            var (winners, keptPicks, patchPicks) = PicksForImportedProfile(manifest, resolvedFolderNames, localFolders);
            _profileStore.Save(new Profile { Name = manifest.ProfileName, MergeQueueFolderNames = resolvedFolderNames, Options = manifest.Options, ConflictWinners = winners });
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

            if (stoppedFetchingAt is not null)
            {
                summary.Add($"stopped downloading at '{stoppedFetchingAt}' (the Downloads page says why)");
            }

            if (notFetched.Count > 0)
            {
                summary.Add($"couldn't download {string.Join(", ", notFetched)}");
            }

            if (versionNotes.Count > 0)
            {
                summary.Add($"{versionNotes.Count} mod(s) are a different version from the patch's (see the warnings below)");
            }

            if (outcome.Replaced.Count > 0)
            {
                summary.Add($"your copy of {string.Join(", ", outcome.Replaced)} was replaced by the patch's (backed up first: its pin, notes and name stay, and Restore latest backup in the Library brings your files back)");
            }

            if (outcome.Beside.Count > 0)
            {
                summary.Add($"the patch's {string.Join(", ", outcome.Beside)} went in beside your mod of the same folder name, which is a different mod and was left as it was");
            }

            if (outcome.KeptYours.Count > 0)
            {
                summary.Add($"kept your edited copy of {string.Join(", ", outcome.KeptYours)}, and the profile uses it instead of the patch's");
            }

            if (keptPicks > 0)
            {
                summary.Add($"kept {keptPicks} conflict pick(s) you made");
            }

            if (patchPicks > 0)
            {
                summary.Add($"took {patchPicks} conflict pick(s) from the patch");
            }

            summary.AddRange(outcome.Problems);

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
    /// The conflict picks the imported profile saves: the ones a profile of the same name already
    /// had (moved to ids, and only for mods the new queue still has), with the patch's own picks
    /// (format 3) over them, each rewritten to the local folder its mod matched. Where both picked
    /// a winner for the same value, the patch's pick wins, so the merge matches the exporter's.
    /// </summary>
    private (Dictionary<string, string> Winners, int Kept, int FromPatch) PicksForImportedProfile(
        PatchManifest manifest, IReadOnlyList<string> queuedFolders, IReadOnlyDictionary<string, string> localFolders)
    {
        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_profileStore.ProfileNames.FirstOrDefault(n => string.Equals(n, manifest.ProfileName, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            try
            {
                var (migrated, _) = MigrateWinners(_profileStore.Load(existing).ConflictWinners ?? []);
                var byFolder = LibraryByFolder();
                var queuedNames = queuedFolders.Select(f => byFolder.TryGetValue(f, out var e) ? e.Name : null).OfType<string>().ToList();
                local = PatchWinners.StillQueued(migrated, queuedFolders, queuedNames);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                // A profile file that can't be read has no picks to keep; the import goes on.
            }
        }

        var fromPatch = PatchWinners.Remap(manifest.ConflictWinners, localFolders);
        var winners = new Dictionary<string, string>(local, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in fromPatch)
        {
            winners[key] = winner;
        }

        return (winners, local.Keys.Count(k => !fromPatch.ContainsKey(k)), fromPatch.Count);
    }

    /// <summary>What the patch's bundled mods did to the Library copies already there, for the summary; filled off the UI thread, read after it.</summary>
    private sealed class PatchLibraryOutcome
    {
        /// <summary>Folder names of edited copies the player chose to keep: the patch's copy of those mods isn't imported.</summary>
        public HashSet<string> KeepYours { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Replaced { get; } = [];

        public List<string> Beside { get; } = [];

        public List<string> KeptYours { get; } = [];

        /// <summary>Things that went wrong without stopping the import (a pin or note that couldn't be carried over).</summary>
        public List<string> Problems { get; } = [];
    }

    /// <summary>
    /// One question, on the UI thread before any mod is touched, when the patch would replace
    /// copies changed in Starlink (✎ edited, or fixed): No keeps them, and the profile uses them.
    /// </summary>
    private void AskBeforeReplacingEditedCopies(PatchImportContents contents, PatchLibraryOutcome outcome)
    {
        var entries = _libraryRepository.GetAll();
        var edited = contents.Manifest.Mods
            .Select(mod => FindCopyToReplace(mod, contents, entries))
            .OfType<LibraryEntry>()
            .Where(e => e.IsLocallyEdited || e.FixedByIslAtUtc is not null)
            .DistinctBy(e => e.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (edited.Count == 0)
        {
            return;
        }

        if (!_dialogs.Confirm(
                $"The patch carries its own copy of {(edited.Count == 1 ? "a mod" : "mods")} you changed in Starlink (✎ — your own edits or fixes):\n\n{string.Join("\n", edited.Select(e => $"• {e.Name}"))}\n\n"
                + "Replacing them loses those changes. Your copy is backed up first, so Restore latest backup can bring it back.\n\nReplace them? No keeps your copy, and the imported profile uses it.",
                "Replace an edited mod?", ThemedConfirmSeverity.Warning))
        {
            foreach (var entry in edited)
            {
                outcome.KeepYours.Add(entry.FolderName);
            }
        }
    }

    /// <summary>
    /// The patch's data mods against the Library: a bundled one is imported (replacing the copy
    /// of the same mod), a referenced one matched; the queue's folder names in the patch's order,
    /// and what's missing. <paramref name="versionNotes"/> (when given) collects the matches whose
    /// version differs from the patch's. A second call (after downloads) doesn't import the
    /// bundled mods again: it finds the copies the first call made.
    /// </summary>
    private (List<string> Resolved, List<MissingPatchMod> Missing, Dictionary<string, string> LocalFolders) ResolvePatchMods(
        PatchImportContents contents, List<string>? versionNotes, Dictionary<string, string> imported, PatchLibraryOutcome outcome)
    {
        var resolved = new List<string>();
        var missing = new List<MissingPatchMod>();
        // The exporter's folder name → the local folder it resolved to, for the patch's conflict picks.
        var localFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in contents.Manifest.Mods)
        {
            if (mod.Bundled && mod.IsPak && contents.BundledPakFolders.TryGetValue(mod.FolderName, out var pakFolder))
            {
                resolved.Add(localFolders[mod.FolderName] = imported.TryGetValue(mod.FolderName, out var pakCopy) ? pakCopy : imported[mod.FolderName] = ImportBundledPak(mod, pakFolder, contents, outcome));
                continue;
            }

            if (mod.Bundled && contents.BundledMods.TryGetValue(mod.FolderName, out var exmod))
            {
                resolved.Add(localFolders[mod.FolderName] = imported.TryGetValue(mod.FolderName, out var exmodCopy) ? exmodCopy : imported[mod.FolderName] = ImportBundledMod(mod, exmod, contents, outcome));
                continue;
            }

            if (FindLibraryMatch(mod) is { } existing)
            {
                resolved.Add(localFolders[mod.FolderName] = existing.FolderName);
                if (versionNotes is not null && !UpdateVersionComparer.AreEquivalent(existing.Version, mod.Version))
                {
                    versionNotes.Add($"'{existing.Name}' is {existing.Version} in your Library; the patch was made with {mod.Version}. Get update in the Library if you want to match it.");
                }
            }
            else
            {
                missing.Add(new MissingPatchMod(
                    $"{mod.Name} by {mod.Author}{(mod.NexusModId is { } id ? $" (Nexus #{id})" : mod.CatalogEntryId is not null ? " (IMM Database)" : "")}",
                    mod.NexusModId, mod.CatalogEntryId, mod.NexusFileId));
            }
        }

        return (resolved, missing, localFolders);
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

    private string ImportBundledMod(PatchModEntry mod, ExmodPackageContents exmod, PatchImportContents contents, PatchLibraryOutcome outcome)
    {
        var existing = FindCopyToReplace(mod, contents, _libraryRepository.GetAll());
        if (existing is not null && outcome.KeepYours.Contains(existing.FolderName))
        {
            outcome.KeptYours.Add($"'{existing.Name}'");
            return existing.FolderName;
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.EXMODZ");
        try
        {
            ExmodzArchive.Write(tempPath, exmod);
            return ReplaceForPatch(existing, mod, BesideAnother(mod, exmod.Package.FileName), outcome,
                () => _libraryRepository.Import(tempPath, source: PatchSource, nexusModId: mod.NexusModId, catalogEntryId: mod.CatalogEntryId).FolderName);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>The Library source of a mod a patch import added: a later import of the patch may replace it.</summary>
    private const string PatchSource = "Patch";

    /// <summary>
    /// The Library copy a bundled mod takes the place of: one of the same kind (an EXMOD mod for an
    /// EXMOD, a pak mod for a pak) under the patch's folder name, or a copy an earlier patch import
    /// made of it (an EXMOD by its file name, a pak under the folder older versions named after
    /// the mod), and only when it is the same mod. Null when nothing is replaced.
    /// </summary>
    private static LibraryEntry? FindCopyToReplace(PatchModEntry mod, PatchImportContents contents, IReadOnlyList<LibraryEntry> entries)
    {
        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        IEnumerable<LibraryEntry> candidates;
        string folderName;
        if (mod.Bundled && mod.IsPak && contents.BundledPakFolders.ContainsKey(mod.FolderName))
        {
            folderName = mod.FolderName;
            var olderFolder = IcarusStarlink.PakIO.Safety.AssetPathGuard.SanitizeToSimpleFileName(mod.Name);
            candidates = entries.Where(e => e.IsOpaquePak && (Same(e.FolderName, folderName) || (e.Source == PatchSource && Same(e.FolderName, olderFolder))));
        }
        else if (mod.Bundled && contents.BundledMods.TryGetValue(mod.FolderName, out var exmod))
        {
            folderName = exmod.Package.FileName;
            candidates = entries.Where(e => !e.IsOpaquePak && (Same(e.FolderName, folderName) || (e.Source == PatchSource && Same(e.FileName, folderName))));
        }
        else
        {
            return null;
        }

        return candidates
            .OrderBy(e => Same(e.FolderName, folderName) ? 0 : 1)
            .FirstOrDefault(e => e.Source == PatchSource || IsSameNameAndAuthor(e, mod));
    }

    /// <summary>
    /// Whether a Library copy under the patch mod's folder name is the same mod, by the name the
    /// mod declares and its author. A copy the player renamed shows the rename as its Name, so the
    /// declared name is used; a renamed pak mod has none, so for it the author alone decides (the
    /// folder name already matched).
    /// </summary>
    private static bool IsSameNameAndAuthor(LibraryEntry entry, PatchModEntry mod) =>
        entry.DisplayNameOverride is not null && entry.DeclaredName is null
            ? CatalogKey.Normalize("", entry.Author) == CatalogKey.Normalize("", mod.Author)
            : CatalogKey.Normalize(entry.DeclaredName ?? entry.Name, entry.Author) == CatalogKey.Normalize(mod.Name, mod.Author);

    /// <summary>The patch mod's name (for the summary) when another Library mod already has the folder name its copy would take, else null.</summary>
    private string? BesideAnother(PatchModEntry mod, string folderName) =>
        _libraryRepository.GetAll().Any(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase))
            ? $"'{mod.Name}'"
            : null;

    /// <summary>
    /// A bundled mod takes the place of the Library copy of the same mod (<paramref name="existing"/>,
    /// null: it's added as a new mod, beside any other mod of that folder name). The copy is backed
    /// up first (no backup, no replace) and put back when the import fails; when it works, its
    /// pin, favourite, notes and rename carry over to the new copy, its Nexus link too when it is
    /// the same mod by name and author, and what the app kept for the old folder (fix choices,
    /// preview cache, saved source pak) is cleared, as the Library's own replacements do.
    /// </summary>
    private string ReplaceForPatch(LibraryEntry? existing, PatchModEntry mod, string? besideAnother, PatchLibraryOutcome outcome, Func<string> import)
    {
        if (existing is null)
        {
            var added = import();
            if (besideAnother is not null)
            {
                outcome.Beside.Add(besideAnother);
            }

            return added;
        }

        string backupPath;
        try
        {
            backupPath = _libraryRepository.BackupMod(existing.FolderName);
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

        if (ModRemover is { } remover)
        {
            remover.RemoveForReplacement(existing.FolderName);
        }
        else
        {
            _libraryRepository.Delete(existing.FolderName);
        }

        string folderName;
        try
        {
            folderName = import();
        }
        catch (Exception importError)
        {
            string? putBackProblem = null;
            try
            {
                if (_libraryRepository.RestoreLatestModBackup(existing.FolderName))
                {
                    if (snapshot is not null)
                    {
                        _libraryRepository.WriteMetaSnapshot(existing.FolderName, snapshot);
                    }
                }
                else
                {
                    putBackProblem = "no backup was found to put back";
                }
            }
            catch (Exception restoreEx)
            {
                Serilog.Log.Warning(restoreEx, "Couldn't put '{Folder}' back after a failed patch import", existing.FolderName);
                putBackProblem = restoreEx.Message;
            }

            if (putBackProblem is null)
            {
                throw;
            }

            _activityLog.Log($"Couldn't put your copy of '{existing.Name}' back after the patch import failed ({putBackProblem}). It's saved in '{backupPath}'.", ActivityEntryKind.Warning);
            throw new IOException($"{importError.Message} Your copy of '{existing.Name}' couldn't be put back ({putBackProblem}): it's saved in '{backupPath}'.", importError);
        }

        outcome.Replaced.Add($"'{existing.Name}'");
        CarryLibraryInfoOver(existing, folderName, mod, outcome);
        ForgetOldFolder(existing.FolderName);
        return folderName;
    }

    /// <summary>The player's pin, favourite, notes and rename onto the patch's copy; the Nexus link only when it is the same mod by name and author. Best-effort: a failure is named in the summary.</summary>
    private void CarryLibraryInfoOver(LibraryEntry existing, string newFolderName, PatchModEntry mod, PatchLibraryOutcome outcome)
    {
        try
        {
            if (existing.IsPinned || existing.IsFavorite || !string.IsNullOrEmpty(existing.Notes))
            {
                _libraryRepository.UpdateMetadata(newFolderName, existing.IsPinned, existing.IsFavorite, existing.Notes);
            }

            if (existing.Source != PatchSource && existing.NexusModId is { } nexusId && IsSameNameAndAuthor(existing, mod))
            {
                _libraryRepository.LinkToNexus(newFolderName, nexusId);
            }

            // Last: it rescans the Library.
            if (existing.DisplayNameOverride is { } rename)
            {
                _libraryRepository.SetDisplayNameOverride(newFolderName, rename);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Couldn't carry the Library info of '{Folder}' over to the patch's copy", existing.FolderName);
            outcome.Problems.Add($"couldn't carry the pin and notes of '{existing.Name}' over ({ex.Message})");
        }
    }

    /// <summary>What the app kept for the replaced copy's folder: its remembered fix choices and saved source pak. Best-effort: leftovers are clutter, not damage.</summary>
    private void ForgetOldFolder(string folderName)
    {
        try
        {
            _fixService?.Forget(folderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Couldn't forget the fix choices of '{Folder}'", folderName);
        }

        ModRemover?.DeleteSourcePak(folderName);
    }

    /// <summary>A bundled pak mod: its pak files (in the order the patch names them) become one Library mod under the patch's folder name, replacing the copy of the same mod.</summary>
    private string ImportBundledPak(PatchModEntry mod, string pakFolder, PatchImportContents contents, PatchLibraryOutcome outcome)
    {
        var existing = FindCopyToReplace(mod, contents, _libraryRepository.GetAll());
        if (existing is not null && outcome.KeepYours.Contains(existing.FolderName))
        {
            outcome.KeptYours.Add($"'{existing.Name}'");
            return existing.FolderName;
        }

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

        // A patch is someone else's file: each pak must be a real pak the game can load, checked
        // before the Library copy is replaced (as a download's paks are).
        foreach (var pak in ordered)
        {
            if ((IcarusStarlink.PakIO.Safety.ModLints.CheckIsPak(pak) ?? IcarusStarlink.PakIO.Safety.ModLints.CheckPakVersion(pak)) is { } notAPak)
            {
                throw new FormatException(notAPak);
            }
        }

        // Under the exporter's folder name, not the display name: a renamed mod (or a "_2" folder)
        // otherwise landed in a new folder each time, and the next import added another copy.
        return ReplaceForPatch(existing, mod, BesideAnother(mod, mod.FolderName), outcome,
            () => _libraryRepository.ImportPaks(ordered, mod.FolderName, source: PatchSource, nexusModId: mod.NexusModId, catalogEntryId: mod.CatalogEntryId).FolderName);
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
        var refusedConfigs = new List<string>();
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
                missing.Add(new MissingPatchMod($"{mod.NexusName ?? mod.Name} (UE4SS mod{(mod.NexusModId is { } id ? $", Nexus #{id}" : "")})", mod.NexusModId, null, mod.NexusFileId));
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
                refusedConfigs.AddRange(mod.RefusedConfigPaths.Select(p => $"{name}: {p}"));
                foreach (var config in mod.Configs)
                {
                    // PatchService already took these out; checked again for any other reader.
                    if (!PatchConfigSafety.IsSafe(config))
                    {
                        refusedConfigs.Add($"{name}: {config.Path}");
                        continue;
                    }

                    var relative = config.Path.Replace('/', Path.DirectorySeparatorChar);
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

        if (refusedConfigs.Count > 0)
        {
            lines.Add($"left out {refusedConfigs.Count} file(s) the patch called settings that aren't plain settings ({string.Join(", ", refusedConfigs)})");
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
