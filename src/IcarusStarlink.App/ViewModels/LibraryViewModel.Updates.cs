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
    /// The action behind the "Update available" badge. A Database-sourced mod genuinely updates in
    /// place: re-download from the catalog, then replace (delete-then-reimport under the same
    /// "Replace, not accumulate" rule Install/patch-import already use), carrying pin/favorite/
    /// notes across the swap. A Nexus-sourced mod jumps to its card on the native Nexus page
    /// instead — a real in-app Nexus download needs a file ID this app doesn't have (only the mod
    /// ID), so the honest action is the card whose own Download button starts a Mod Manager
    /// Download through the existing nxm:// pipeline (or its "Open page" action, for a non-Premium
    /// account that needs the real website).
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task GetUpdateAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (string.Equals(item.Source, "Nexus", StringComparison.OrdinalIgnoreCase))
        {
            // Nexus downloads persist in Pending Downloads (MO2-style) — if a file for this mod is
            // already sitting there UNCLAIMED, activating it is one click closer than a fresh
            // browser trip, so point there instead of the website. (Can't distinguish which version
            // that file is — PendingDownloadEntry carries no version — so this stays a pointer, not
            // an automatic activation of a possibly-older file.)
            //
            // "Unclaimed" specifically — not just "an entry for this mod ID exists at all" (a real
            // bug found live): downloads are never removed except by an explicit Discard, so ANY mod
            // ever downloaded from Nexus always has at least one PendingDownloadEntry sitting around
            // forever. The old check matched regardless of activation state, meaning once a mod had
            // been downloaded via this app even once, clicking Update on it could never again open
            // its Nexus page for a newer file — it always redirected to Pending Downloads instead,
            // even when the earlier download was already successfully activated and IS the mod
            // currently showing right here. Only a pending entry that ISN'T currently reflected as a
            // live Library item (never activated, or activated then since removed) is genuinely
            // "unclaimed" and worth pointing the user at instead of Nexus.
            var hasUnclaimedDownload = item.NexusModId is { } nexusModId && _pendingDownloadStore.Entries
                .Where(e => e.ModId == nexusModId)
                .Any(e => e.AllActivatedItems.Count == 0
                    || e.AllActivatedItems.Any(activated => activated.Kind == PendingDownloadActivationKind.Library
                        ? !_repository.GetAll().Any(le => string.Equals(le.FolderName, activated.FolderName, StringComparison.OrdinalIgnoreCase))
                        : !_ue4ssModRepository.GetAll().Any(f => string.Equals(f, activated.FolderName, StringComparison.OrdinalIgnoreCase))));
            if (hasUnclaimedDownload)
            {
                StatusMessage = "A downloaded file for this mod is already on the Downloads page — Activate or Reinstall it there (or re-download from Nexus if it's older than the update).";
                return;
            }

            SearchNexusFor(item);
            StatusMessage = "Found the mod's card on the Nexus page — use its Download button to pull the update through this app.";
            return;
        }

        await UpdateFromCatalogAsync(item, offerComparison: true);
    }

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

            StatusMessage = $"Updated '{imported.Name}' to v{imported.Version}.{result.Summary}";
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
    /// One action over the update list the last check produced. Database-sourced mods update in
    /// place (the same delete-then-reimport GetUpdateAsync does), one after another. Nexus-sourced
    /// ones can't be fetched by this app without a file ID, so instead of opening a browser tab per
    /// mod the Nexus page is shown with its "Updates only" filter on — every outdated mod's own
    /// Download button is right there.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task UpdateAllAsync()
    {
        var outdated = AllLibraryItems().Where(i => i.HasUpdateAvailable).ToList();
        if (outdated.Count == 0)
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
            if (await UpdateFromCatalogAsync(item, offerComparison: false))
            {
                updated++;
            }
        }

        if (nexusItems.Count > 0)
        {
            var nexus = _nexusCatalogViewModel();
            nexus.SearchText = "";
            nexus.UpdatesOnly = true;
            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
        }

        // Named here, not promised on the Nexus page: its "updates only" filter narrows only the page
        // of results it's on, so an outdated mod on another page wouldn't show there.
        var nexusNames = string.Join(", ", nexusItems.Select(i => i.Name));
        if (nexusItems.Count > 0)
        {
            _activityLog.Log($"Nexus mod(s) with updates to download: {nexusNames}.", ActivityEntryKind.Info);
        }

        var compareHint = updated > 0 ? " Right-click a mod › See what changed vs previous version to see what its author changed." : "";
        StatusMessage = (databaseItems.Count, nexusItems.Count) switch
        {
            (> 0, 0) => $"Updated {updated} of {databaseItems.Count} catalog mod(s).{compareHint}",
            (0, > 0) => $"{nexusItems.Count} Nexus mod(s) have updates: {nexusNames}. Download each from its Nexus page (right-click › Open on Nexus).",
            _ => $"Updated {updated} of {databaseItems.Count} catalog mod(s).{compareHint} {nexusItems.Count} Nexus mod(s) have updates: {nexusNames}. Download each from its Nexus page (right-click › Open on Nexus).",
        };
    }

    /// <summary>
    /// The spec's "Nexus search from a library row". A mod linked to Nexus is looked up by its mod
    /// ID — its Library name is often not its Nexus title, so a name search missed it (user report
    /// 2026-10-23: Update landed on an empty or wrong search). Unlinked mods still search by name.
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
    /// update-checking like anything activated through the real nxm:// pipeline. Best-effort
    /// enrichment afterward (real name/author/version via the API) mirrors
    /// DownloadsViewModel.EnrichOpaquePakFromNexusAsync's own two-step shape — a rejected/missing
    /// key just means the ID link itself still succeeds without the extra display data.
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
    private async Task LinkFolderToNexusAsync(string folderName, string displayName, int nexusModId)
    {
        try
        {
            _repository.LinkToNexus(folderName, nexusModId);

            var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
            if (apiKey is not null)
            {
                try
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", nexusModId);
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
    private async Task CheckEndorsements()
    {
        if (IsCheckingEndorsements)
        {
            return;
        }

        var nexusLinkedItems = AllLibraryItems().Where(i => i.HasNexusLink).ToList();
        var nexusLinkedLua = Ue4ssMods.Where(r => r.HasNexusLink && !r.IsBuiltIn).ToList();
        if (nexusLinkedItems.Count == 0 && nexusLinkedLua.Count == 0)
        {
            EndorsementCheckStatusMessage = "No Library mods are linked to a Nexus mod ID yet.";
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            EndorsementCheckStatusMessage = "Sign in with your Nexus API key in Settings first.";
            return;
        }

        IsCheckingEndorsements = true;
        try
        {
            var endorsements = await _nexusApiClient.GetEndorsementsAsync(apiKey);
            var statusByModId = endorsements
                .Where(e => string.Equals(e.DomainName, "icarus", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => e.ModId, e => e.Status);

            var notEndorsedCount = 0;
            foreach (var item in nexusLinkedItems)
            {
                item.EndorsementStatus = statusByModId.GetValueOrDefault(item.NexusModId!.Value, NexusEndorsementStatus.Undecided);
                if (item.EndorsementStatus != NexusEndorsementStatus.Endorsed)
                {
                    notEndorsedCount++;
                }
            }

            foreach (var row in nexusLinkedLua)
            {
                row.EndorsementStatus = statusByModId.GetValueOrDefault(row.NexusModId!.Value, NexusEndorsementStatus.Undecided);
                if (!row.IsEndorsed)
                {
                    notEndorsedCount++;
                }
            }

            var linkedCount = nexusLinkedItems.Count + nexusLinkedLua.Count;
            EndorsementCheckStatusMessage = notEndorsedCount == 0
                ? $"All {linkedCount} Nexus-linked mod(s) are endorsed."
                : $"{notEndorsedCount} of {linkedCount} Nexus-linked mod(s) aren't endorsed yet.";
        }
        catch (Exception ex)
        {
            EndorsementCheckStatusMessage = $"Couldn't check endorsements: {ex.Message}";
        }
        finally
        {
            IsCheckingEndorsements = false;
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
                    var (libraryUpdates, libraryFailures) = await ApplyNexusVersionsAsync(nexusItems, apiKey, i => i.NexusModId!.Value, (i, v) => i.LatestVersion = v, i => i.HasUpdateAvailable);
                    updatedCount += libraryUpdates;
                    failedCount += libraryFailures;
                    // Resolved against the LIVE row by name at apply time: a reload of the UE4SS list while
                    // the Nexus lookups were in flight replaces every row, and stamping the version onto
                    // the captured (now orphaned) instance left no badge until the next manual check.
                    var (ue4ssUpdates, ue4ssFailures) = await ApplyNexusVersionsAsync(ue4ssItems, apiKey, r => r.NexusModId!.Value,
                        (r, v) => (Ue4ssMods.FirstOrDefault(m => m.Name == r.Name) ?? r).LatestVersion = v,
                        r => (Ue4ssMods.FirstOrDefault(m => m.Name == r.Name) ?? r).HasUpdateAvailable);
                    updatedCount += ue4ssUpdates;
                    failedCount += ue4ssFailures;
                }
            }

            if (databaseItems.Count > 0)
            {
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

                    foreach (var item in databaseItems)
                    {
                        var catalogVersion = item.CatalogEntryId is not null && catalogVersionById.TryGetValue(item.CatalogEntryId, out var byId)
                            ? byId
                            : catalogVersionByKey.GetValueOrDefault(CatalogKey.Normalize(item.Name, item.Author));
                        if (catalogVersion is null)
                        {
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
                messages.Add(updatedCount > 0
                    ? $"{updatedCount} mod(s) have an update available."
                    : failedCount > 0 ? "No updates found among the mods that could be checked." : "Everything checked is up to date.");
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

        var sets = await Task.WhenAll(modIds.Select(async modId =>
        {
            try
            {
                return (ModId: modId, Set: (NexusModFileSet?)await _nexusApiClient.GetModFileSetAsync(apiKey, "icarus", modId));
            }
            catch (Exception)
            {
                return (ModId: modId, Set: (NexusModFileSet?)null); // best-effort, like the mod-level check
            }
        }));
        var setByMod = sets.Where(s => s.Set is not null).ToDictionary(s => s.ModId, s => s.Set!);

        var updates = 0;
        foreach (var item in trackedItems)
        {
            if (setByMod.TryGetValue(item.NexusModId!.Value, out var set) && NexusFileLineage.Newest(set, item.NexusFileId!.Value) is { } newest)
            {
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
                row.LatestVersion = newest.Version;
                row.LatestNexusFileId = newest.FileId;
                resolvedNames.Add(captured.Name);
                updates += row.HasUpdateAvailable ? 1 : 0;
            }
        }

        return (updates, resolvedItems, resolvedNames);
    }

    /// <summary>
    /// Batched per-item Nexus version lookup + apply, shared by CheckForUpdatesAsync's Library-mod
    /// and UE4SS-mod checks above — both used to be near-identical copies differing only in which
    /// property/type they touched. Returns how many of the checked items now have an update
    /// available, for the caller's own running total.
    /// </summary>
    private async Task<(int Updates, int Failures)> ApplyNexusVersionsAsync<T>(
        IReadOnlyList<T> items, string apiKey, Func<T, int> getNexusModId, Action<T, string> setLatestVersion, Func<T, bool> hasUpdateAvailable)
    {
        var results = await Task.WhenAll(items.Select(async item =>
        {
            try
            {
                var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", getNexusModId(item));
                return (Item: item, Version: info?.Version, Failed: info is null);
            }
            catch (Exception)
            {
                // Best-effort per-mod — one bad lookup (rate limit, a since-removed mod) shouldn't
                // stop the rest of the batch from checking. Counted as not checked.
                return (Item: item, Version: (string?)null, Failed: true);
            }
        }));

        var updatedCount = 0;
        var failedCount = results.Count(r => r.Failed);
        foreach (var (item, version, _) in results)
        {
            if (string.IsNullOrEmpty(version))
            {
                continue;
            }

            setLatestVersion(item, version);
            if (hasUpdateAvailable(item))
            {
                updatedCount++;
            }
        }

        return (updatedCount, failedCount);
    }
}
