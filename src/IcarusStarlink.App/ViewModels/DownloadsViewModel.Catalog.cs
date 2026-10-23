using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Catalog;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.ViewModels;

// The IMM Database catalog browser: fetching, filtering and Download & extract.
public sealed partial class DownloadsViewModel
{
    private IReadOnlyList<CatalogEntry> _allCatalogEntries = [];

    // Reused across ApplyCatalogFilters calls so a search keystroke or a checkbox toggle doesn't
    // force the DataGrid to fully reset/regenerate every row container just because a brand-new
    // CatalogEntryViewModel object was constructed for a row that didn't actually change — cleared
    // whenever _allCatalogEntries itself is replaced by a real catalog refresh.
    private readonly Dictionary<CatalogEntry, (string? InstalledVersion, DateTimeOffset? LastUpdated, CatalogEntryViewModel ViewModel)> _catalogRowCache = [];

    // Keyed by (owner, repo), not by mod — see GitHubRepoDateClient's own doc comment for why
    // that's a deliberate repo-level, not per-file, granularity. Only ever replaced on a
    // *successful* RefreshCatalogAsync fetch (see there) so a transient GitHub failure on a later
    // refresh doesn't wipe out dates a previous successful refresh already resolved.
    private IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset> _repoPushedDates =
        new Dictionary<(string, string), DateTimeOffset>();

    // --- IMM Database tab ---
    public ObservableCollection<CatalogEntryViewModel> CatalogEntries { get; } = [];
    public ObservableCollection<string> AvailableAuthors { get; } = [AllAuthors];
    public ObservableCollection<string> AvailableCategories { get; } = [AllCategories];

    private const string AllAuthors = "All authors";
    private const string AllCategories = "All categories";

    [ObservableProperty]
    private string _catalogSearchText = "";

    [ObservableProperty]
    private string _selectedAuthor = AllAuthors;

    [ObservableProperty]
    private string _selectedCategory = AllCategories;

    [ObservableProperty]
    private bool _showUpdatesOnly;

    [ObservableProperty]
    private bool _extractedOnly;

    [ObservableProperty]
    private bool _notDownloadedOnly;

    /// <summary>0 = unset. Deliberately not defaulted to "whatever week it happened to be when this was written" — that goes stale the moment it's wrong, so HideOlderWeeks starts off until the user sets a real value.</summary>
    [ObservableProperty]
    private int _gameWeek;

    [ObservableProperty]
    private bool _hideOlderWeeks;

    /// <summary>The IMM Database tab's Filters section header: the filters now narrowing the list
    /// (search not included — it's always visible), or "None". Set by ApplyCatalogFilters, which
    /// every filter change runs.</summary>
    [ObservableProperty]
    private string _catalogFilterSummary = "None";

    [ObservableProperty]
    private CatalogEntryViewModel? _selectedCatalogEntry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogEmptyStateText))]
    private bool _isLoadingCatalog;

    [ObservableProperty]
    private string? _catalogStatusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogEmptyStateText))]
    private int _catalogShownCount;

    /// <summary>What the empty IMM Database list says instead of showing blank rows: loading, nothing loaded yet, or nothing matching. Null while rows show.</summary>
    public string? CatalogEmptyStateText => CatalogShownCount > 0 ? null
        : IsLoadingCatalog ? "Loading mods from the IMM Database…"
        : CatalogTotalCount == 0 ? "No mods loaded yet — press Refresh."
        : "No mods match — clear the search or the filters.";

    /// <summary>DataGrid.SelectedItems isn't a bindable DependencyProperty — set from ImmDatabaseView's own code-behind SelectionChanged handler, same limitation/workaround the EXMOD editor's mass-edit selection already uses.</summary>
    private IReadOnlyList<CatalogEntryViewModel> _selectedCatalogEntriesForBatch = [];

    [ObservableProperty]
    private int _selectedCatalogEntryCount;

    /// <summary>Drives the batch-download button's visibility — a plain int bound through NullToVisibilityConverter would always read "has a value" (an int is never null), the same bug class already caught once this session, so this is a real bool instead.</summary>
    public bool HasCatalogSelectionForBatch => SelectedCatalogEntryCount >= 2;

    partial void OnSelectedCatalogEntryCountChanged(int value) => OnPropertyChanged(nameof(HasCatalogSelectionForBatch));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogEmptyStateText))]
    private int _catalogTotalCount;

    [ObservableProperty]
    private bool _isColumnsMenuOpen;

    // Mod Name has no toggle of its own — always shown, same as Explorer's "Name" column.
    [ObservableProperty]
    private bool _showAuthorColumn;

    [ObservableProperty]
    private bool _showVersionColumn;

    [ObservableProperty]
    private bool _showInstalledVersionColumn;

    [ObservableProperty]
    private bool _showCompatibilityColumn;

    [ObservableProperty]
    private bool _showCategoryColumn;

    [ObservableProperty]
    private bool _showStatusColumn;

    [ObservableProperty]
    private bool _showLastUpdatedColumn;

    partial void OnCatalogSearchTextChanged(string value) => _searchDebounceTimer.Restart();

    partial void OnSelectedAuthorChanged(string value) => ApplyCatalogFilters();
    partial void OnSelectedCategoryChanged(string value) => ApplyCatalogFilters();
    partial void OnShowUpdatesOnlyChanged(bool value) => ApplyCatalogFilters();
    partial void OnExtractedOnlyChanged(bool value) => ApplyCatalogFilters();
    partial void OnNotDownloadedOnlyChanged(bool value) => ApplyCatalogFilters();
    partial void OnGameWeekChanged(int value) => ApplyCatalogFilters();
    partial void OnHideOlderWeeksChanged(bool value) => ApplyCatalogFilters();

