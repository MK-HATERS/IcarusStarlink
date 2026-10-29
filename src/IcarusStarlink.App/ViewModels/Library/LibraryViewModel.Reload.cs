using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Building the two panels: Reload (search, sort, filters, download stubs), the cached rows and families, and OnPageShown.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>
    /// fullResync is true only for the explicit Refresh() command and the LibraryChangedMessage
    /// handler (another page changed mods on disk) — every other caller (the
    /// search debounce, import, delete, a pin toggle) reloads because the *set* of visible items
    /// changed, not because any individual still-visible mod's own data did, so those paths must
    /// leave already-cached LibraryItemViewModel instances alone rather than re-syncing them.
    /// Doing that unconditionally on every reload was tried and reverted: it re-applied whatever
    /// Notes/Pinned/Favorite happened to be in the repository's last-saved snapshot over top of
    /// values the user might still be mid-typing (Notes saves on a 500ms debounce), and it wiped
    /// every still-selected mod's cached Files/Readme/thumbnail on every keystroke of a search,
    /// forcing a redundant disk re-read — defeating the exact caching GetOrCreateItem exists for.
    /// </summary>
    private void Reload(bool fullResync = false)
    {
        var previouslySelectedFolder = SelectedItem?.FolderName;

        var unsortedGroups = VariantGrouping.Group(_repository.Search(SearchText));

        // Default (no column header clicked yet): pinned mods (or a family with any pinned
        // member) sort first, matching the real app's "pinned mods sort to the top" — then
        // alphabetical within each of those two bands. Clicking a sortable header switches
        // entirely to that column (dropping the pinned-first grouping) — matching Explorer's own
        // "clicking a header takes over sorting" convention, on the assumption that a user who
        // deliberately picked a sort column wants exactly that order, not a pinned-first override.
        // A family's own representative value (Entries[0], already ordered by SortVariants) stands
        // in for Author/Version/Source/Imported, which are real per-entry fields VariantGroup
        // itself has no single value for.
        var groups = SortColumn switch
        {
            "Author" => SortGroups(unsortedGroups, g => g.Entries[0].Author, StringComparer.OrdinalIgnoreCase),
            "Version" => SortGroups(unsortedGroups, g => g.Entries[0].Version, StringComparer.OrdinalIgnoreCase),
            "Source" => SortGroups(unsortedGroups, g => g.Entries[0].Source ?? "", StringComparer.OrdinalIgnoreCase),
            "Imported" => SortGroups(unsortedGroups, g => g.Entries[0].ImportedAtUtc),
            "Name" => SortGroups(unsortedGroups, g => g.DisplayName, StringComparer.OrdinalIgnoreCase),
            _ => [.. unsortedGroups
                .OrderByDescending(g => g.Entries.Any(e => e.IsPinned))
                .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)],
        };

        ModCount = groups.Sum(g => g.Entries.Count);
        GroupCount = groups.Count(g => g.IsFamily);
        BuildPackages();

        // A folder that's visibly on disk but unreadable (corrupt EXMOD, locked file) was only
        // ever explained in the log file — surfaced here so "why isn't my mod showing?" has an
        // answer in the UI itself.
        UnreadableFoldersMessage = _repository.UnreadableFolders.Count > 0
            ? $"{_repository.UnreadableFolders.Count} folder(s) couldn't be read and aren't shown: {string.Join(", ", _repository.UnreadableFolders)}. Check the mod's own files (a corrupt .EXMOD is the usual cause), or see the log."
            : null;

        var seenGroupKeys = new HashSet<string>();
        var targetRootItems = new List<object>();

        // A download in flight shows as a stub row at the top — no folder on disk yet to have
        // become a real LibraryEntry, so it can't come from _repository.Search above. It disappears
        // the moment the download finishes (success or failure) via the tracker's own CollectionChanged
        // triggering another Reload — a successful one is replaced by the real imported entry via
        // the LibraryChangedMessage DownloadsViewModel already sends right after activating it.
        foreach (var download in _activeDownloadsTracker.Current)
        {
            targetRootItems.Add(new DownloadStubViewModel(download.Key, download.DisplayName));
        }

        // One list for every kind of mod: pak/EXMOD rows (groups) and UE4SS rows side by side.
        // A UE4SS mod that is the Lua half of a Mods-tab row (same Nexus download) is folded into
        // that row and not listed on its own. Default/Name order interleaves both kinds by name
        // (pinned families first, as before); any other sort keeps the UE4SS rows after the groups.
        var libraryRows = new List<(string SortName, bool Pinned, object Row)>();
        foreach (var group in groups)
        {
            if (!MatchesTypeFilter(group))
            {
                continue;
            }

            var items = group.Entries.Select(entry => GetOrCreateItem(entry, fullResync)).ToList();
            var pinned = group.Entries.Any(e => e.IsPinned);
            if (group.IsFamily)
            {
                seenGroupKeys.Add(group.GroupKey);
                libraryRows.Add((group.DisplayName, pinned, GetOrCreateGroup(group.GroupKey, group.DisplayName, items)));
            }
            else
            {
                libraryRows.Add((group.DisplayName, pinned, items[0]));
            }
        }

        var ue4ssRows = TypeFilter is "All" or "UE4SS"
            ? Ue4ssMods.Where(row => (ShowUe4ssBuiltIns || !row.IsBuiltIn) && MatchesSearch(row)).Select(row => (SortName: row.Name, Pinned: false, Row: (object)row)).ToList()
            : [];

        // The filter chips need each row's current status (On, not in the game yet…) and count
        // over what the search found; the chip on then narrows both panels. A family stays when
        // any of its mods matches.
        EnsureMergeQueueSubscription();
        RefreshEnabledAndStatus();
        var searchedItems = ExpandGroups(libraryRows.Select(r => r.Row)).OfType<LibraryItemViewModel>().ToList();
        var searchedLua = ue4ssRows.Select(r => (Ue4ssModRowViewModel)r.Row).ToList();
        _searchedItems = searchedItems;
        _searchedLua = searchedLua;
        UpdateFilterCounts(searchedItems, searchedLua);
        if (ListFilter != FilterAll)
        {
            libraryRows = [.. libraryRows.Where(r => r.Row switch
            {
                LibraryGroupViewModel family => family.Items.Any(i => MatchesListFilter(i, ListFilter)),
                LibraryItemViewModel item => MatchesListFilter(item, ListFilter),
                _ => true,
            })];
            ue4ssRows = [.. ue4ssRows.Where(r => MatchesListFilter((Ue4ssModRowViewModel)r.Row, ListFilter))];
        }

        // Left panel: downloads in progress, then the data mods (pinned first in the default
        // order). Right panel: the UE4SS mods by name. RootItems keeps both, data first, for the
        // whole-list actions (Select all, Prev/Next in the detail window).
        var targetDataItems = new List<object>(targetRootItems);
        if (SortColumn is null)
        {
            targetDataItems.AddRange(libraryRows.Where(r => r.Pinned).Select(r => r.Row));
            targetDataItems.AddRange(libraryRows.Where(r => !r.Pinned).OrderBy(r => r.SortName, StringComparer.OrdinalIgnoreCase).Select(r => r.Row));
        }
        else
        {
            targetDataItems.AddRange(libraryRows.Select(r => r.Row));
        }

        var luaInOrder = SortColumn == "Name" && SortDescending
            ? ue4ssRows.OrderByDescending(r => r.SortName, StringComparer.OrdinalIgnoreCase)
            : ue4ssRows.OrderBy(r => r.SortName, StringComparer.OrdinalIgnoreCase);
        var targetLuaItems = luaInOrder.Select(r => r.Row).ToList();

        Ue4ssBuiltInCount = Ue4ssMods.Count(row => row.IsBuiltIn);
        ObservableCollectionSync.SyncTo(DataRootItems, targetDataItems);
        ObservableCollectionSync.SyncTo(Ue4ssRootItems, targetLuaItems);
        SyncRootItems([.. targetDataItems, .. targetLuaItems]);
        DataShownCount = ExpandGroups(targetDataItems).Count();
        Ue4ssShownCount = targetLuaItems.Count;
        OnPropertyChanged(nameof(IsUe4ssPanelEmpty));
        OnPropertyChanged(nameof(IsDataPanelEmpty));
        OnPropertyChanged(nameof(IsListNarrowed));
        OnPropertyChanged(nameof(DataEmptyText));
        OnPropertyChanged(nameof(Ue4ssPanelEmptyText));
        LinkHalves();
        OnPropertyChanged(nameof(IsAllSelectedForBulk));
        EnsureMergeQueueSubscription();
        RefreshEnabledAndStatus();

        // Drop cached instances for mods/families no longer present/matching, so these don't grow
        // forever. FlushPendingSave() first, not CancelPendingSave() — the mod's folder isn't going
        // anywhere here (unlike DeleteSelected), so a pending Notes edit should be saved a little
        // early, not silently discarded; without this, an orphaned timer could fire later and write
        // the edit to disk after a fresh instance for the same mod (built from the now-stale
        // repository read) has already replaced this one, leaving the visible Notes field stale
        // relative to what's actually on disk.
        // Evicted only when the mod is GONE from the repository, not merely hidden by the current
        // search filter (an audit finding): whole-library checks run over AllLibraryItems() below,
        // and a hidden row must keep its fetched "Update available" badge until the filter clears.
        var liveFolders = _repository.GetAll().Select(e => e.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var staleFolderName in _itemsByFolderName.Keys.Where(k => !liveFolders.Contains(k)).ToList())
        {
            _itemsByFolderName[staleFolderName].FlushPendingSave();
            _itemsByFolderName.Remove(staleFolderName);
        }

        foreach (var staleGroupKey in _groupsByKey.Keys.Except(seenGroupKeys).ToList())
        {
            _groupsByKey.Remove(staleGroupKey);
        }

        SelectedItem = previouslySelectedFolder is not null && _itemsByFolderName.TryGetValue(previouslySelectedFolder, out var stillSelected)
            ? stillSelected
            : null;

        // OnSelectedItemChanged only fires on an actual reference change, but SelectedItem above
        // is frequently reassigned to the exact same cached instance it already was (nothing
        // selection-worthy changed) — that path still needs EnsureDetailsLoaded() run explicitly
        // so a just-applied Update() (which clears AssetPaths/ReadmeContent/ThumbnailImage and
        // resets _detailsLoaded) actually reloads the still-selected mod's details right away,
        // rather than only the next time the user reselects it.
        SelectedItem?.EnsureDetailsLoaded();

        // New rows, or mods that may have changed on disk: rate the game files they replace again.
        // Not from the constructor's first load: the app sets BaseGameContent just after it.
        _fileRisksStale |= fullResync;
        if (_fileRisksStale && BaseGameContent is not null)
        {
            _fileRisksStale = false;
            _ = RefreshFileRisksAsync();
        }
    }

    /// <summary>The Library page is showing (MainViewModel): rates the game files mods replace, if not done since mods changed.</summary>
    public void OnPageShown()
    {
        // Steam may have updated the game while another page was open.
        _ = CheckForGameUpdateAsync();

        if (_fileRisksStale && BaseGameContent is not null)
        {
            _fileRisksStale = false;
            _ = RefreshFileRisksAsync();
        }
    }

    /// <summary>A row was added since the last risk pass (set in GetOrCreateItem).</summary>
    private bool _fileRisksStale = true;

    /// <summary>
    /// Updates RootItems to match `target` via targeted Remove/Insert/Move rather than
    /// Clear()+re-Add(): WPF's TreeView regenerates every container from scratch on the Reset
    /// notification Clear() raises, even for items whose object identity didn't change — which
    /// was collapsing every expanded family (and losing the selection highlight) on every
    /// debounced search reload despite GetOrCreateGroup/GetOrCreateItem already reusing the same
    /// instances. LibraryGroupViewModel.Update applies the same fix one level down, for a
    /// family's own children.
    /// </summary>
    private void SyncRootItems(IReadOnlyList<object> target) => ObservableCollectionSync.SyncTo(RootItems, target);

    /// <summary>
    /// Every mod in the Library, regardless of the current search filter — the set every
    /// "whole-library" action (Check for updates, Check mods against game data, Convert opaque
    /// mods, endorsements, Delete all) is documented to operate on. They used to run over the
    /// visible rows only (an audit finding): "No possibly stale items remain" while filtered to
    /// "armor" said nothing about the other 40 mods. Rows for hidden mods are created on demand and
    /// kept (see Reload's eviction rule).
    /// </summary>
    private List<LibraryItemViewModel> AllLibraryItems() =>
        _repository.GetAll().Select(entry => GetOrCreateItem(entry, fullResync: false)).ToList();

    /// <summary>
    /// Reusing the same LibraryItemViewModel instance across reloads (rather than always
    /// constructing a new one) means a mod that's still selected after a search-triggered reload
    /// keeps its already-loaded Files/Readme state (no redundant disk I/O) and stays the same
    /// object reference SelectedItem points at.
    /// </summary>
    private LibraryItemViewModel GetOrCreateItem(LibraryEntry entry, bool fullResync)
    {
        if (_itemsByFolderName.TryGetValue(entry.FolderName, out var existing))
        {
            // Only on an explicit Refresh() — see the fullResync doc comment on Reload() for why
            // this can't run on every reload.
            if (fullResync)
            {
                existing.Update(entry);
            }

            return existing;
        }

        // onPinnedChanged: pinned status drives Reload()'s sort order, but toggling it only
        // updates the repository/cache in place — nothing else re-runs that ordering. Without
        // this, a pin saves correctly but the row silently stays wherever it already was in the
        // tree until some unrelated change (a search edit, a delete) happens to trigger the next
        // Reload().
        var created = new LibraryItemViewModel(
            entry, _repository, _unrealPakService, _uassetTextureDecoder, _uassetStaticMeshDecoder,
            _uassetSkeletalMeshDecoder, _uassetSoundDecoder, _uassetMaterialDecoder,
            _opaquePakAssetPreviewService, _settingsService,
            _nexusApiClient, _credentialStore,
            _downloadHttpClient, _thumbnailCacheDirectory, _pakPreviewCacheDirectory, Downloads.GetOrFetchCatalogAsync,
            status => StatusMessage = status, () => Reload());
        created.EnabledToggled += OnItemEnabledToggled;
        created.DataFolder = _dataFolder;
        _fileRisksStale = true;
        _itemsByFolderName[entry.FolderName] = created;
        return created;
    }

    /// <summary>
    /// Same instance-reuse rationale as GetOrCreateItem, but for family headers: reusing the
    /// LibraryGroupViewModel by GroupKey (not DisplayName — a search that narrows which variants
    /// match can't change the key) keeps the TreeView's expanded/collapsed state across a
    /// debounced search reload instead of collapsing every family on each keystroke. DisplayName
    /// is still refreshed on every call (via Update, not just at construction) since
    /// VariantGrouping can derive it from a different member entry between reloads — e.g. after
    /// the member that supplied it is deleted.
    /// </summary>
    private LibraryGroupViewModel GetOrCreateGroup(string groupKey, string displayName, IReadOnlyList<LibraryItemViewModel> items)
    {
        if (!_groupsByKey.TryGetValue(groupKey, out var group))
        {
            group = new LibraryGroupViewModel(displayName);
            _groupsByKey[groupKey] = group;
        }

        group.Update(displayName, items);
        return group;
    }
}
