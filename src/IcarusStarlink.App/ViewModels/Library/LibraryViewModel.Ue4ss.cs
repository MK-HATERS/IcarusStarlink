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
                    row.NewestOnNexus = previous.NewestOnNexus;
                    row.EndorsementStatus = previous.EndorsementStatus;
                    row.IsOwnNexusMod = previous.IsOwnNexusMod;
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
                row.KeptVersion = meta.KeptVersion;
                row.HasConfig = ModHasConfig(state);
                row.ConfigNeedsReview = row.HasConfig && meta.ConfigReviews is { Count: > 0 };
                row.LocalVersion = ModLocalVersion(state);
                Ue4ssMods.Add(row);
            }
            RecomputeHasPendingUe4ssChanges();
            RefreshUe4ssLastRun();

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

    private const string Ue4ssGameRunningMessage =
        "Icarus is running, so its UE4SS Mods folder is in use. Close the game, then try again.";

    /// <summary>
    /// Whether Icarus is running (Merge &amp; Install's check; tests set their own). Apply and Delete
    /// move mod folders in and out of the game's Mods folder: with the game running, a C++ mod's dll
    /// is locked and a move stops halfway, and a Lua mod already loaded keeps running from memory.
    /// A failing check counts as not running, as it does for Install.
    /// </summary>
    internal Func<bool> IsGameRunningCheck
    {
        get => _isGameRunningCheck ??= () =>
        {
            try
            {
                return _mergeInstallViewModel().IsGameRunningNow();
            }
            catch (Exception)
            {
                return false;
            }
        };
        set => _isGameRunningCheck = value;
    }

    private Func<bool>? _isGameRunningCheck;

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
    private string? DeleteUe4ssModCore(string folderName) => DeleteUe4ssModCore(folderName, out _);

    /// <summary>The same, also saying whether it failed on a locked or read-only file.</summary>
    /// <param name="locked">True when closing whatever uses the mod's files may let it go through.</param>
    private string? DeleteUe4ssModCore(string folderName, out bool locked)
    {
        locked = false;
        // UE4SS's own shared\ (its Lua library) is never deleted, whichever path asks.
        if (Ue4ssModStart.IsFrameworkFolder(folderName))
        {
            return $"not deleted: {Ue4ssModStart.FrameworkFolderReason}";
        }

        try
        {
            var backedUp = false;
            var contentPath = _settingsService.Current.IcarusContentPath;
            if (!string.IsNullOrWhiteSpace(contentPath))
            {
                var modsFolder = Ue4ssGamePaths.ResolveModsFolder(contentPath);
                if (_ue4ssModRepository.ListInstalledInGame(modsFolder).Contains(folderName, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsGameRunningCheck())
                    {
                        return Ue4ssGameRunningMessage;
                    }

                    var failures = _ue4ssModStateService.Apply(modsFolder, new Dictionary<string, bool> { [folderName] = false }, _backupDirectory);
                    if (failures.Count > 0)
                    {
                        return failures[0].Reason;
                    }

                    backedUp = true; // Apply backed up the game copy before moving it out
                }
            }

            if (_ue4ssModRepository.GetAll().Contains(folderName, StringComparer.OrdinalIgnoreCase))
            {
                // Backed up first, as the game copy is above and every Library part is: a mod that
                // was off (only in staging) used to go for good, while the question said the parts
                // are backed up. One that can't be backed up isn't deleted. Backups\UE4SS, where
                // turning a mod off and replacing it keep their copies too.
                if (!backedUp)
                {
                    FolderBackup.BackupFolder(_ue4ssModRepository.GetFolderPath(folderName), Path.Combine(_backupDirectory, "UE4SS"));
                }

                _ue4ssModRepository.Delete(folderName);
            }

            _ue4ssModMetaStore.Delete(folderName);
            return null;
        }
        catch (Exception ex)
        {
            locked = ex is IOException or UnauthorizedAccessException;
            return ex.Message;
        }
    }

    /// <summary>The UE4SS tab's own Delete — the verb that was missing entirely (a real report: "I can't delete UE4SS mods inside ISL"). A package member, or a row linked to a data mod by name, goes through DeleteMods, so one question names every part and asks which halves go.</summary>
    [RelayCommand]
    private void DeleteUe4ssMod(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (Ue4ssModStart.IsFrameworkFolder(item.Name))
        {
            Ue4ssStatusMessage = $"'{item.Name}' is part of UE4SS (the Lua library its mods use), so it's always on and can't be deleted.";
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

        // Linked to a data row by name only: the same question as from the data row (DeleteMods
        // adds this half through the link). It used to delete this half alone. Only when the link
        // goes both ways: with two UE4SS rows named like one data mod, the data mod links to the
        // last of them, and routing the other one here offered that row instead of the one clicked.
        if (item.LinkedPartner is { } data && ReferenceEquals(data.LinkedPartner, item) && AllLibraryItems().Contains(data))
        {
            DeleteMods([data]);
            return;
        }

        // Not asked at all while the game holds its Mods folder: the delete would fail after the Yes.
        if (item.IsFolderInGame && IsGameRunningCheck())
        {
            Ue4ssStatusMessage = Ue4ssGameRunningMessage;
            return;
        }

        var confirm = _dialogService.Confirm(
            item.IsFolderInGame
                ? $"Delete '{item.Name}'?\n\nIt is removed from the game's Mods folder and from Starlink's holding folder. A copy is kept in Starlink's Backups folder."
                : $"Delete '{item.Name}'?\n\nIts staged folder is removed from disk. A copy is kept in Starlink's Backups folder.",
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

    /// <summary>
    /// The Del key on the UE4SS panel: one selected row is deleted (with the usual question);
    /// with several it says to pick one, as Del with two rows used to do nothing at all.
    /// </summary>
    public void DeleteSelectedUe4ssMods()
    {
        switch (BulkSelectedUe4ssRows.Count)
        {
            case 0:
                return;
            case 1:
                DeleteUe4ssModCommand.Execute(BulkSelectedUe4ssRows[0]);
                return;
            default:
                Ue4ssStatusMessage = "Del deletes one UE4SS mod at a time: select just one, or use its ⋯ menu.";
                return;
        }
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

    /// <summary>The same from a data mod's row whose download has a Lua half: that half's config.</summary>
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

            // What the last update changed in the config (settings added or gone), until the user says they've looked.
            var reviews = _ue4ssModMetaStore.Load(row.Name).ConfigReviews;
            var viewModel = new ModConfigEditorViewModel(row.Name, locations, _backupDirectory, reviews, markReviewed: () =>
            {
                var meta = _ue4ssModMetaStore.Load(row.Name);
                meta.ConfigReviews = null;
                _ue4ssModMetaStore.Save(row.Name, meta);
                row.ConfigNeedsReview = false;
            });
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

    /// <summary>Opens UE4SS.log, where UE4SS says which mods it started the last time the game ran.</summary>
    [RelayCommand]
    private void OpenUe4ssLog()
    {
        var contentPath = _settingsService.Current.IcarusContentPath;
        if (string.IsNullOrWhiteSpace(contentPath))
        {
            Ue4ssStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var logPath = Ue4ssGamePaths.ResolveLoaderLogPath(contentPath);
            if (!File.Exists(logPath))
            {
                Ue4ssStatusMessage = "No UE4SS.log yet: UE4SS writes it when the game starts. Play once, then try again.";
                return;
            }

            UrlOpener.TryOpen(logPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Ue4ssStatusMessage = $"Couldn't open UE4SS.log: {ex.Message}";
        }
    }

    /// <summary>
    /// Sets each UE4SS row's last-run badge from UE4SS.log (read shared, cached by its write time, so
    /// this is cheap on every reload and every time the page is shown again after a game run).
    /// </summary>
    private void RefreshUe4ssLastRun()
    {
        var contentPath = _settingsService.Current.IcarusContentPath;
        if (string.IsNullOrWhiteSpace(contentPath) || Ue4ssMods.Count == 0)
        {
            return;
        }

        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(contentPath);
            var log = Ue4ssLogStarts.Read(Ue4ssGamePaths.ResolveLoaderLogPath(contentPath));
            foreach (var row in Ue4ssMods)
            {
                row.LastRun = row.RealIsEnabled && !row.IsAlwaysOn
                    ? log.StateFor(row.Name, Ue4ssLogStarts.LastChanged(modsFolder, row.Name))
                    : Ue4ssLastRun.Unknown;
            }
        }
        catch (ArgumentException)
        {
            // A Content path with no parent folder: no log to read.
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

        if (IsGameRunningCheck())
        {
            Ue4ssStatusMessage = Ue4ssGameRunningMessage;
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

        await LinkUe4ssModToNexusCoreAsync(item, dialog.NexusModId);
    }

    /// <summary>LinkUe4ssModToNexus after the dialog: saves the link, then fills in the name and version from Nexus when signed in.</summary>
    internal async Task LinkUe4ssModToNexusCoreAsync(Ue4ssModRowViewModel item, int nexusModId)
    {
        try
        {
            var meta = _ue4ssModMetaStore.Load(item.Name);
            var linkChanged = meta.NexusModId != nexusModId;
            meta.NexusModId = nexusModId;
            if (linkChanged)
            {
                // The old mod's file is none of the new mod's files: kept, the update check compared
                // against it and an old "Update available" could never clear.
                meta.NexusFileId = null;
                meta.NexusFileVersion = null;
            }

            _ue4ssModMetaStore.Save(item.Name, meta);

            var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
            if (apiKey is not null)
            {
                try
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, NxmHandlerCommand.IcarusDomain, nexusModId);
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

            // The reload carries the update found on Nexus over by name; it was for the old mod.
            if (linkChanged && Ue4ssMods.FirstOrDefault(m => string.Equals(m.Name, item.Name, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                row.LatestVersion = null;
                row.LatestNexusFileId = null;
                row.NewestOnNexus = null;
            }

            Ue4ssStatusMessage = $"Linked '{item.Name}' to Nexus mod {nexusModId}.";
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
            description: "Type the oldest UE4SS version this mod works with (like \"3.0.1\"). Find it on the mod's Nexus page or readme: Starlink can't work it out by itself. If your UE4SS is older, the mod shows a warning.",
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
                : $"'{item.Name}' now requires UE4SS {VersionLabel.For(meta.MinUe4ssVersion)}+.";
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

    /// <summary>The thumb is greyed out once endorsed (it stayed clickable and did nothing), and never shown for the user's own upload.</summary>
    private static bool CanEndorseUe4ssMod(Ue4ssModRowViewModel? row) => row is { HasNexusLink: true, IsOwnNexusMod: false, IsEndorsed: false };

    [RelayCommand(FlowExceptionsToTaskScheduler = true, CanExecute = nameof(CanEndorseUe4ssMod))]
    private async Task EndorseUe4ssMod(Ue4ssModRowViewModel? row)
    {
        if (row?.NexusModId is not { } modId || row.IsEndorsed || row.IsOwnNexusMod)
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
            EndorseUe4ssModCommand.NotifyCanExecuteChanged();
            StatusMessage = $"Endorsed '{row.Name}' on Nexus.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't endorse '{row.Name}': {ex.Message}";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
        }
    }
}
