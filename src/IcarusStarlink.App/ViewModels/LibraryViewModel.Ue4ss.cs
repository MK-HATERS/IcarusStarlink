using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The UE4SS panel: reading the installed UE4SS mods, deleting, config, Apply, Nexus link and import.</summary>
public sealed partial class LibraryViewModel
{
    [RelayCommand]
    private void ReloadInstalledUe4ssMods() => ReloadUe4ssRows();

    /// <summary>
    /// Reads the UE4SS mods again, then rebuilds both panels with one Reload (the UE4SS rows live in
    /// the same lists as everything else) — every way out, so the right panel, its selection and the
    /// packages never keep rows that are gone (they did when the Content path was cleared, or the
    /// Mods folder couldn't be read).
    /// </summary>
    /// <param name="fullResync">Passed on to Reload: another page changed mods on disk.</param>
    private void ReloadUe4ssRows(bool fullResync = false)
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            ClearUe4ssRows();
            IsUe4ssLoaderInstalled = true; // nothing to check against yet, so nothing to warn about
            Ue4ssEmptyText = "Set the Icarus Content folder in Settings first.";
            Reload(fullResync);
            return;
        }

        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath);
            var states = _ue4ssModStateService.GetAll(modsFolder);

            // Read once per reload, not once per mod below — it's the same installed loader
            // regardless of which mod row is asking, same as SettingsViewModel.RefreshUe4ssStatus's
            // own single call.
            var loaderStatus = _ue4ssLoaderInstallService.GetStatus(_settingsService.Current.IcarusContentPath!);
            var installedLoaderVersion = loaderStatus.InstalledVersion;
            IsUe4ssLoaderInstalled = loaderStatus.IsInstalled;

            // This reload also runs on every LibraryChangedMessage (an unrelated import/delete/link
            // elsewhere in the app, not just this page's own Refresh/Apply) — rebuilding every row
            // from scratch would otherwise silently drop an unsaved pending enable/disable toggle
            // and any already-fetched "Update available" badge each time, neither of which is
            // persisted anywhere else. Snapshot by name first, then reapply onto the fresh rows.
            var previousByName = Ue4ssMods.ToDictionary(m => m.Name);

            // The old row objects are about to go: carry their selection over by name.
            var selectedNames = BulkSelectedUe4ssRows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var anchorName = (_bulkSelectionAnchor as Ue4ssModRowViewModel)?.Name;
            BulkSelectedUe4ssRows.Clear();

            Ue4ssMods.Clear();
            _ue4ssMetaByName.Clear();
            foreach (var state in states)
            {
                var meta = _ue4ssModMetaStore.Load(state.Name);
                _ue4ssMetaByName[state.Name] = meta;
                // IsFrameworkOwned, not membership in ListUserAddedMods — that list only enumerates
                // what's currently IN the game's real Mods folder, so a disabled/staged mod (built-in
                // or not) would silently misclassify as "user-added" by simple absence from it.
                var isBuiltIn = _ue4ssLoaderInstallService.IsFrameworkOwned(_settingsService.Current.IcarusContentPath!, state.Name);
                var row = new Ue4ssModRowViewModel(
                    state.Name, state.IsEnabled, isBuiltIn, meta.NexusModId, meta.NexusVersion,
                    meta.MinUe4ssVersion, installedLoaderVersion, RecomputeHasPendingUe4ssChanges, meta.NexusName);
                if (previousByName.TryGetValue(state.Name, out var previous))
                {
                    if (previous.IsDirty)
                    {
                        row.IsEnabled = previous.IsEnabled;
                    }

                    row.LatestVersion = previous.LatestVersion;
                    row.LatestNexusFileId = previous.LatestNexusFileId;
                    row.EndorsementStatus = previous.EndorsementStatus;
                }

                if (selectedNames.Contains(row.Name))
                {
                    SetRowSelected(row, true);
                }

                if (anchorName is not null && string.Equals(anchorName, row.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _bulkSelectionAnchor = row;
                }

                row.LoaderMissing = !IsUe4ssLoaderInstalled;
                row.NotStartedReason = state.IsSwitchedOffInGame ? state.WhyNotStarted : null;
                row.NexusFileId = meta.NexusFileId;
                row.HasConfig = ModHasConfig(state);
                row.LocalVersion = ModLocalVersion(state);
                Ue4ssMods.Add(row);
            }
            RecomputeHasPendingUe4ssChanges();

            // A sidecar whose mod folder is gone (deleted by hand, renamed) is dead weight that can
            // attach a stale Nexus link to an unrelated mod landing under the same name later —
            // found live as two orphaned "ArcFissionLibrary … _2.json" files. Only when the game's
            // Mods folder is really there: with the game drive unplugged (or UE4SS just removed)
            // every enabled mod reads as gone, and purging then wiped all their Nexus links.
            if (Directory.Exists(modsFolder))
            {
                var liveNames = new HashSet<string>(states.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var stale in _ue4ssModMetaStore.ListFolderNames().Where(n => !liveNames.Contains(n)).ToList())
                {
                    _ue4ssModMetaStore.Delete(stale);
                }
            }

            Ue4ssEmptyText = states.Count == 0 ? "No UE4SS mods yet. Import one, or download one from Nexus." : NoMatchText;
        }
        catch (Exception ex)
        {
            // Not a half-built list: none, and why.
            ClearUe4ssRows();
            Ue4ssStatusMessage = $"Couldn't read the game's UE4SS Mods folder: {ex.Message}";
            Ue4ssEmptyText = Ue4ssStatusMessage;
        }

        // The UE4SS rows live in the same lists as everything else — rebuild them with the fresh rows.
        Reload(fullResync);
    }

    /// <summary>No UE4SS rows: none selected or anchored, no sidecars cached, nothing pending.</summary>
    private void ClearUe4ssRows()
    {
        foreach (var row in BulkSelectedUe4ssRows)
        {
            row.IsSelectedForBulk = false;
        }

        BulkSelectedUe4ssRows.Clear();
        if (_bulkSelectionAnchor is Ue4ssModRowViewModel)
        {
            _bulkSelectionAnchor = null;
        }

        Ue4ssMods.Clear();
        _ue4ssMetaByName.Clear();
        HasPendingUe4ssChanges = false;
    }

    /// <summary>Where a UE4SS mod's current copy is: the game's Mods folder when it's in the game (and a game folder is set), this app's staging otherwise.</summary>
    private string ResolveUe4ssModFolder(string name, bool inGame)
    {
        var contentPath = _settingsService.Current.IcarusContentPath;
        return inGame && !string.IsNullOrWhiteSpace(contentPath)
            ? Path.Combine(Ue4ssGamePaths.ResolveModsFolder(contentPath), name)
            : _ue4ssModRepository.GetFolderPath(name);
    }

    /// <summary>The version the mod's current copy states in its main.lua; null when it states none or can't be read, so the list never breaks over it.</summary>
    private string? ModLocalVersion(Ue4ssModState state)
    {
        try
        {
            return Ue4ssModVersion.TryRead(ResolveUe4ssModFolder(state.Name, state.IsInGame));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether the mod's current copy (game when on, staging when off) has a config file; false when that can't be read, so the list never breaks over it.</summary>
    private bool ModHasConfig(Ue4ssModState state)
    {
        try
        {
            return Ue4ssModConfigFiles.HasAny(ResolveUe4ssModFolder(state.Name, state.IsInGame));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RecomputeHasPendingUe4ssChanges()
    {
        HasPendingUe4ssChanges = Ue4ssMods.Any(m => m.IsDirty);
        RefreshEnabledAndStatus();
    }

    /// <summary>
    /// Removes one UE4SS mod completely: out of the game's Mods folder if it's enabled there (backed
    /// up first, through the same Apply path the Disable checkbox uses), then out of staging, then
    /// its Nexus sidecar. Returns a failure reason or null.
    /// </summary>
    private string? DeleteUe4ssModCore(string folderName)
    {
        try
        {
            var contentPath = _settingsService.Current.IcarusContentPath;
            if (!string.IsNullOrWhiteSpace(contentPath))
            {
                var modsFolder = Ue4ssGamePaths.ResolveModsFolder(contentPath);
                if (_ue4ssModRepository.ListInstalledInGame(modsFolder).Contains(folderName, StringComparer.OrdinalIgnoreCase))
                {
                    var failures = _ue4ssModStateService.Apply(modsFolder, new Dictionary<string, bool> { [folderName] = false }, _backupDirectory);
                    if (failures.Count > 0)
                    {
                        return failures[0].Reason;
                    }
                }
            }

            if (_ue4ssModRepository.GetAll().Contains(folderName, StringComparer.OrdinalIgnoreCase))
            {
                _ue4ssModRepository.Delete(folderName);
            }

            _ue4ssModMetaStore.Delete(folderName);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The UE4SS tab's own Delete — the verb that was missing entirely (a real report: "I can't delete UE4SS mods inside ISL"). A package member is deleted together with its Library paks via DeleteMods, so one confirmation names every part.</summary>
    [RelayCommand]
    private void DeleteUe4ssMod(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsBuiltIn)
        {
            Ue4ssStatusMessage = $"'{item.Name}' ships with the UE4SS loader itself — untick it and Apply to disable it instead.";
            return;
        }

        if (PackageOf(item) is { LibraryFolderNames.Count: > 0 } package)
        {
            var libraryParts = AllLibraryItems().Where(i => package.LibraryFolderNames.Contains(i.FolderName, StringComparer.OrdinalIgnoreCase)).ToList();
            if (libraryParts.Count > 0)
            {
                DeleteMods(libraryParts);
                return;
            }
        }

        var confirm = _dialogService.Confirm(
            item.IsFolderInGame
                ? $"Delete '{item.Name}'?\n\nIt is removed from the game's Mods folder (a backup is kept under Backups\\UE4SS) and from this app's staging. Re-download it to get it back."
                : $"Delete '{item.Name}'?\n\nIts staged folder is removed from disk. Re-download it to get it back.",
            "Delete UE4SS mod", ThemedConfirmSeverity.Warning);
        if (!confirm)
        {
            return;
        }

        var failure = DeleteUe4ssModCore(item.Name);
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        Ue4ssStatusMessage = failure is null ? $"Deleted '{item.Name}'." : $"Couldn't delete '{item.Name}': {failure}";
        _activityLog.Log(failure is null ? $"Deleted UE4SS mod '{item.Name}'." : $"Couldn't delete UE4SS mod '{item.Name}': {failure}",
            failure is null ? ActivityEntryKind.Info : ActivityEntryKind.Warning);
    }

    /// <summary>The servers Starlink runs (the Server page's local servers): a UE4SS mod's config can be saved to its copy there too.</summary>
    public Func<IReadOnlyList<IcarusStarlink.Core.Server.DedicatedServerProfile>>? ServerProfiles { get; init; }

    /// <summary>Right-click a UE4SS mod > Edit config: its config file as fields, for the game's copy and any server's.</summary>
    [RelayCommand]
    private void EditUe4ssModConfig(Ue4ssModRowViewModel? row)
    {
        if (row is not null)
        {
            OpenModConfigEditor(row);
        }
    }

    /// <summary>The same for a "Pak + UE4SS" row: its Lua half's config.</summary>
    [RelayCommand]
    private void EditLinkedUe4ssConfig(LibraryItemViewModel? item)
    {
        if (item is not null && LinkedLuaRows(item).FirstOrDefault(r => r.HasConfig) is { } row)
        {
            OpenModConfigEditor(row);
        }
    }

    private void OpenModConfigEditor(Ue4ssModRowViewModel row)
    {
        try
        {
            var locations = new List<ModConfigLocation>();
            var inGame = row.IsFolderInGame && !string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath);
            locations.Add(new ModConfigLocation(inGame ? "Your game" : "Starlink (the mod is off)", ResolveUe4ssModFolder(row.Name, inGame), IsServer: false));

            foreach (var profile in ServerProfiles?.Invoke() ?? [])
            {
                var folder = Path.Combine(IcarusStarlink.Core.Server.DedicatedServerLayout.Ue4ssModsFolder(profile.InstallFolder), row.Name);
                if (Directory.Exists(folder))
                {
                    locations.Add(new ModConfigLocation($"Server: {profile.Name}", folder, IsServer: true));
                }
            }

            var viewModel = new ModConfigEditorViewModel(row.Name, locations, _backupDirectory);
            if (!viewModel.HasFiles)
            {
                Ue4ssStatusMessage = $"'{row.Name}' has no config file.";
                return;
            }

            new ModConfigEditorWindow(viewModel) { Owner = Application.Current.MainWindow }.Show();
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't open the config: {ex.Message}";
        }
    }

    /// <summary>Where this mod's files actually are right now — the game's Mods folder when enabled, this app's staging when not.</summary>
    [RelayCommand]
    private void OpenUe4ssModFolder(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var path = ResolveUe4ssModFolder(item.Name, item.IsFolderInGame);
            if (!Directory.Exists(path))
            {
                Ue4ssStatusMessage = $"'{item.Name}' has no folder at {path}.";
                return;
            }

            UrlOpener.TryOpen(path);
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't open the folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Moves every dirty row to match its toggled state — enabling copies staging→game, disabling
    /// backs up then moves game→staging (IUe4ssModStateService.Apply) — then reloads so every row's
    /// RealIsEnabled (and the page's own dirty state) reflects what's actually on disk now.
    /// </summary>
    [RelayCommand]
    private void ApplyUe4ssChanges()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            Ue4ssStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath);
            var desired = Ue4ssMods.Where(m => m.IsDirty).ToDictionary(m => m.Name, m => m.IsEnabled);
            var changedCount = desired.Count;

            var failures = _ue4ssModStateService.Apply(modsFolder, desired, _backupDirectory);
            // Always reload, even on partial failure — Apply itself never rolls back a change it
            // already made to an EARLIER mod in this same batch just because a LATER one failed, so
            // the UI must re-read real on-disk state either way rather than keep showing whatever
            // was true before this call.
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = failures.Count == 0
                ? $"Applied {changedCount} change(s)."
                : $"Applied {changedCount - failures.Count} of {changedCount} change(s) — failed: "
                    + string.Join("; ", failures.Select(f => $"{f.Name} ({f.Reason})"));
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Apply failed: {ex.Message}";
            _activityLog.Log($"Applying UE4SS changes failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Manual counterpart to DownloadsViewModel.EnrichUe4ssModFromNexusAsync (which only ever runs
    /// automatically when a UE4SS mod is activated from a pending Nexus download) — mirrors
    /// LinkToNexus/LinkFolderToNexusAsync's own shape exactly, just writing through
    /// IUe4ssModMetaStore's per-folder sidecar instead of ILibraryRepository. Deliberately not
    /// offered for a framework-built-in row (see the XAML's own IsBuiltIn-gated menu item) — those
    /// ship with the loader itself and have no standalone Nexus page of their own to link to.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task LinkUe4ssModToNexus(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var dialog = new LinkNexusDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var meta = _ue4ssModMetaStore.Load(item.Name);
            meta.NexusModId = dialog.NexusModId;
            _ue4ssModMetaStore.Save(item.Name, meta);

            var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
            if (apiKey is not null)
            {
                try
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, NxmHandlerCommand.IcarusDomain, dialog.NexusModId);
                    if (info is not null)
                    {
                        meta.NexusVersion = info.Version;
                        meta.NexusName = info.Name;
                        _ue4ssModMetaStore.Save(item.Name, meta);
                    }
                }
                catch (Exception)
                {
                    // Best-effort — the ID link above already succeeded regardless.
                }
            }

            // Rebuilds every row (Ue4ssModRowViewModel's NexusModId/KnownVersion are constructor-set,
            // not observable) so this row picks up what was just saved — same reason ApplyUe4ssChanges
            // already reloads after its own write.
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = $"Linked '{item.Name}' to Nexus mod {dialog.NexusModId}.";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't link: {ex.Message}";
        }
    }

    /// <summary>
    /// Manual, user-declared-only counterpart to LinkUe4ssModToNexus, right next to it in the row's
    /// own context menu — same IUe4ssModMetaStore sidecar write, same reload-after-write to refresh
    /// the row's constructor-set properties. There is no automatic source for a mod's own minimum
    /// UE4SS version (see Ue4ssModMeta.MinUe4ssVersion's own doc comment), so this is the only way
    /// the field is ever set: the user reads it off the mod's own Nexus page/readme and types it in.
    /// Reuses IDialogService.PromptRename (this app's one generic single-line text prompt, despite
    /// its name) rather than a new dialog type — resetValue is left null so "Clear requirement"
    /// clears the field entirely (Ue4ssVersionComparer.Compare then reports Unknown, never a
    /// warning), the same "reset means clear the override" semantics Library's own RenameItem uses.
    /// </summary>
    [RelayCommand]
    private void SetMinUe4ssVersion(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var result = _dialogService.PromptRename(
            item.MinUe4ssVersion ?? string.Empty,
            description: "Enter the minimum UE4SS loader version this mod needs (e.g. \"3.0.1\") — read it from the mod's own Nexus page or readme, since this app has no way to detect a mod's own requirement automatically. Compared against your installed UE4SS loader version to show a warning if it's too old.",
            resetValue: null,
            resetLabel: "Clear requirement",
            resetTooltip: "Removes the minimum UE4SS version requirement — no warning will show for this mod",
            title: "Set minimum UE4SS version",
            fieldLabel: "Minimum UE4SS version");
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            var meta = _ue4ssModMetaStore.Load(item.Name);
            meta.MinUe4ssVersion = string.IsNullOrWhiteSpace(result.NewDisplayName) ? null : result.NewDisplayName.Trim();
            _ue4ssModMetaStore.Save(item.Name, meta);

            // Same reason ApplyUe4ssChanges/LinkUe4ssModToNexus already reload after their own
            // writes — Ue4ssModRowViewModel's MinUe4ssVersion is constructor-set, not observable.
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = meta.MinUe4ssVersion is null
                ? $"Cleared the minimum UE4SS version requirement for '{item.Name}'."
                : $"'{item.Name}' now requires UE4SS v{meta.MinUe4ssVersion}+.";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>
    /// The UE4SS tab's own Import… — the SAME content sniffing every other entry point uses
    /// (ExtractedModClassifier via ImportOnePath), so an archive in game-folder layout imported
    /// here lands exactly as it would from Nexus: the Lua folder staged here, its paks as one
    /// Library mod of the same name. It used to unzip straight into staging, which dropped a
    /// bundle's paks on the floor.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportUe4ssModAsync()
    {
        var paths = _dialogService.PickFiles("Select a UE4SS mod archive", "Mod archives (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|All files (*.*)|*.*");
        if (paths.Count == 0)
        {
            return;
        }

        var before = new HashSet<string>(SafeStagedUe4ssNames(), StringComparer.OrdinalIgnoreCase);
        await ImportPaths(paths); // reloads both panels
        var added = SafeStagedUe4ssNames().Where(n => !before.Contains(n)).ToList();
        Ue4ssStatusMessage = added.Count switch
        {
            0 => StatusMessage,
            1 => $"Staged '{added[0]}' — enable it below, then click Apply.",
            _ => $"Staged {added.Count} mods ({string.Join(", ", added)}) — enable them below, then click Apply.",
        };
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task EndorseUe4ssMod(Ue4ssModRowViewModel? row)
    {
        if (row?.NexusModId is not { } modId || row.IsEndorsed)
        {
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            StatusMessage = "Sign in with your Nexus API key in Settings to endorse mods.";
            return;
        }

        try
        {
            var info = await _nexusApiClient.GetModInfoAsync(apiKey, NxmHandlerCommand.IcarusDomain, modId);
            if (info is null)
            {
                StatusMessage = $"Couldn't reach '{row.Name}' on Nexus.";
                return;
            }

            row.EndorsementStatus = await _nexusApiClient.SetEndorsementAsync(apiKey, NxmHandlerCommand.IcarusDomain, modId, info.Version, endorse: true);
            StatusMessage = $"Endorsed '{row.Name}' on Nexus.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't endorse '{row.Name}': {ex.Message}";
        }
    }
}