// Immediate-save, not debounced: these are discrete checkbox clicks, not rapid-repeat
    // keystrokes like Notes/Name — same "save right away" precedent MainViewModel's own theme
    // selection already established for a silent UI preference with no dedicated Save button.
    partial void OnShowAuthorColumnChanged(bool value) => SaveColumnPreferences();
    partial void OnShowVersionColumnChanged(bool value) => SaveColumnPreferences();
    partial void OnShowInstalledVersionColumnChanged(bool value) => SaveColumnPreferences();
    partial void OnShowCompatibilityColumnChanged(bool value) => SaveColumnPreferences();
    partial void OnShowCategoryColumnChanged(bool value) => SaveColumnPreferences();
    partial void OnShowStatusColumnChanged(bool value) => SaveColumnPreferences();
    partial void OnShowLastUpdatedColumnChanged(bool value) => SaveColumnPreferences();

    private void SaveColumnPreferences()
    {
        _settingsService.Current.CatalogShowAuthorColumn = ShowAuthorColumn;
        _settingsService.Current.CatalogShowVersionColumn = ShowVersionColumn;
        _settingsService.Current.CatalogShowInstalledVersionColumn = ShowInstalledVersionColumn;
        _settingsService.Current.CatalogShowCompatibilityColumn = ShowCompatibilityColumn;
        _settingsService.Current.CatalogShowCategoryColumn = ShowCategoryColumn;
        _settingsService.Current.CatalogShowStatusColumn = ShowStatusColumn;
        _settingsService.Current.CatalogShowLastUpdatedColumn = ShowLastUpdatedColumn;
        _settingsService.Save();
    }

    [RelayCommand]
    private void ToggleColumnsMenu() => IsColumnsMenuOpen = !IsColumnsMenuOpen;

    /// <summary>
    /// Shared read access to the same catalog snapshot this page's own table renders from — added so
    /// LibraryViewModel's Get update/Check for updates paths (Database-sourced mods) stop
    /// independently re-fetching both catalog sources over the network on every single call, a
    /// second full round-trip of the exact same data this VM already fetches and caches at startup.
    /// Returns the cache as-is if already populated; otherwise awaits one real fetch first — avoids
    /// a race against RefreshCatalogAsync's own fire-and-forget constructor call, which may not have
    /// completed yet by the time a caller elsewhere asks for this early in app startup.
    /// </summary>
    public async Task<IReadOnlyList<CatalogEntry>> GetOrFetchCatalogAsync()
    {
        if (_allCatalogEntries.Count == 0)
        {
            // The load already under way (the constructor's, or another caller's) is awaited, not
            // started again — two callers early in startup used to fetch both catalogs twice. A
            // finished load that came back empty (offline) is retried.
            if (_catalogLoad is null || _catalogLoad.IsCompleted)
            {
                _catalogLoad = LoadCatalogAsync();
            }

            await _catalogLoad;
        }

        return _allCatalogEntries;
    }

    private Task? _catalogLoad;

    /// <summary>The author the loaded catalog lists for this entry, or null (not loaded yet, or no longer listed).</summary>
    private string? CatalogAuthorOf(string? catalogEntryId) =>
        catalogEntryId is null ? null : _allCatalogEntries.FirstOrDefault(e => e.Id == catalogEntryId)?.Author;

    /// <summary>How old a snapshot may be and still answer the automatic load; Refresh always goes to the network.</summary>
    private static readonly TimeSpan CatalogSnapshotMaxAge = TimeSpan.FromHours(6);

    private readonly ICatalogSnapshotStore? _catalogSnapshotStore;

    /// <summary>
    /// The automatic load (constructor, first GetOrFetchCatalogAsync): a fresh-enough on-disk
    /// snapshot answers it instantly with no network — both catalogs and the ~40 GitHub repo dates
    /// used to be re-fetched on every launch, which is also what kept tripping GitHub's 60/hour cap.
    /// The Refresh button is wired to RefreshCatalogAsync directly, so it always fetches.
    /// </summary>
    private async Task LoadCatalogAsync()
    {
        var snapshot = _catalogSnapshotStore?.TryLoad();
        if (snapshot is not null && snapshot.Entries.Count > 0 && DateTimeOffset.UtcNow - snapshot.FetchedAtUtc < CatalogSnapshotMaxAge)
        {
            ApplyFetchedCatalog(snapshot.Entries);
            _repoPushedDates = snapshot.RepoDates.ToDictionary(d => (d.Owner, d.Repo), d => d.PushedAtUtc);
            var age = DateTimeOffset.UtcNow - snapshot.FetchedAtUtc;
            var ageText = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago" : $"{(int)age.TotalHours} h ago";
            CatalogStatusMessage = $"Loaded {_allCatalogEntries.Count} mods (cached {ageText}) — Refresh fetches the latest.";
            ApplyCatalogFilters();
            return;
        }

        await RefreshCatalogAsync();
    }

    /// <summary>
    /// Everything that has to happen when a new set of entries becomes THE catalog: the row cache
    /// (bound to grow unboundedly otherwise — CatalogEntry has value equality, so a changed entry
    /// would just add another key), and the author/category pickers. Clearing/rebuilding those
    /// disrupts the ComboBoxes' own SelectedItem tracking (WPF can reset a bound SelectedItem to
    /// null mid-rebuild), so the prior selection is captured and restored (or falls back to
    /// "All…") — a null SelectedAuthor once made the filter match nothing: "0 shown / 624 in catalog".
    /// </summary>
    private void ApplyFetchedCatalog(IReadOnlyList<CatalogEntry> entries)
    {
        _allCatalogEntries = [.. entries];
        _catalogRowCache.Clear();

        var previousAuthor = SelectedAuthor;
        var previousCategory = SelectedCategory;

        AvailableAuthors.Clear();
        AvailableAuthors.Add(AllAuthors);
        foreach (var author in _allCatalogEntries.Select(e => e.Author).Distinct().OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            AvailableAuthors.Add(author);
        }

        AvailableCategories.Clear();
        AvailableCategories.Add(AllCategories);
        foreach (var category in _allCatalogEntries.SelectMany(e => e.Categories).Distinct().OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            AvailableCategories.Add(category);
        }

        SelectedAuthor = AvailableAuthors.Contains(previousAuthor) ? previousAuthor : AllAuthors;
        SelectedCategory = AvailableCategories.Contains(previousCategory) ? previousCategory : AllCategories;
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task RefreshCatalogAsync()
    {
        IsLoadingCatalog = true;
        CatalogStatusMessage = "Fetching catalog…";

        try
        {
            using var perfScope = _performanceTracker.Track("RefreshCatalog");
            // Each source is isolated the same way GitHubRepoDateClient already isolates its own
            // per-repo fetches — one source being down (a transient outage, a renamed endpoint)
            // shouldn't discard the other source's already-successful results via a shared
            // Task.WhenAll failure.
            var failedSources = new List<string>();
            var daedalusTask = CatalogSourceFetch.FetchAsync(_daedalusClient.FetchAsync, "Daedalus", failedSources);
            var jimk72Task = CatalogSourceFetch.FetchAsync(_jimk72Client.FetchAsync, "Jimk72", failedSources);
            await Task.WhenAll(daedalusTask, jimk72Task);

            // Both sources down (offline, say) must not replace a good in-memory catalog with an
            // empty one — CatalogSourceFetch swallows each failure into [], so the "a failed fetch
            // leaves the catalog unchanged" reasoning only held for the exception path (an audit
            // finding: Refresh while offline emptied a 600-row table).
            if (failedSources.Count == 2 && _allCatalogEntries.Count > 0)
            {
                CatalogStatusMessage = "Couldn't reach Daedalus or Jimk72 — showing the catalog from the last successful load.";
                return;
            }

            ApplyFetchedCatalog([.. daedalusTask.Result, .. jimk72Task.Result]);

            CatalogStatusMessage = failedSources.Count > 0
                ? $"Loaded {_allCatalogEntries.Count} mods — {string.Join(" and ", failedSources)} unavailable, try Refresh again."
                : $"Loaded {_allCatalogEntries.Count} mods.";
            _activityLog.Log($"Catalog refreshed — {_allCatalogEntries.Count} mods.", failedSources.Count > 0 ? ActivityEntryKind.Warning : ActivityEntryKind.Info);
            // Inside the try, not after the whole try/catch/finally: this method is invoked
            // fire-and-forget (`_ = RefreshCatalogAsync();`) from the constructor specifically
            // because its own top-level try/catch was meant to guarantee no unobserved exception
            // could escape — that guarantee doesn't hold for anything called outside the try.
            // Only needed on the success path: a failed fetch leaves _allCatalogEntries (and so
            // the already-rendered CatalogEntries) unchanged, nothing to re-apply filters over.
            ApplyCatalogFilters();

            // Best-effort enrichment layered on top of the core catalog data above, deliberately
            // not blocking it: rows render (Last Updated blank) as soon as the catalog itself is
            // ready, then get a second ApplyCatalogFilters() pass once repo dates resolve a moment
            // later, rather than making the whole table wait on ~40 extra GitHub API round-trips.
            try
            {
                var repoKeys = _allCatalogEntries
                    .Select(e => GitHubRepoKey.Extract(e.PakUrl ?? e.ExmodzUrl))
                    .Where(key => key is not null)
                    .Select(key => key!.Value)
                    .Distinct()
                    .ToList();
                // Merged over what's already known, not replaced (an audit finding): the client omits
                // any repo whose lookup failed (GitHub's 60/hour unauthenticated cap bites on a 40-repo
                // refresh), so a wholesale replace blanked dates a previous refresh had resolved.
                var fetched = await _gitHubRepoDateClient.FetchPushedDatesAsync(repoKeys);
                var merged = new Dictionary<(string Owner, string Repo), DateTimeOffset>(_repoPushedDates);
                foreach (var (key, pushedAt) in fetched)
                {
                    merged[key] = pushedAt;
                }

                _repoPushedDates = merged;
                ApplyCatalogFilters();
            }
            catch (Exception)
            {
                // Same best-effort rule GitHubRepoDateClient applies per-repo, one level up: leave
                // _repoPushedDates exactly as it was (possibly populated by a prior successful
                // refresh, possibly still empty) rather than blanking known-good dates because
                // this particular refresh's GitHub lookup failed outright (offline, GitHub API
                // down, rate-limited). The core catalog above is entirely unaffected either way.
            }

            // Only a fully successful fetch is worth caching — a half catalog would answer the
            // next launch's automatic load with the missing source silently absent.
            if (failedSources.Count == 0)
            {
                _catalogSnapshotStore?.Save(new CatalogSnapshot(
                    DateTimeOffset.UtcNow, _allCatalogEntries,
                    [.. _repoPushedDates.Select(kv => new RepoPushedDate(kv.Key.Owner, kv.Key.Repo, kv.Value))]));
            }
        }
        catch (Exception ex)
        {
            // A live-internet fetch can fail for many reasons (offline, endpoint moved, rate
            // limited, malformed response) — same UI-boundary rule as everywhere else in this
            // app: show it, don't crash.
            CatalogStatusMessage = $"Catalog fetch failed: {ex.Message}";
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    [RelayCommand]
    private void ClearCatalogFilters()
    {
        CatalogSearchText = "";
        SelectedAuthor = AllAuthors;
        SelectedCategory = AllCategories;
        ShowUpdatesOnly = false;
        ExtractedOnly = false;
        NotDownloadedOnly = false;
        HideOlderWeeks = false;
        // ApplyCatalogFilters already runs once per property reset above (each has its own
        // OnXChanged hook); the explicit call here only matters if every value above happened to
        // already equal its default (no OnXChanged would fire), so filters still get re-applied.
        ApplyCatalogFilters();
    }

    // Downloaded/Outdated status matches by the catalog's own stable entry ID first
    // (LibraryEntry.CatalogEntryId, recorded at Download & extract time — survives a Rename and a
    // mod whose internal name differs from the catalog's curated display name, e.g. the real
    // "Aberiu's Supply Crates" vs its EXMOD's own "Aberiu_Supply_Crates"), then falls back to the
    // original (Name, Author) normalization for anything imported before the ID existed or brought
    // in manually rather than through Download & extract.
    private void ApplyCatalogFilters()
    {
        var previouslySelectedId = SelectedCatalogEntry?.Entry.Id;

        var libraryEntries = _libraryRepository.GetAll();
        var libraryByCatalogId = libraryEntries
            .Where(e => e.CatalogEntryId is not null)
            .GroupBy(e => e.CatalogEntryId!)
            .ToDictionary(g => g.Key, g => g.First().Version);
        var libraryByKey = libraryEntries
            .GroupBy(e => CatalogKey.Normalize(e.Name, e.Author))
            .ToDictionary(g => g.Key, g => g.First().Version);

        IEnumerable<CatalogEntry> query = _allCatalogEntries;

        if (!string.IsNullOrWhiteSpace(CatalogSearchText))
        {
            var search = CatalogSearchText.Trim();
            query = query.Where(e =>
                e.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Author.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Description.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedAuthor != AllAuthors)
        {
            query = query.Where(e => e.Author == SelectedAuthor);
        }

        if (SelectedCategory != AllCategories)
        {
            query = query.Where(e => e.Categories.Contains(SelectedCategory));
        }

        if (HideOlderWeeks && GameWeek > 0)
        {
            query = query.Where(e => e.CompatibleWeek is null || e.CompatibleWeek >= GameWeek);
        }

        CatalogEntryViewModel GetOrCreateRow(CatalogEntry e)
        {
            var installedVersion = libraryByCatalogId.GetValueOrDefault(e.Id) ?? libraryByKey.GetValueOrDefault(CatalogKey.Normalize(e.Name, e.Author));
            DateTimeOffset? lastUpdated = GitHubRepoKey.Extract(e.PakUrl ?? e.ExmodzUrl) is { } repoKey ? _repoPushedDates.GetValueOrDefault(repoKey) : null;

            if (_catalogRowCache.TryGetValue(e, out var cached) && cached.InstalledVersion == installedVersion && cached.LastUpdated == lastUpdated)
            {
                return cached.ViewModel;
            }

            var viewModel = new CatalogEntryViewModel(e, installedVersion, lastUpdated);
            _catalogRowCache[e] = (installedVersion, lastUpdated, viewModel);
            return viewModel;
        }

        var rows = query
            .Select(GetOrCreateRow)
            .Where(row => !ShowUpdatesOnly || row.IsOutdated)
            .Where(row => !ExtractedOnly || row.IsDownloaded)
            .Where(row => !NotDownloadedOnly || !row.IsDownloaded)
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Targeted Remove/Insert/Move instead of Clear()+re-Add(): a row whose CatalogEntryViewModel
        // instance is unchanged (see GetOrCreateRow's cache above) keeps its identity, so the
        // DataGrid doesn't regenerate every container just because one filter checkbox flipped.
        ObservableCollectionSync.SyncTo(CatalogEntries, rows);

        CatalogShownCount = rows.Count;
        CatalogTotalCount = _allCatalogEntries.Count;
        CatalogFilterSummary = BuildCatalogFilterSummary();

        // A row whose installed version or date changed is a new CatalogEntryViewModel, so without this the DataGrid's
        // two-way SelectedItem binding just drops back to null on every filter/search change (or
        // after Download & extract re-applies filters) even when the same mod still matches —
        // the detail pane and its buttons would disappear for no reason the user did anything
        // wrong.
        SelectedCatalogEntry = previouslySelectedId is null
            ? null
            : CatalogEntries.FirstOrDefault(row => row.Entry.Id == previouslySelectedId);
    }

    private string BuildCatalogFilterSummary()
    {
        var parts = new List<string>();
        if (SelectedAuthor != AllAuthors)
        {
            parts.Add($"Author: {SelectedAuthor}");
        }

        if (SelectedCategory != AllCategories)
        {
            parts.Add($"Category: {SelectedCategory}");
        }

        if (ShowUpdatesOnly)
        {
            parts.Add("Updates only");
        }

        if (ExtractedOnly)
        {
            parts.Add("Downloaded only");
        }

        if (NotDownloadedOnly)
        {
            parts.Add("Not downloaded only");
        }

        if (HideOlderWeeks && GameWeek > 0)
        {
            parts.Add($"Week {GameWeek} and newer");
        }

        return parts.Count == 0 ? "None" : string.Join(" · ", parts);
    }

    /// <summary>
    /// Downloads the given catalog row, or the selected one when none is given. The "More details"
    /// pop-out passes its own row: it is non-modal, so the grid's selection may have moved on to
    /// another mod since it opened (that used to download the wrong mod).
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DownloadAndExtractAsync(CatalogEntryViewModel? entry)
    {
        entry ??= SelectedCatalogEntry;
        if (entry is null)
        {
            return;
        }

        var (success, message) = await DownloadAndExtractEntryAsync(entry);
        CatalogStatusMessage = message;
        if (success)
        {
            // Activate already told the Library (and this page) that it changed.
            ApplyCatalogFilters();
        }
    }

    /// <summary>
    /// Downloads and imports one catalog entry — shared by the single-entry Download &amp; extract
    /// button and DownloadAndExtractSelectedAsync's own batch loop, since the two are otherwise
    /// identical work. Deliberately doesn't itself refresh filters/send LibraryChangedMessage — a
    /// batch of N imports refreshing once at the end (not N times mid-loop) is both cheaper and
    /// avoids the DataGrid's own selection getting reset out from under a loop still in progress
    /// (see ApplyCatalogFilters' own selection-by-ID restore, which a mid-batch rebuild would
    /// otherwise fight with the still-multi-selected rows).
    /// </summary>
    private async Task<(bool Success, string Message)> DownloadAndExtractEntryAsync(CatalogEntryViewModel entry)
    {
        var catalogEntry = entry.Entry;
        if ((catalogEntry.ExmodzUrl ?? catalogEntry.PakUrl) is null)
        {
            return (false, $"'{catalogEntry.Name}' has no downloadable file listed.");
        }

        try
        {
            var justDownloaded = await DownloadCatalogFileAsync(catalogEntry);
            var result = await ActivateAsync(justDownloaded);
            if (!result.Succeeded)
            {
                return (false, PendingDownloadStatusMessage ?? $"'{catalogEntry.Name}' downloaded but didn't import — see the Downloads page.");
            }

            _activityLog.Log($"Downloaded and imported '{catalogEntry.Name}' from the catalog.", ActivityEntryKind.Success);
            return (true, $"Downloaded and imported '{catalogEntry.Name}'.");
        }
        catch (OperationCanceledException)
        {
            return (false, $"Cancelled downloading '{catalogEntry.Name}'.");
        }
        catch (Exception ex)
        {
            return (false, $"'{catalogEntry.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads a catalog entry's file into Pending_Downloads and records it like any other
    /// download (not installed yet) — so Reinstall original and the Downloads page know the file
    /// a Database mod came from, Library's Get update included. Throws when it can't.
    /// </summary>
    public async Task<PendingDownloadItemViewModel> DownloadCatalogFileAsync(CatalogEntry catalogEntry)
    {
        var downloadUrl = catalogEntry.ExmodzUrl ?? catalogEntry.PakUrl
            ?? throw new InvalidOperationException($"'{catalogEntry.Name}' has no downloadable file listed.");

        // A real catalog entry can point at a github.com "view this file" page instead of its raw
        // content (a common submission mistake — see GitHubBlobUrl's own doc comment) — downloading
        // that HTML page and treating it as an archive fails opaquely, so this is normalized before
        // the download below.
        downloadUrl = GitHubBlobUrl.ToRawContentUrl(downloadUrl);

        var isExmodz = catalogEntry.ExmodzUrl is not null;
        // The file lands under Pending_Downloads and is recorded like a Nexus download, so the
        // Downloads page is the one place every archive lives (Reinstall / Discard work the same)
        // — the way a mod organizer keeps its downloads, per direct user feedback. The file is
        // named after the catalog entry: a .pak that can't be converted is imported as an opaque
        // entry whose name derives from the pak's own file name (an audit finding).
        Directory.CreateDirectory(_pendingDownloadsDirectory);
        var fileId = PendingDownloadEntry.CatalogFileId(catalogEntry.Id);
        var fileName = DownloadFileNameSanitizer.Sanitize($"{catalogEntry.Name}{(isExmodz ? ".EXMODZ" : ".pak")}");
        fileName = DownloadFileNameSanitizer.ResolveUniqueFileName(
            _pendingDownloadsDirectory, fileName, 0, fileId,
            path => _pendingDownloadStore.Entries.Any(e => e.ModId == 0 && e.FileId == fileId && e.LocalFilePath == path));
        var localPath = Path.Combine(_pendingDownloadsDirectory, fileName);

        // Streamed to disk through a .part file, same as the Nexus path (an audit finding):
        // GetByteArrayAsync buffered the whole pak in memory AND put the entire transfer under
        // HttpClient's 100 s timeout, so a large catalog pak on a slow connection failed every time.
        var cancellationToken = BeginDownload();
        try
        {
            await HttpFileDownloader.DownloadToFileAsync(_downloadHttpClient, [downloadUrl], localPath, HttpFileDownloader.DefaultIdleTimeout, cancellationToken);
        }
        finally
        {
            EndDownload();
        }

        // catalogEntryId is the Database counterpart of the Nexus pipeline's nexusModId — the
        // stable link that keeps the Downloaded badge and update-checking working after a
        // Rename, which name-matching alone can't survive.
        _pendingDownloadStore.Add(new PendingDownloadEntry
        {
            ModId = 0,
            FileId = fileId,
            FileName = fileName,
            LocalFilePath = localPath,
            DownloadedAtUtc = DateTimeOffset.UtcNow,
            Source = PendingDownloadEntry.DatabaseSource,
            CatalogEntryId = catalogEntry.Id,
            DisplayName = catalogEntry.Name,
            Author = catalogEntry.Author,
        });
        ReloadPendingDownloads();

        return PendingDownloads.FirstOrDefault(p => p.ModId == 0 && p.FileId == fileId)
            ?? throw new InvalidOperationException($"'{catalogEntry.Name}' downloaded but couldn't be recorded.");
    }

    /// <summary>Called from LibraryView's own DataGrid.SelectionChanged code-behind (the IMM Database tab lives there now) — DataGrid.SelectedItems isn't a bindable DependencyProperty, same limitation the EXMOD editor's own mass-edit selection already works around.</summary>
    public void SetSelectedCatalogEntries(IReadOnlyList<CatalogEntryViewModel> entries)
    {
        _selectedCatalogEntriesForBatch = entries;
        SelectedCatalogEntryCount = entries.Count;
    }

    /// <summary>Sequential, not parallel — a burst of concurrent downloads against the same catalog source (and Library import, which isn't designed for concurrent writers) is more likely to trip a rate limit or a file-collision than to meaningfully speed this up.</summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DownloadAndExtractSelectedAsync()
    {
        if (_selectedCatalogEntriesForBatch.Count == 0)
        {
            return;
        }

        var entries = _selectedCatalogEntriesForBatch;
        var successCount = 0;
        var failures = new List<string>();

        for (var i = 0; i < entries.Count; i++)
        {
            CatalogStatusMessage = $"Downloading {i + 1} of {entries.Count} — '{entries[i].Name}'…";
            var (success, message) = await DownloadAndExtractEntryAsync(entries[i]);
            if (success)
            {
                successCount++;
            }
            else
            {
                failures.Add(message);
            }
        }

        CatalogStatusMessage = failures.Count == 0
            ? $"Downloaded and imported {successCount} mod(s)."
            : $"Downloaded and imported {successCount} of {entries.Count} — {failures.Count} failed: {string.Join("; ", failures)}";
        ApplyCatalogFilters();
    }

    /// <summary>Opens the given row's readme, or the selected row's — same reason as DownloadAndExtractAsync.</summary>
    [RelayCommand]
    private void OpenCatalogReadme(CatalogEntryViewModel? entry)
    {
        if ((entry ?? SelectedCatalogEntry)?.Entry.ReadmeUrl is not { } url)
        {
            return;
        }

        UrlOpener.TryOpen(url);
    }
}
