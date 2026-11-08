using System.Globalization;
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
using IcarusStarlink.Catalog.GitHub;
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

/// <summary>Update checks, Get update / Update all, version compare, endorsements and Nexus links.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>
    /// The action behind the "Update available" badge. A Database-sourced mod updates in place:
    /// re-download from the catalog, then replace (delete-then-reimport under the same "Replace,
    /// not accumulate" rule Install/patch-import already use), carrying pin/favorite/notes across
    /// the swap. A Nexus-sourced mod goes straight to the choice of its Nexus files (the newer
    /// version of the installed file picked), then downloads and installs it in place of this copy
    /// (rather than opening the Nexus page, where Download had to be clicked again). Without a Nexus key, or a mod not linked by ID, its card on the Nexus page opens.
    /// Concurrent: an update waits on a file choice, a download and an install, and every other
    /// row's pill stays live meanwhile (installs take turns inside Downloads). A second click on the
    /// row that is already updating is refused.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true, AllowConcurrentExecutions = true)]
    private async Task GetUpdateAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var key = DataUpdateKey(item);
        if (!_updatesRunning.Add(key))
        {
            StatusMessage = $"'{item.Name}' is already updating.";
            return;
        }

        try
        {
            if (string.Equals(item.Source, "Nexus", StringComparison.OrdinalIgnoreCase))
            {
                if (item.NexusModId is { } nexusModId && Downloads.CanFetchFromNexus)
                {
                    if (RefuseWhileBusy(item))
                    {
                        return;
                    }

                    item.FlushPendingSave();
                    StatusMessage = $"Getting '{item.Name}' from Nexus…";
                    await FetchNexusUpdateAsync(nexusModId, item.Name);
                    StatusMessage = Downloads.PendingDownloadStatusMessage;
                    return;
                }

                SearchNexusFor(item);
                StatusMessage = Downloads.CanFetchFromNexus ? LinkToUpdateHere : SignInToUpdateHere;
                return;
            }

            await UpdateFromCatalogAsync(item, offerComparison: true);
        }
        finally
        {
            _updatesRunning.Remove(key);
        }
    }

    /// <summary>Update without a Nexus key: the mod's Nexus page opens, but its Download asks for the key too.</summary>
    private const string SignInToUpdateHere = "Sign in with your Nexus API key in Settings to update from here.";

    /// <summary>Update on a Nexus mod with no mod ID recorded: its Nexus page is searched by name.</summary>
    private const string LinkToUpdateHere = "Link this mod to its Nexus ID (right-click it) to update from here.";

    /// <summary>The rows whose update is running now ("data:" + folder name, "ue4ss:" + mod name): a second Update on one of them is refused.</summary>
    private readonly HashSet<string> _updatesRunning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many Nexus updates are choosing or fetching a file right now (from the pills or Update all).</summary>
    private int _nexusUpdatesRunning;

    private static string DataUpdateKey(LibraryItemViewModel item) => "data:" + item.FolderName;

    private static string Ue4ssUpdateKey(Ue4ssModRowViewModel row) => "ue4ss:" + row.Name;

    /// <summary>One Nexus update (file choice, download, install), counted while it runs so "Your build" can wait for it.</summary>
    /// <returns>True when its file choice was cancelled.</returns>
    private async Task<bool> FetchNexusUpdateAsync(int nexusModId, string name)
    {
        _nexusUpdatesRunning++;
        try
        {
            return await Downloads.ResolvePrimaryFileAndFetchAsync(nexusModId, name);
        }
        finally
        {
            _nexusUpdatesRunning--;
        }
    }

    private const string WaitBeforeReplacingYourBuild = "Wait for the update that's running to finish, then replace your build.";

    /// <summary>
    /// The "Your build" pill: this copy is newer than anything on Nexus (the author's own build).
    /// Clicking it swaps it for a Nexus version after asking, through the same file choice as Update.
    /// Not while another Nexus update runs: that update's file choice could open over this question.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true, AllowConcurrentExecutions = true)]
    private async Task ReplaceOwnBuildAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (_nexusUpdatesRunning > 0)
        {
            StatusMessage = WaitBeforeReplacingYourBuild;
            return;
        }

        if (ConfirmReplaceOwnBuild(item.Name, item.Version, item.NewestOnNexusShown))
        {
            await GetUpdateAsync(item);
        }
    }

    private bool ConfirmReplaceOwnBuild(string name, string? yours, string? nexus) =>
        _dialogService.ConfirmWithWords(
            $"'{name}' here is your own build ({VersionLabel.For(yours)}), newer than Nexus's latest ({VersionLabel.For(nexus)}).\n\nReplace it with a version from Nexus? You choose the file next; your build is backed up first (Backups).",
            "Replace your build", ThemedConfirmSeverity.Question, "Choose a Nexus version", "Keep my build");

    /// <summary>
    /// A Database mod's Get update: downloaded and installed through Downloads' own Activate (the
    /// same path as Download &amp; extract — author kept, the download classified, its requirements
    /// offered), which replaces the Library copy through ModReplacementService (backed up first,
    /// pin/notes/rename carried over, remembered fixes made again, the old copy put back on
    /// failure). True when the mod was updated.
    /// </summary>
    /// <param name="offerComparison">Ask "See what the author changed?" afterwards — not during Update all, which would ask once per mod.</param>
    private async Task<bool> UpdateFromCatalogAsync(LibraryItemViewModel item, bool offerComparison)
    {
        if (RefuseWhileBusy(item))
        {
            return false;
        }

        StatusMessage = $"Updating '{item.Name}'…";
        try
        {
            // Reuses Downloads' own cached catalog (fetching once if it hasn't yet) instead of an
            // independent re-fetch of both sources — the "Update available" badge that led here was
            // itself computed from a catalog check that already ran (CheckForUpdatesAsync, below),
            // so this isn't working from data any staler than what justified showing the button.
            var allEntries = await Downloads.GetOrFetchCatalogAsync();

            // Same ID-first, name-fallback matching CheckForUpdatesAsync itself uses.
            var catalogEntry = (item.CatalogEntryId is not null ? allEntries.FirstOrDefault(e => e.Id == item.CatalogEntryId) : null)
                ?? allEntries.FirstOrDefault(e => CatalogKey.Normalize(e.Name, e.Author) == CatalogKey.Normalize(item.Name, item.Author));
            if (catalogEntry is null)
            {
                StatusMessage = $"Couldn't find '{item.Name}' in the catalog anymore.";
                return false;
            }

            if ((catalogEntry.ExmodzUrl ?? catalogEntry.PakUrl) is null)
            {
                StatusMessage = $"'{catalogEntry.Name}' has no downloadable file listed.";
                return false;
            }

            var download = await Downloads.DownloadCatalogFileAsync(catalogEntry);
            // The download may have taken a while: a pass may have started or the editor opened.
            if (RefuseWhileBusy(item))
            {
                return false;
            }

            var folderName = item.FolderName;
            // Matched by name only: linked to the entry first, so Activate finds this copy and
            // replaces it (it looks for the previous copies by catalog entry) instead of adding a "_2".
            if (item.CatalogEntryId is null)
            {
                _repository.SetCatalogEntry(folderName, catalogEntry.Id);
            }

            // The notes typed a moment ago go into the copy that is backed up and carried over.
            item.FlushPendingSave();
            var result = await Downloads.ActivateAsync(download);
            var newFolder = result.Items.FirstOrDefault(i => i.Kind == PendingDownloadActivationKind.Library)?.FolderName;
            var imported = newFolder is null ? null
                : _repository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, newFolder, StringComparison.OrdinalIgnoreCase));
            if (!result.Succeeded || imported is null)
            {
                StatusMessage = result.Cancelled ? result.Message : $"Update failed: {result.Message ?? Downloads.PendingDownloadStatusMessage}";
                if (!result.Cancelled)
                {
                    _activityLog.Log($"Updating '{item.Name}' failed: {StatusMessage}", ActivityEntryKind.Warning);
                }

                return false;
            }

            StatusMessage = $"Updated '{imported.Name}'{(VersionLabel.For(imported.Version) is { Length: > 0 } version ? " to " + version : "")}.{result.Summary}";
            _activityLog.Log($"Updated '{imported.Name}' from the catalog.", ActivityEntryKind.Success);

            // A real bug found live: HasUpdateAvailable compares Version against LatestVersion
            // (set by CheckForUpdatesAsync from the CATALOG's own listed version string), but
            // imported.Version comes from the downloaded file's OWN internal metadata — a mod
            // author can bump one and not the other (e.g. catalog says "2.0", the .EXMODZ
            // itself still declares "2.0.0"), so even a fully successful update could leave
            // the two permanently unequal and the "Update available" badge stuck forever. The
            // file we just fetched and installed is, by definition, current as of right now —
            // syncing LatestVersion straight to what we actually just installed (rather than
            // whatever string the catalog separately happens to use) clears the badge
            // regardless of any such mismatch in the source data. On the row of the folder it
            // landed in: a reimport can land under a different folder name.
            var row = _itemsByFolderName.GetValueOrDefault(imported.FolderName) ?? item;
            row.LatestVersion = imported.Version;
            RefreshFilters();

            // The backup taken by the replacement is the mod's own previous version, so "what
            // did the author actually change?" is answerable right now — offered rather than
            // shown automatically. Passes BOTH folder names, since a reimport can land on a
            // different one than it started with (e.g. the mod's own declared fileName changed
            // between versions): the OLD folderName is where the backup was saved, but
            // imported.FolderName is where the CURRENT copy now lives on disk.
            if (offerComparison)
            {
                await OfferVersionComparisonAsync(imported.Name, folderName, imported.FolderName);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Failures before the swap starts (catalog fetch, download) — the mod on disk is
            // untouched at this point; the swap itself has its own backup-and-restore path.
            StatusMessage = $"Update failed: {ex.Message}";
            _activityLog.Log($"Updating '{item.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
            return false;
        }
    }

    /// <summary>Takes the user to this mod in the community Database (the Database page's IMM Database tab), pre-searched by name.</summary>
    [RelayCommand]
    private void FindInDatabase(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        Downloads.CatalogSearchText = item.Name;
        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("imm-database"));
    }

    /// <summary>
    /// One action over the update list the last check produced (data and UE4SS rows). Database mods
    /// update in place, one after another (the same path as their Update pill). Signed in with a
    /// Nexus key, each outdated Nexus data mod and UE4SS mod then runs its own Update in turn: the
    /// file choice opens for a mod with several files, cancelling it stops there, and the two halves
    /// of one download (same mod and file) are updated once. Without a key, or for an account Nexus
    /// says isn't Premium (it can't download through the API), the Nexus and UE4SS mods are only
    /// named, and the Nexus page opens with "Updates only" on when Nexus data mods are among them.
    /// One summary line at the end.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task UpdateAllAsync()
    {
        var outdated = AllLibraryItems().Where(i => i.HasUpdateAvailable).ToList();
        var outdatedLua = Ue4ssMods.Where(r => r.HasUpdateAvailable).ToList();
        if (outdated.Count == 0 && outdatedLua.Count == 0)
        {
            StatusMessage = "Nothing to update — run Check for updates first, or everything is already current.";
            return;
        }

        var databaseItems = outdated.Where(i => string.Equals(i.Source, "Database", StringComparison.OrdinalIgnoreCase)).ToList();
        var nexusItems = outdated.Where(i => string.Equals(i.Source, "Nexus", StringComparison.OrdinalIgnoreCase)).ToList();

        // Counted from what each update reports: a reimport can land in a new folder (a new row),
        // so the old row's badge says nothing about it. Not asking "See what the author changed?"
        // once per mod: right-click a mod and pick it to see that on demand.
        var updated = 0;
        foreach (var item in databaseItems)
        {
            var key = DataUpdateKey(item);
            if (!_updatesRunning.Add(key))
            {
                continue; // its own Update pill is running it
            }

            try
            {
                if (await UpdateFromCatalogAsync(item, offerComparison: false))
                {
                    updated++;
                }
            }
            finally
            {
                _updatesRunning.Remove(key);
            }
        }

        var parts = new List<string>();
        if (databaseItems.Count > 0)
        {
            var compareHint = updated > 0 ? " Right-click a mod › See what changed vs previous version to see what its author changed." : "";
            parts.Add($"Updated {updated} of {databaseItems.Count} catalog mod(s).{compareHint}");
        }

        if (nexusItems.Count > 0 || outdatedLua.Count > 0)
        {
            if (await CanRunNexusUpdatesInTurnAsync())
            {
                parts.Add(await RunNexusUpdatesInTurnAsync(nexusItems, outdatedLua));
            }
            else
            {
                parts.AddRange(NameNexusUpdates(nexusItems, outdatedLua));
            }
        }

        StatusMessage = string.Join(" ", parts);
    }

    /// <summary>
    /// Update all may run the Nexus updates itself: signed in, and Nexus doesn't say the account
    /// isn't Premium (only Premium accounts download through the API). Not known (the account
    /// lookup failed) counts as yes: each mod's own Update then says what went wrong. Downloads
    /// looks the account up once per sign-in, so a known answer costs no request.
    /// </summary>
    private async Task<bool> CanRunNexusUpdatesInTurnAsync()
    {
        if (!Downloads.CanFetchFromNexus || _credentialStore.Read(CredentialTargets.NexusApiKey) is null)
        {
            return false;
        }

        return await Downloads.CheckIsPremiumAsync() != false;
    }

    /// <summary>One outdated Nexus row for Update all: a data mod or a UE4SS mod.</summary>
    private sealed record NexusUpdateTarget(string Name, int ModId, int? FileId, string Key);

    /// <summary>
    /// Runs each outdated Nexus row's own Update, one after another, and returns the summary line.
    /// Rows without a Nexus mod ID are named only (their Update opens the Nexus page).
    /// </summary>
    private async Task<string> RunNexusUpdatesInTurnAsync(IReadOnlyList<LibraryItemViewModel> nexusItems, IReadOnlyList<Ue4ssModRowViewModel> outdatedLua)
    {
        var targets = new List<NexusUpdateTarget>();
        var notLinked = new List<string>();
        foreach (var item in nexusItems)
        {
            if (item.NexusModId is { } modId)
            {
                targets.Add(new(item.Name, modId, item.LatestNexusFileId ?? item.NexusFileId, DataUpdateKey(item)));
            }
            else
            {
                notLinked.Add(item.Name);
            }
        }

        targets.AddRange(outdatedLua
            .Where(r => r.NexusModId is not null)
            .Select(r => new NexusUpdateTarget(r.Name, r.NexusModId!.Value, r.LatestNexusFileId ?? r.NexusFileId, Ue4ssUpdateKey(r))));

        var started = new List<NexusUpdateTarget>();
        var ran = new List<string>();
        var notRun = new List<string>();
        string? stoppedAt = null;
        foreach (var target in targets)
        {
            if (stoppedAt is not null)
            {
                notRun.Add(target.Name);
                continue;
            }

            // The other half of a download already updated: same mod, same known file.
            if (started.Any(s => s.ModId == target.ModId && s.FileId is not null && s.FileId == target.FileId))
            {
                continue;
            }

            // Another download of a mod already updated, with no known file to tell them apart:
            // a second file choice for the same mod would be a guess, so it's named instead.
            if (started.Any(s => s.ModId == target.ModId && (s.FileId is null || target.FileId is null)))
            {
                notRun.Add(target.Name);
                continue;
            }

            if (!_updatesRunning.Add(target.Key))
            {
                continue; // its own Update pill is running it
            }

            bool cancelled;
            try
            {
                var item = nexusItems.FirstOrDefault(i => DataUpdateKey(i) == target.Key);
                if (item is not null && RefuseWhileBusy(item))
                {
                    notRun.Add(target.Name);
                    continue;
                }

                // Counted only once it really runs: a refused row's other half still gets its turn.
                started.Add(target);
                item?.FlushPendingSave();
                StatusMessage = $"Getting '{target.Name}' from Nexus… ({started.Count} of {targets.Count})";
                cancelled = await FetchNexusUpdateAsync(target.ModId, target.Name);
            }
            finally
            {
                _updatesRunning.Remove(target.Key);
            }

            if (cancelled)
            {
                stoppedAt = target.Name;
            }
            else
            {
                ran.Add(target.Name);
            }
        }

        var line = ran.Count > 0
            ? $"Ran Update on {ran.Count} Nexus mod(s): {string.Join(", ", ran)}. The Downloads page shows how each went."
            : "";
        if (stoppedAt is not null)
        {
            line += $" Stopped at '{stoppedAt}': its file choice was cancelled.";
        }

        notRun.AddRange(notLinked);
        if (notRun.Count > 0)
        {
            line += $" Not updated: {string.Join(", ", notRun)} (click Update on each row).";
        }

        if (ran.Count > 0)
        {
            _activityLog.Log($"Update all ran Update on: {string.Join(", ", ran)}.", ActivityEntryKind.Info);
        }

        return line.Trim();
    }

    /// <summary>The summary when Update all doesn't download from Nexus itself: the Nexus and UE4SS mods with updates, by name.</summary>
    private List<string> NameNexusUpdates(IReadOnlyList<LibraryItemViewModel> nexusItems, IReadOnlyList<Ue4ssModRowViewModel> outdatedLua)
    {
        var parts = new List<string>();
        if (nexusItems.Count > 0)
        {
            var nexus = _nexusCatalogViewModel();
            nexus.SearchText = "";
            nexus.UpdatesOnly = true;
            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));

            // Also in the Activity panel, so the names outlast the status line.
            var nexusNames = string.Join(", ", nexusItems.Select(i => i.Name));
            _activityLog.Log($"Nexus mod(s) with updates to download: {nexusNames}.", ActivityEntryKind.Info);
            parts.Add($"{nexusItems.Count} Nexus mod(s) have updates: {nexusNames}. Click Update on each row, or find them on the Nexus page (Updates only).");
        }

        if (outdatedLua.Count > 0)
        {
            // The Nexus page's Updates only lists Library data mods, not UE4SS mods.
            var luaNames = string.Join(", ", outdatedLua.Select(r => r.Name));
            _activityLog.Log($"UE4SS mod(s) with updates to download: {luaNames}.", ActivityEntryKind.Info);
            parts.Add($"{outdatedLua.Count} UE4SS mod(s) have updates: {luaNames}. Click Update on each row in the UE4SS panel.");
        }

        return parts;
    }

    /// <summary>
    /// Nexus search from a library row. A mod linked to Nexus is looked up by its mod
    /// ID — its Library name is often not its Nexus title, so a name search missed it (Update
    /// landed on an empty or wrong search). Unlinked mods still search by name.
    /// </summary>
    [RelayCommand]
    private void SearchNexusFor(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _nexusCatalogViewModel().SearchText = item.NexusModId is { } modId ? modId.ToString(CultureInfo.InvariantCulture) : item.Name;
        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
    }

    /// <summary>
    /// The UE4SS row's Update pill: the same as a Nexus data mod's Get update, straight to the
    /// choice of the mod's Nexus files; without a Nexus key, its card on the Nexus page. Concurrent
    /// and refused on a row already updating, like GetUpdate.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true, AllowConcurrentExecutions = true)]
    private async Task GetUe4ssUpdateAsync(Ue4ssModRowViewModel? row)
    {
        if (row?.NexusModId is not { } nexusModId)
        {
            return;
        }

        if (Downloads.CanFetchFromNexus)
        {
            var key = Ue4ssUpdateKey(row);
            if (!_updatesRunning.Add(key))
            {
                Ue4ssStatusMessage = $"'{row.Name}' is already updating.";
                return;
            }

            try
            {
                Ue4ssStatusMessage = $"Getting '{row.Name}' from Nexus…";
                await FetchNexusUpdateAsync(nexusModId, row.Name);
                Ue4ssStatusMessage = Downloads.PendingDownloadStatusMessage;
            }
            finally
            {
                _updatesRunning.Remove(key);
            }

            return;
        }

        _nexusCatalogViewModel().SearchText = nexusModId.ToString(CultureInfo.InvariantCulture);
        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
        Ue4ssStatusMessage = SignInToUpdateHere;
    }

    /// <summary>The UE4SS row's "Your build" pill: as ReplaceOwnBuild, for the Lua half.</summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true, AllowConcurrentExecutions = true)]
    private async Task ReplaceOwnUe4ssBuildAsync(Ue4ssModRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (_nexusUpdatesRunning > 0)
        {
            Ue4ssStatusMessage = WaitBeforeReplacingYourBuild;
            return;
        }

        if (ConfirmReplaceOwnBuild(row.Name, row.LocalVersion ?? row.KnownVersion, row.NewestOnNexusShown))
        {
            await GetUe4ssUpdateAsync(row);
        }
    }

    /// <summary>
    /// "See what the author changed" — compares this mod's most recent backup (its previous
    /// version, whether that backup came from an update or a manual Create mod backup) against
    /// what's installed now. Read-only: nothing is restored, moved, or written.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CompareToPreviousVersionAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await ShowVersionComparisonAsync(item.Name, item.FolderName, item.FolderName);
    }

    /// <summary>Asks first, then shows — the post-update half of the same comparison the context menu offers on demand.</summary>
    private async Task OfferVersionComparisonAsync(string modName, string backupFolderName, string currentFolderName)
    {
        var answer = _dialogService.Confirm(
            $"'{modName}' was updated.\n\nSee what the author changed between your old version and this one?",
            "Update installed", ThemedConfirmSeverity.Information);
        if (answer)
        {
            await ShowVersionComparisonAsync(modName, backupFolderName, currentFolderName);
        }
    }

    /// <summary>
    /// Two separate folder names on purpose, not one: <paramref name="backupFolderName"/> is where
    /// TryGetLatestModBackupPath looks for the earlier copy (the name it was backed up under),
    /// <paramref name="currentFolderName"/> is where GetFolderPath looks for the current one (the
    /// name it lives under on disk right now) — a post-update reimport can land on a different
    /// folder name than it started with, so these two can genuinely differ. CompareToPreviousVersionAsync
    /// passes the same name for both, since nothing has renamed the folder in that path.
    /// </summary>
    private async Task ShowVersionComparisonAsync(string modName, string backupFolderName, string currentFolderName)
    {
        var previousVersionPath = _repository.TryGetLatestModBackupPath(backupFolderName);
        if (previousVersionPath is null)
        {
            StatusMessage = $"There's no earlier copy of '{modName}' to compare against — this app only has one once it's updated or backed up (right-click → Create mod backup).";
            return;
        }

        StatusMessage = $"Comparing '{modName}' against its previous version…";
        try
        {
            var result = await _modVersionComparer.CompareAsync(
                previousVersionPath, _repository.GetFolderPath(currentFolderName), _settingsService.Current.UnrealPakExePath);

            var window = new ModVersionCompareWindow(new ModVersionCompareViewModel(modName, result))
            {
                Owner = Application.Current.MainWindow,
            };
            window.Show();

            StatusMessage = result.IsIdentical
                ? $"'{modName}' is identical to its previous version."
                : $"Showing what changed in '{modName}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't compare versions: {ex.Message}";
        }
    }

    /// <summary>
    /// Manually connects a Library entry (however it got here — a manual Import, or an unmatched
    /// entry from a future bulk import) to a real Nexus mod ID, so it starts participating in
    /// update-checking like anything activated through the real nxm:// pipeline.
    /// LinkFolderToNexusAsync saves the link first, then fills in the real name, author and version
    /// from Nexus as a best effort: a rejected or missing key still leaves the link in place.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task LinkToNexus(LibraryItemViewModel? item)
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

        await LinkFolderToNexusAsync(item.FolderName, item.Name, dialog.NexusModId);
    }

    /// <summary>Shared by LinkToNexus (the context-menu action on an already-imported mod) and ImportPak's own post-import prompt — records the ID, then best-effort-enriches real name/author/version from the API (a rejected/missing key just means the ID link itself still succeeds without the extra display data).</summary>
    internal async Task LinkFolderToNexusAsync(string folderName, string displayName, int nexusModId)
    {
        try
        {
            var previousModId = _repository.GetAll()
                .FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase))?.NexusModId;
            _repository.LinkToNexus(folderName, nexusModId);
            if (previousModId is not null && previousModId != nexusModId)
            {
                ForgetTheOldNexusMod(folderName);
            }

            var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
            if (apiKey is not null)
            {
                try
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, NxmHandlerCommand.IcarusDomain, nexusModId);
                    if (info is not null)
                    {
                        _repository.SetNexusMetadata(folderName, info.Name, info.Author, info.Summary, info.Version);
                    }
                }
                catch (Exception)
                {
                    // Best-effort — the ID link above already succeeded regardless.
                }
            }

            StatusMessage = $"Linked '{displayName}' to Nexus mod #{nexusModId}.";
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't link to Nexus: {ex.Message}";
        }
    }

    /// <summary>
    /// A link to another Nexus mod: what the last check found and the cached picture belonged to the
    /// old mod. Left in place, the old Update pill stayed (the per-file check can't clear it, the old
    /// file isn't in the new mod's files) and the old picture kept showing.
    /// </summary>
    private void ForgetTheOldNexusMod(string folderName)
    {
        if (_itemsByFolderName.GetValueOrDefault(folderName) is { } row)
        {
            row.LatestVersion = null;
            row.LatestNexusFileId = null;
            row.NewestOnNexus = null;
        }

        try
        {
            File.Delete(Path.Combine(_thumbnailCacheDirectory, folderName + ".img"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Couldn't delete the cached Nexus picture of {Folder}", folderName);
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task CheckForUpdates() => CheckForUpdatesAsync(isAutomatic: false);

    /// <summary>
    /// Fetches this account's whole endorsement history ONCE (GetEndorsementsAsync — there's no
    /// per-mod endpoint that would let this batch any other way) and stamps every Nexus-linked
    /// Library row with its real status — a mod absent from the returned list has never been
    /// touched (Undecided), matching Nexus's own implicit default. A separate, explicit action from
    /// CheckForUpdatesAsync (a different question — version-checking vs. "have I endorsed this"),
    /// same as CheckModsAgainstCurrentDataAsync already is.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task CheckEndorsements() => SyncEndorsementsAsync(quiet: false);

    /// <summary>The automatic endorsement sync has run this session (it runs once, the first time the Library is shown with a Nexus key).</summary>
    private bool _endorsementsSynced;

    /// <summary>
    /// The Library was shown: the first time this session (with a Nexus key), every row's thumb is
    /// filled in from Nexus without a click, so mods endorsed on the website show green. Quiet: no
    /// result line, and a failure just leaves the thumbs as they were.
    /// </summary>
    private void SyncEndorsementsOnce()
    {
        if (_endorsementsSynced || IsCheckingEndorsements || _credentialStore.Read(CredentialTargets.NexusApiKey) is null)
        {
            return;
        }

        _endorsementsSynced = true;
        _ = SyncEndorsementsAsync(quiet: true);
    }

    /// <summary>
    /// Stamps every Nexus-linked row (data and UE4SS) with this account's endorsement, and marks the
    /// mods this account uploaded itself (no thumb: Nexus refuses an author's own endorsement).
    /// Three calls whatever the Library's size: the account (for its member id), the whole
    /// endorsement history, and the linked mods' uploaders in one by-ID query.
    /// </summary>
    /// <param name="quiet">The automatic sync: no result or error line (Check endorsements shows both).</param>
    private async Task SyncEndorsementsAsync(bool quiet)
    {
        if (IsCheckingEndorsements)
        {
            return;
        }

        var nexusLinkedItems = AllLibraryItems().Where(i => i.HasNexusLink).ToList();
        var nexusLinkedLua = Ue4ssMods.Where(r => r.HasNexusLink && !r.IsBuiltIn).ToList();
        if (nexusLinkedItems.Count == 0 && nexusLinkedLua.Count == 0)
        {
            if (!quiet)
            {
                EndorsementCheckStatusMessage = "No Library mods are linked to a Nexus mod ID yet.";
            }

            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            if (!quiet)
            {
                EndorsementCheckStatusMessage = "Sign in with your Nexus API key in Settings first.";
            }

            return;
        }

        IsCheckingEndorsements = true;
        try
        {
            var endorsements = await _nexusApiClient.GetEndorsementsAsync(apiKey);
            var statusByModId = endorsements
                .Where(e => string.Equals(e.DomainName, NxmHandlerCommand.IcarusDomain, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => e.ModId, e => e.Status);
            var ownModIds = await FindOwnNexusModsAsync(apiKey,
                [.. nexusLinkedItems.Select(i => i.NexusModId!.Value).Concat(nexusLinkedLua.Select(r => r.NexusModId!.Value))]);

            var notEndorsedCount = 0;
            var ownCount = 0;
            foreach (var item in nexusLinkedItems)
            {
                item.IsOwnNexusMod = ownModIds.Contains(item.NexusModId!.Value);
                item.EndorsementStatus = statusByModId.GetValueOrDefault(item.NexusModId!.Value, NexusEndorsementStatus.Undecided);
                if (item.IsOwnNexusMod)
                {
                    ownCount++;
                }
                else if (item.EndorsementStatus != NexusEndorsementStatus.Endorsed)
                {
                    notEndorsedCount++;
                }
            }

            foreach (var row in nexusLinkedLua)
            {
                row.IsOwnNexusMod = ownModIds.Contains(row.NexusModId!.Value);
                row.EndorsementStatus = statusByModId.GetValueOrDefault(row.NexusModId!.Value, NexusEndorsementStatus.Undecided);
                if (row.IsOwnNexusMod)
                {
                    ownCount++;
                }
                else if (!row.IsEndorsed)
                {
                    notEndorsedCount++;
                }
            }

            // A UE4SS row's thumb is greyed out once endorsed; ask again now its status changed.
            EndorseUe4ssModCommand.NotifyCanExecuteChanged();

            if (!quiet)
            {
                var othersCount = nexusLinkedItems.Count + nexusLinkedLua.Count - ownCount;
                var own = ownCount == 0 ? "" : $" {ownCount} {(ownCount == 1 ? "is" : "are")} your own upload{(ownCount == 1 ? "" : "s")} (you can't endorse those).";
                EndorsementCheckStatusMessage = (othersCount == 0
                        ? "Every Nexus-linked mod here is your own upload."
                        : notEndorsedCount == 0
                            ? $"All {othersCount} Nexus-linked mod(s) are endorsed."
                            : $"{notEndorsedCount} of {othersCount} Nexus-linked mod(s) aren't endorsed yet.")
                    + (othersCount == 0 ? "" : own);
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                EndorsementCheckStatusMessage = $"Couldn't check endorsements: {ex.Message}";
            }
        }
        finally
        {
            IsCheckingEndorsements = false;
        }
    }

    /// <summary>The mods among <paramref name="modIds"/> that this account uploaded; empty when either lookup fails (the thumbs then simply show).</summary>
    private async Task<HashSet<int>> FindOwnNexusModsAsync(string apiKey, IReadOnlyList<int> modIds)
    {
        try
        {
            if (await _nexusApiClient.ValidateKeyAsync(apiKey) is not { UserId: > 0 } user)
            {
                return [];
            }

            var mods = await _nexusApiClient.GetModsByIdAsync(apiKey, NxmHandlerCommand.IcarusDomain, modIds);
            return [.. mods.Where(m => m.UploaderMemberId == user.UserId).Select(m => m.ModId)];
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Nexus-sourced mods get a real version lookup via the Nexus API (same GetModInfoAsync the
    /// Activate enrichment flow already uses), compared against each mod's own currently-known
    /// Version. Database-sourced mods cross-reference the live Daedalus+Jimk72 catalog by
    /// (Name, Author) — same CatalogKey normalization Export Patch already uses — and compare its
    /// Version field. Neither result is persisted; both are recomputed every call, matching
    /// Downloads' own Nexus watchlist precedent ("a real but coarse signal... not persisted").
    /// isAutomatic (the once-per-launch call from the constructor) suppresses status messages
    /// entirely — no "sign in first" nag just from opening the app, only from an explicit click.
    /// </summary>
    internal async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        var nexusItems = AllLibraryItems()
            .Where(i => string.Equals(i.Source, "Nexus", StringComparison.OrdinalIgnoreCase) && i.NexusModId is not null)
            .ToList();
        var databaseItems = AllLibraryItems()
            .Where(i => string.Equals(i.Source, "Database", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ue4ssItems = Ue4ssMods.Where(m => m.HasNexusLink).ToList();

        if (nexusItems.Count == 0 && databaseItems.Count == 0 && ue4ssItems.Count == 0)
        {
            return;
        }

        IsCheckingForUpdates = true;
        try
        {
            var updatedCount = 0;
            // Mods whose check itself failed (no network, the Nexus rate limit) — "up to date" would be a guess for them.
            var failedCount = 0;
            // Mods actually asked about (Nexus ones only with a key). "Up to date" is said only when
            // one of them got an answer: without a key, a Library of only Nexus mods had nothing checked.
            var askedCount = 0;
            var messages = new List<string>();

            if (nexusItems.Count > 0 || ue4ssItems.Count > 0)
            {
                var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
                if (apiKey is null)
                {
                    if (!isAutomatic)
                    {
                        messages.Add("Sign in with your Nexus API key in Settings to check Nexus-sourced mods.");
                    }
                }
                else
                {
                    askedCount += nexusItems.Count + ue4ssItems.Count;
                    // Same batched GetModInfoAsync pattern for both collections — a UE4SS mod's own
                    // "current version" is whatever KnownVersion recorded at link time, since
                    // there's no live-installed version string to read the way a Library EXMOD/pak
                    // entry has, but the check itself (fetch, compare, count) is identical.
                    // Mods that know their exact Nexus file are checked against that file's own
                    // update chain first; only the rest fall back to the mod's headline version.
                    var (perFileUpdates, resolvedItems, resolvedRows) = await ApplyNexusFileUpdatesAsync(nexusItems, ue4ssItems, apiKey);
                    updatedCount += perFileUpdates;
                    nexusItems = [.. nexusItems.Where(i => !resolvedItems.Contains(i))];
                    ue4ssItems = [.. ue4ssItems.Where(r => !resolvedRows.Contains(r.Name))];

                    // One lookup per mod: a data half and its Lua half share one Nexus mod ID.
                    var versions = await FetchModVersionsAsync(
                        nexusItems.Select(i => i.NexusModId!.Value).Concat(ue4ssItems.Select(r => r.NexusModId!.Value)), apiKey);
                    foreach (var item in nexusItems)
                    {
                        var (updated, failed) = ApplyModVersion(versions[item.NexusModId!.Value], v => item.LatestVersion = v, () => item.HasUpdateAvailable);
                        updatedCount += updated ? 1 : 0;
                        failedCount += failed ? 1 : 0;
                    }

                    // Resolved against the LIVE row by name at apply time: a reload of the UE4SS list while
                    // the Nexus lookups were in flight replaces every row, and stamping the version onto
                    // the captured (now orphaned) instance left no badge until the next manual check.
                    foreach (var captured in ue4ssItems)
                    {
                        var row = Ue4ssMods.FirstOrDefault(m => m.Name == captured.Name) ?? captured;
                        var (updated, failed) = ApplyModVersion(versions[captured.NexusModId!.Value], v => row.LatestVersion = v, () => row.HasUpdateAvailable);
                        updatedCount += updated ? 1 : 0;
                        failedCount += failed ? 1 : 0;
                    }
                }
            }

            if (databaseItems.Count > 0)
            {
                askedCount += databaseItems.Count;
                try
                {
                    // Reuses Downloads' own cached catalog instead of an independent re-fetch of
                    // both sources — see GetUpdateAsync's own comment above for why this is safe;
                    // GetOrFetchCatalogAsync fetches once if the cache is still empty, otherwise
                    // returns immediately.
                    var allEntries = await Downloads.GetOrFetchCatalogAsync();

                    // ID-first, name-fallback — same rename-safe matching Downloads'
                    // ApplyCatalogFilters now uses, for the same reason (a renamed/oddly-named mod
                    // still checks correctly when its stable CatalogEntryId was recorded at
                    // Download & extract time).
                    var catalogVersionById = new Dictionary<string, string>();
                    var catalogVersionByKey = new Dictionary<(string Name, string Author), string>();
                    foreach (var catalogEntry in allEntries)
                    {
                        catalogVersionById[catalogEntry.Id] = catalogEntry.Version;
                        catalogVersionByKey[CatalogKey.Normalize(catalogEntry.Name, catalogEntry.Author)] = catalogEntry.Version;
                    }

                    // A source that couldn't be reached may be the one listing a mod that wasn't found.
                    var catalogPartlyLoaded = Downloads.LastFailedCatalogSources.Count > 0;
                    foreach (var item in databaseItems)
                    {
                        var catalogVersion = item.CatalogEntryId is not null && catalogVersionById.TryGetValue(item.CatalogEntryId, out var byId)
                            ? byId
                            : catalogVersionByKey.GetValueOrDefault(CatalogKey.Normalize(item.Name, item.Author));
                        if (catalogVersion is null)
                        {
                            if (catalogPartlyLoaded)
                            {
                                failedCount++;
                            }

                            continue;
                        }

                        item.LatestVersion = catalogVersion;
                        if (item.HasUpdateAvailable)
                        {
                            updatedCount++;
                        }
                    }
                }
                catch (Exception)
                {
                    // Best-effort — a catalog fetch failure just means Database-sourced mods don't
                    // get checked this time, not a reason to fail the whole update check. Counted,
                    // so the result doesn't claim they're up to date.
                    failedCount += databaseItems.Count;
                }
            }

            if (!isAutomatic)
            {
                if (updatedCount > 0)
                {
                    messages.Add($"{updatedCount} mod(s) have an update available.");
                }
                else if (askedCount > failedCount)
                {
                    messages.Add(failedCount > 0 ? "No updates found among the mods that could be checked." : "Everything checked is up to date.");
                }

                if (failedCount > 0)
                {
                    messages.Add($"Couldn't check {failedCount} mod(s) (network or Nexus limit) — try again later.");
                }

                UpdateCheckStatusMessage = string.Join(" ", messages);
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
            // The Updates chip counts what was just found.
            RefreshFilters();
        }
    }

    /// <summary>
    /// Per-file update check: one files request per Nexus mod, then each tracked copy follows its
    /// own file's update chain (NexusFileLineage) — so with several variants of one mod installed or
    /// known, each is judged on its own file. Returns how many have an update and which items/rows
    /// were resolved this way (the rest fall back to the mod-level check).
    /// </summary>
    private async Task<(int Updates, HashSet<LibraryItemViewModel> Items, HashSet<string> Ue4ssNames)> ApplyNexusFileUpdatesAsync(
        IReadOnlyList<LibraryItemViewModel> items, IReadOnlyList<Ue4ssModRowViewModel> rows, string apiKey)
    {
        var resolvedItems = new HashSet<LibraryItemViewModel>();
        var resolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trackedItems = items.Where(i => i.NexusFileId is not null).ToList();
        var trackedRows = rows.Where(r => r.NexusFileId is not null && r.NexusModId is not null).ToList();
        var modIds = trackedItems.Select(i => i.NexusModId!.Value).Concat(trackedRows.Select(r => r.NexusModId!.Value)).Distinct().ToList();
        if (modIds.Count == 0)
        {
            return (0, resolvedItems, resolvedNames);
        }

        var sets = await WhenAllCappedAsync(modIds, async modId =>
        {
            try
            {
                return (ModId: modId, Set: (NexusModFileSet?)await _nexusApiClient.GetModFileSetAsync(apiKey, NxmHandlerCommand.IcarusDomain, modId));
            }
            catch (Exception)
            {
                return (ModId: modId, Set: (NexusModFileSet?)null); // best-effort, like the mod-level check
            }
        });
        var setByMod = sets.Where(s => s.Set is not null).ToDictionary(s => s.ModId, s => s.Set!);

        var updates = 0;
        foreach (var item in trackedItems)
        {
            if (setByMod.TryGetValue(item.NexusModId!.Value, out var set) && NexusFileLineage.Newest(set, item.NexusFileId!.Value) is { } newest)
            {
                item.NewestOnNexus = UpdateVersionComparer.Highest(set.Files.Select(f => f.Version));
                item.LatestVersion = newest.Version;
                item.LatestNexusFileId = newest.FileId;
                resolvedItems.Add(item);
                updates += item.HasUpdateAvailable ? 1 : 0;
            }
        }

        foreach (var captured in trackedRows)
        {
            // The live row by name, for the same reason as the mod-level check below.
            var row = Ue4ssMods.FirstOrDefault(m => m.Name == captured.Name) ?? captured;
            if (setByMod.TryGetValue(captured.NexusModId!.Value, out var set) && NexusFileLineage.Newest(set, captured.NexusFileId!.Value) is { } newest)
            {
                row.NewestOnNexus = UpdateVersionComparer.Highest(set.Files.Select(f => f.Version));
                row.LatestVersion = newest.Version;
                row.LatestNexusFileId = newest.FileId;
                resolvedNames.Add(captured.Name);
                updates += row.HasUpdateAvailable ? 1 : 0;
            }
        }

        return (updates, resolvedItems, resolvedNames);
    }

    /// <summary>
    /// The mod-level check: each distinct mod's headline version from Nexus, a few requests at a
    /// time. A mod whose lookup failed (rate limit, a since-removed mod) is marked Failed; one bad
    /// lookup doesn't stop the rest.
    /// </summary>
    private async Task<Dictionary<int, (string? Version, bool Failed)>> FetchModVersionsAsync(IEnumerable<int> modIds, string apiKey)
    {
        var results = await WhenAllCappedAsync(modIds.Distinct(), async modId =>
        {
            try
            {
                var info = await _nexusApiClient.GetModInfoAsync(apiKey, NxmHandlerCommand.IcarusDomain, modId);
                return (ModId: modId, Version: info?.Version, Failed: info is null);
            }
            catch (Exception)
            {
                return (ModId: modId, Version: (string?)null, Failed: true);
            }
        });
        return results.ToDictionary(r => r.ModId, r => (r.Version, r.Failed));
    }

    /// <summary>Applies one mod's looked-up version to a row: whether the row now has an update, and whether its lookup failed.</summary>
    private static (bool Updated, bool Failed) ApplyModVersion((string? Version, bool Failed) lookup, Action<string> setLatestVersion, Func<bool> hasUpdateAvailable)
    {
        if (lookup.Failed)
        {
            return (false, true);
        }

        if (string.IsNullOrEmpty(lookup.Version))
        {
            return (false, false);
        }

        setLatestVersion(lookup.Version);
        return (hasUpdateAvailable(), false);
    }

    /// <summary>
    /// At most this many Nexus requests at once during a check: the client has no limiter of its
    /// own, and every request still in flight when Nexus starts refusing (its rate limit) fails.
    /// </summary>
    private const int MaxNexusRequestsAtOnce = 4;

    /// <summary>Task.WhenAll over the sources, running at most MaxNexusRequestsAtOnce at a time; results in source order.</summary>
    private static async Task<TResult[]> WhenAllCappedAsync<TSource, TResult>(IEnumerable<TSource> sources, Func<TSource, Task<TResult>> run)
    {
        using var gate = new SemaphoreSlim(MaxNexusRequestsAtOnce);
        return await Task.WhenAll(sources.Select(async source =>
        {
            await gate.WaitAsync();
            try
            {
                return await run(source);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
