using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
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
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Two independent sections (IMM Database, Nexus Mods) in one VM, matching the real app's two
/// Downloads sub-tabs — kept as one class rather than two, the same single-VM-per-page shape
/// LibraryViewModel already established for this app, since neither section is complex enough on
/// its own to justify its own page/VM.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly IDaedalusCatalogClient _daedalusClient;
    private readonly IJimk72CatalogClient _jimk72Client;
    private readonly IGitHubRepoDateClient _gitHubRepoDateClient;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IUe4ssModRepository _ue4ssModRepository;
    private readonly IUe4ssModMetaStore _ue4ssModMetaStore;
    private readonly ISettingsService _settingsService;
    private readonly PerformanceTracker _performanceTracker;
    private readonly INexusApiClient _nexusApiClient;
    private readonly ICredentialStore _credentialStore;
    private readonly IPendingDownloadStore _pendingDownloadStore;
    private readonly HttpClient _downloadHttpClient;
    private readonly IActivityLog _activityLog;
    private readonly IActiveDownloadsTracker _activeDownloadsTracker;
    private readonly string _pendingDownloadsDirectory;
    private readonly IPrebuiltPakImporter _prebuiltPakImporter;
    private readonly string _dataFolder;
    private readonly DebounceTimer _searchDebounceTimer;

    /// <summary>Null in tests and headless use — then a missing requirement is only reported, never offered for download.</summary>
    private readonly IDialogService? _dialogService;

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

    [ObservableProperty]
    private CatalogEntryViewModel? _selectedCatalogEntry;

    [ObservableProperty]
    private bool _isLoadingCatalog;

    [ObservableProperty]
    private string? _catalogStatusMessage;

    [ObservableProperty]
    private int _catalogShownCount;

    /// <summary>DataGrid.SelectedItems isn't a bindable DependencyProperty — set from LibraryView's own code-behind SelectionChanged handler (the IMM Database tab now lives there), same limitation/workaround the EXMOD editor's mass-edit selection already uses.</summary>
    private IReadOnlyList<CatalogEntryViewModel> _selectedCatalogEntriesForBatch = [];

    [ObservableProperty]
    private int _selectedCatalogEntryCount;

    /// <summary>Drives the batch-download button's visibility — a plain int bound through NullToVisibilityConverter would always read "has a value" (an int is never null), the same bug class already caught once this session, so this is a real bool instead.</summary>
    public bool HasCatalogSelectionForBatch => SelectedCatalogEntryCount >= 2;

    partial void OnSelectedCatalogEntryCountChanged(int value) => OnPropertyChanged(nameof(HasCatalogSelectionForBatch));

    [ObservableProperty]
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

    // --- Pending Downloads tab ---
    public ObservableCollection<PendingDownloadItemViewModel> PendingDownloads { get; } = [];

    [ObservableProperty]
    private string? _pendingDownloadStatusMessage;

    public DownloadsViewModel(
        IDaedalusCatalogClient daedalusClient,
        IJimk72CatalogClient jimk72Client,
        IGitHubRepoDateClient gitHubRepoDateClient,
        ILibraryRepository libraryRepository,
        IUe4ssModRepository ue4ssModRepository,
        IUe4ssModMetaStore ue4ssModMetaStore,
        ISettingsService settingsService,
        INexusApiClient nexusApiClient,
        ICredentialStore credentialStore,
        IPendingDownloadStore pendingDownloadStore,
        HttpClient downloadHttpClient,
        PerformanceTracker performanceTracker,
        IActivityLog activityLog,
        IActiveDownloadsTracker activeDownloadsTracker,
        string pendingDownloadsDirectory, IPrebuiltPakImporter prebuiltPakImporter, string dataFolder,
        ICatalogSnapshotStore? catalogSnapshotStore = null,
        IDialogService? dialogService = null)
    {
        _catalogSnapshotStore = catalogSnapshotStore;
        _dialogService = dialogService;
        _prebuiltPakImporter = prebuiltPakImporter;
        _dataFolder = dataFolder;
        _daedalusClient = daedalusClient;
        _jimk72Client = jimk72Client;
        _gitHubRepoDateClient = gitHubRepoDateClient;
        _libraryRepository = libraryRepository;
        _ue4ssModRepository = ue4ssModRepository;
        _ue4ssModMetaStore = ue4ssModMetaStore;
        _settingsService = settingsService;
        _performanceTracker = performanceTracker;
        _nexusApiClient = nexusApiClient;
        _credentialStore = credentialStore;
        _pendingDownloadStore = pendingDownloadStore;
        _downloadHttpClient = downloadHttpClient;
        _activityLog = activityLog;
        _activeDownloadsTracker = activeDownloadsTracker;
        _pendingDownloadsDirectory = pendingDownloadsDirectory;

        _showAuthorColumn = settingsService.Current.CatalogShowAuthorColumn;
        _showVersionColumn = settingsService.Current.CatalogShowVersionColumn;
        _showInstalledVersionColumn = settingsService.Current.CatalogShowInstalledVersionColumn;
        _showCompatibilityColumn = settingsService.Current.CatalogShowCompatibilityColumn;
        _showCategoryColumn = settingsService.Current.CatalogShowCategoryColumn;
        _showStatusColumn = settingsService.Current.CatalogShowStatusColumn;
        _showLastUpdatedColumn = settingsService.Current.CatalogShowLastUpdatedColumn;

        _searchDebounceTimer = new DebounceTimer(TimeSpan.FromMilliseconds(250), ApplyCatalogFilters);

        ReloadPendingDownloads();

        // Constructors can't be async; this is the same "fire it and let the method itself
        // handle every failure" shape a WPF event handler already has to use for async work —
        // RefreshCatalogAsync has its own top-level try/catch, so nothing here can produce an
        // unobserved exception.
        _ = LoadCatalogAsync();

        // This ViewModel Sends LibraryChangedMessage itself (after a download/extract, an
        // Activate, or an import) but, until now, never Registered to receive it — so the IMM
        // Database's Downloaded/Outdated/installed-version status went stale after an import or
        // delete that happened elsewhere (Library, or Merge & Install's own Install) until an
        // unrelated filter change happened to recompute it. Also refreshes every Pending Downloads
        // row's own computed Install/Reinstall/deleted state, for the same reason — a delete in
        // Library doesn't touch this VM's own PendingDownloads collection at all, so nothing else
        // would notice.
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) =>
        {
            var self = (DownloadsViewModel)recipient;
            self.ApplyCatalogFilters();
            foreach (var item in self.PendingDownloads)
            {
                item.Refresh();
            }
        });
    }

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
            await LoadCatalogAsync();
        }

        return _allCatalogEntries;
    }

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

    // Downloaded/Outdated status is cross-referenced against the Library by (Name, Author),
    // there being no other shared key — LibraryEntry doesn't record which catalog entry (if any)
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

        // Every row above is a brand-new CatalogEntryViewModel, so without this the DataGrid's
        // two-way SelectedItem binding just drops back to null on every filter/search change (or
        // after Download & extract re-applies filters) even when the same mod still matches —
        // the detail pane and its buttons would disappear for no reason the user did anything
        // wrong.
        SelectedCatalogEntry = previouslySelectedId is null
            ? null
            : CatalogEntries.FirstOrDefault(row => row.Entry.Id == previouslySelectedId);
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DownloadAndExtractAsync()
    {
        if (SelectedCatalogEntry is null)
        {
            return;
        }

        var (success, message) = await DownloadAndExtractEntryAsync(SelectedCatalogEntry);
        CatalogStatusMessage = message;
        if (success)
        {
            ApplyCatalogFilters();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
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
        var downloadUrl = catalogEntry.ExmodzUrl ?? catalogEntry.PakUrl;
        if (downloadUrl is null)
        {
            return (false, $"'{catalogEntry.Name}' has no downloadable file listed.");
        }

        // A real catalog entry can point at a github.com "view this file" page instead of its raw
        // content (a common submission mistake — see GitHubBlobUrl's own doc comment) — downloading
        // that HTML page and treating it as an archive fails opaquely, so this is normalized before
        // ever reaching the actual GetByteArrayAsync call below.
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
        var partPath = $"{localPath}.{Guid.NewGuid():N}.part";

        try
        {
            // Streamed to disk, same as the Nexus path (an audit finding): GetByteArrayAsync buffered
            // the whole pak in memory AND put the entire transfer under HttpClient's 100 s timeout,
            // so a large catalog pak on a slow connection failed every time.
            using (var response = await HttpFileDownloader.GetWithStreamingResponseAsync(_downloadHttpClient, downloadUrl))
            {
                response.EnsureSuccessStatusCode();
                await using var fileStream = File.Create(partPath);
                await response.Content.CopyToAsync(fileStream);
            }

            File.Move(partPath, localPath, overwrite: true);

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
            });
            ReloadPendingDownloads();

            var justDownloaded = PendingDownloads.FirstOrDefault(p => p.ModId == 0 && p.FileId == fileId);
            if (justDownloaded is null)
            {
                return (false, $"'{catalogEntry.Name}' downloaded but couldn't be recorded.");
            }

            await ActivatePendingDownloadAsync(justDownloaded);
            if (!justDownloaded.IsInstalled)
            {
                return (false, PendingDownloadStatusMessage ?? $"'{catalogEntry.Name}' downloaded but didn't import — see the Downloads page.");
            }

            _activityLog.Log($"Downloaded and imported '{catalogEntry.Name}' from the catalog.", ActivityEntryKind.Success);
            return (true, $"Downloaded and imported '{catalogEntry.Name}'.");
        }
        catch (Exception ex)
        {
            return (false, $"'{catalogEntry.Name}' failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
            catch (Exception)
            {
                // Best-effort temp cleanup — a leftover file in %TEMP% isn't worth surfacing.
            }
        }
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
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
    }

    [RelayCommand]
    private void OpenCatalogReadme()
    {
        if (SelectedCatalogEntry?.Entry.ReadmeUrl is not { } url)
        {
            return;
        }

        UrlOpener.TryOpen(url);
    }

    /// <summary>
    /// Resolves a bare Nexus mod ID to a downloadable file, then runs it through
    /// FetchAndDownloadAsync — the same two-step DownloadModAsync uses. Shared here so
    /// NexusCatalogViewModel's own per-card Download button (its only real caller) doesn't carry
    /// its own copy of this logic. A mod with exactly one file downloads it directly, same as
    /// before; a mod with more than one (different versions, optional add-ons, or FOMOD-style
    /// variant packs) shows a real picker instead of silently grabbing whatever Nexus marks
    /// primary — that was silently discarding every other file a mod offered.
    /// </summary>
    public async Task ResolvePrimaryFileAndFetchAsync(int nexusModId, string? displayName = null)
    {
        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            PendingDownloadStatusMessage = "Sign in with your Nexus API key in Settings first.";
            return;
        }

        PendingDownloadStatusMessage = $"Finding mod #{nexusModId}'s file(s)…";
        try
        {
            var files = await _nexusApiClient.GetModFilesAsync(apiKey, "icarus", nexusModId);
            if (files.Count == 0)
            {
                PendingDownloadStatusMessage = $"Mod #{nexusModId} has no downloadable files listed.";
                return;
            }

            NexusModFile file;
            if (files.Count == 1)
            {
                file = files[0];
            }
            else
            {
                var dialog = new PickNexusFileDialog(displayName ?? $"Mod #{nexusModId}", files) { Owner = Application.Current.MainWindow };
                if (dialog.ShowDialog() != true || dialog.SelectedFile is not { } chosen)
                {
                    PendingDownloadStatusMessage = "Download cancelled.";
                    return;
                }

                file = chosen;
            }

            await FetchAndDownloadAsync($"nxm://icarus/mods/{nexusModId}/files/{file.FileId}", displayName);
        }
        catch (NexusRateLimitedException ex)
        {
            PendingDownloadStatusMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            // GetModFilesAsync's own rejection — an actually invalid/expired stored key (401/403
            // on a plain file listing, not a premium-tier restriction: listing works the same for
            // every account tier). The premium-specific rejection this used to (wrongly) describe
            // instead happens one step later, inside FetchAndDownloadAsync's own GetDownloadLinksAsync
            // call — handled there now, with its own accurate, actionable message.
            PendingDownloadStatusMessage = $"{ex.Message} Sign in again with a valid key in Settings.";
        }
        catch (Exception ex)
        {
            PendingDownloadStatusMessage = $"Couldn't find that mod's file: {ex.Message}";
        }
    }

    /// <summary>
    /// The real download pipeline — parse the nxm:// link, resolve it to a real CDN URL via
    /// Nexus's own API, download the bytes, record it in Pending Downloads, then immediately
    /// activate it (the same classify-and-import logic ActivatePendingDownloadAsync already has,
    /// which already tells an EXMOD mod from a prebuilt pak from a UE4SS mod). A successful
    /// download just becomes a normal Library (or UE4SS) entry with no separate click needed —
    /// per direct feedback, a downloaded mod should "blend in" automatically. displayName (when
    /// the caller already knows it) drives the Library page's in-progress stub; falls back to
    /// "mod #N" when it isn't known yet, which has no name to show until Nexus's own headers
    /// reveal the real file name partway through. Public (not private) since this is the landing
    /// point for a real nxm:// link handed off from the OS protocol handler (App.xaml.cs's
    /// HandleNxmUrl) — its only real caller.
    /// </summary>
    public async Task FetchAndDownloadAsync(string nxmUrlText, string? displayName = null)
    {
        NxmUrl nxmUrl;
        try
        {
            nxmUrl = NxmUrl.Parse(nxmUrlText);
        }
        catch (FormatException ex)
        {
            PendingDownloadStatusMessage = $"That doesn't look like a Nexus mod page URL or an nxm:// link: {ex.Message}";
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            PendingDownloadStatusMessage = "Sign in with your Nexus API key in Settings first.";
            return;
        }

        var trackerKey = $"{nxmUrl.ModId}:{nxmUrl.FileId}";
        var trackerDisplayName = displayName ?? $"Mod #{nxmUrl.ModId}";

        // A second handoff for a file already in flight (a double-click on Nexus's download button
        // is common) used to download it twice and orphan the first copy (an audit finding).
        if (_activeDownloadsTracker.Current.Any(d => d.Key == trackerKey))
        {
            PendingDownloadStatusMessage = $"'{trackerDisplayName}' is already downloading.";
            return;
        }

        _activeDownloadsTracker.Start(trackerKey, trackerDisplayName);
        try
        {
            var links = await _nexusApiClient.GetDownloadLinksAsync(
                apiKey, nxmUrl.GameDomain, nxmUrl.ModId, nxmUrl.FileId, nxmUrl.Key, nxmUrl.Expires);
            var link = links.FirstOrDefault();
            if (link is null)
            {
                PendingDownloadStatusMessage = "Nexus didn't return a download link for this file.";
                return;
            }

            using var response = await HttpFileDownloader.GetWithStreamingResponseAsync(_downloadHttpClient, link.Uri);
            response.EnsureSuccessStatusCode();

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? Path.GetFileName(new Uri(link.Uri).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"nexus_{nxmUrl.ModId}_{nxmUrl.FileId}.zip";
            }

            // fileName came straight from the CDN response's own Content-Disposition header (or,
            // failing that, the download URL's own path) — neither is trustworthy: a rooted or
            // "../"-laden value would otherwise let Path.Combine below write anywhere the app
            // process can write.
            // Nexus's "<name> <modId> <fileId> <timestamp> <hash>" tail dropped from the saved name
            // (a real complaint: it rode into staged mod names). The (ModId, FileId) pair below is
            // what keeps same-named files apart, not the hash.
            fileName = DownloadFileNameSanitizer.Sanitize(
                NexusDownloadFileName.StripNexusSuffix(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName));

            Directory.CreateDirectory(_pendingDownloadsDirectory);
            // See DownloadFileNameSanitizer.ResolveUniqueFileName's own doc comment: disambiguates
            // against a DIFFERENT (ModId, FileId) pair's download that happens to share this exact
            // file name, without disturbing a legitimate re-download of this same pair.
            fileName = DownloadFileNameSanitizer.ResolveUniqueFileName(
                _pendingDownloadsDirectory, fileName, nxmUrl.ModId, nxmUrl.FileId,
                path => _pendingDownloadStore.Entries.Any(e => e.ModId == nxmUrl.ModId && e.FileId == nxmUrl.FileId && e.LocalFilePath == path));
            var localPath = Path.Combine(_pendingDownloadsDirectory, fileName);

            // Streamed to a .part file and renamed into place on completion (an audit finding):
            // writing straight to the final path truncated an already-complete earlier download of
            // the same file the moment a re-download started, so a mid-stream failure left a
            // truncated file that the Pending Downloads row still presented as good.
            var partPath = $"{localPath}.{Guid.NewGuid():N}.part";
            try
            {
                await using (var fileStream = File.Create(partPath))
                {
                    await response.Content.CopyToAsync(fileStream);
                }

                File.Move(partPath, localPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }

            _pendingDownloadStore.Add(new PendingDownloadEntry
            {
                ModId = nxmUrl.ModId,
                FileId = nxmUrl.FileId,
                FileName = fileName,
                LocalFilePath = localPath,
                DownloadedAtUtc = DateTimeOffset.UtcNow,
            });
            ReloadPendingDownloads();
            PendingDownloadStatusMessage = $"Downloaded '{fileName}' — importing…";

            var justDownloaded = PendingDownloads.FirstOrDefault(p => p.ModId == nxmUrl.ModId && p.FileId == nxmUrl.FileId);
            if (justDownloaded is not null)
            {
                await ActivatePendingDownloadAsync(justDownloaded);
            }
        }
        catch (InvalidOperationException ex)
        {
            // Nexus's own real rejection for exactly this case: a non-premium account's API key
            // alone isn't accepted for a direct download-link request (GetDownloadLinksAsync
            // above) — the account needs a real, temporary key+expires pair, which only comes from
            // clicking Nexus's own "Mod Manager Download" button on the actual website. Caught
            // specifically, ahead of the generic catch below, and opens the real mod page directly
            // rather than just naming it, so there's one less manual step for a confused non-
            // premium user. Found live: this exact case used to fall through to the generic
            // "Download failed: ..." message below with no actionable next step at all, since
            // ResolvePrimaryFileAndFetchAsync's own separate (more helpful) catch for this same
            // exception type was unreachable dead code — this method's own broad catch already
            // swallowed it first, every time, before it could ever propagate that far.
            UrlOpener.TryOpen(NexusModWebUrl.For(nxmUrl.ModId));
            PendingDownloadStatusMessage =
                $"{ex.Message} Opened the mod's Nexus page — if your account isn't Premium, click its "
                + "\"Mod Manager Download\" button there instead (register IcarusStarlink as your nxm:// "
                + "handler in Settings first, if you haven't already).";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a stale/expired nxm key, a network hiccup, or
            // a disk-full save all show a status message rather than crashing the app.
            PendingDownloadStatusMessage = $"Download failed: {ex.Message}";
        }
        finally
        {
            _activeDownloadsTracker.Finish(trackerKey);
        }
    }

    /// <summary>
    /// MO2-style: never removes the entry from Pending Downloads (only Discard does that) — a
    /// second click on an already-installed entry is a Reinstall, which first deletes whatever
    /// its last activation produced (mirroring the "delete existing Library entry by folder name
    /// before re-importing" pattern MergeInstallViewModel's own InstallAsync already established,
    /// so re-Activating doesn't leave a "_2"-suffixed duplicate next to the one it's replacing).
    /// Deleting happens before the new import is attempted, same as that existing pattern — if the
    /// new import then fails, the entry is left correctly showing "deleted from Library" rather
    /// than silently keeping a stale, no-longer-accurate "installed" state.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ActivatePendingDownloadAsync(PendingDownloadItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        string? tempExtractDirectory = null;
        try
        {
            // The UE4SS folder names this download owned before this Reinstall — they may still be
            // INSTALLED in the game's Mods folder (an audit of a real report: the first activation
            // had been applied into the game, so re-staging saw the name "in use" and landed as a
            // "_2" copy, doubling the mod in the UE4SS tab). Re-staging under the same name is the
            // whole point of a reinstall; the UE4SS tab's Apply then updates the game copy.
            var ownUe4ssNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (item.IsInstalled)
            {
                foreach (var activated in item.ActivatedItems)
                {
                    if (activated.Kind == PendingDownloadActivationKind.Library)
                    {
                        _libraryRepository.Delete(activated.FolderName);
                    }
                    else if (activated.Kind == PendingDownloadActivationKind.Ue4ssMod)
                    {
                        ownUe4ssNames.Add(activated.FolderName);
                        _ue4ssModRepository.DeleteAttachedPaks(activated.FolderName);
                        _ue4ssModRepository.Delete(activated.FolderName);
                        // Without this, a Reinstall's own Nexus-link sidecar (Ue4ss_Meta/{folder}.json)
                        // is orphaned on disk — never cleaned up, and a stale link if a different,
                        // unrelated mod later happens to land under the same folder name.
                        _ue4ssModMetaStore.Delete(activated.FolderName);
                    }
                }
            }
            else
            {
                // This exact PendingDownloadEntry has never been activated before through THIS row
                // (a first-time download of a new file for this Nexus mod — e.g. clicking "Update"
                // on a mod that was originally installed some other way: a manual EXMOD/pak import,
                // a migration from classic IMM, or a download from before Pending Downloads existed
                // at all) — but a Library or UE4SS entry already linked to this same Nexus mod ID can
                // still exist on disk. Without deleting it first, the classify-and-import below just
                // adds a second, "_2"-suffixed copy alongside the original, which is left completely
                // untouched — from the user's own perspective, clicking "Update" on their existing
                // mod silently did nothing to it at all (a real bug found live: "I download the
                // update but it's not updating the mod I clicked update on").
                // EVERY entry linked to this same source identity (Nexus mod ID, or catalog entry
                // ID for an IMM Database download), not just the first.
                var existingLibraryEntries = item.IsCatalog
                    ? _libraryRepository.GetAll().Where(e => string.Equals(e.CatalogEntryId, item.CatalogEntryId, StringComparison.OrdinalIgnoreCase)).ToList()
                    : _libraryRepository.GetAll().Where(e => e.NexusModId == item.ModId).ToList();
                foreach (var existingLibraryEntry in existingLibraryEntries)
                {
                    _libraryRepository.Delete(existingLibraryEntry.FolderName);
                }

                if (!item.IsCatalog)
                {
                    foreach (var existingUe4ssFolderName in _ue4ssModRepository.GetAll()
                        .Where(folderName => _ue4ssModMetaStore.Load(folderName).NexusModId == item.ModId).ToList())
                    {
                        ownUe4ssNames.Add(existingUe4ssFolderName);
                        _ue4ssModRepository.DeleteAttachedPaks(existingUe4ssFolderName);
                        _ue4ssModRepository.Delete(existingUe4ssFolderName);
                        _ue4ssModMetaStore.Delete(existingUe4ssFolderName);
                    }
                }
            }

            var source = item.IsCatalog ? PendingDownloadEntry.DatabaseSource : PendingDownloadEntry.NexusSource;
            int? nexusModId = item.IsCatalog ? null : item.ModId;

            string statusMessage;
            IReadOnlyList<PendingDownloadActivatedItem> activatedItems;
            IReadOnlyList<string> requirements = [];

            // Resolved at use (an audit finding): the stored path is absolute, so moving the app
            // folder — the "unzip anywhere" portable install — broke every pending entry; the file
            // name is always under this app's own Pending_Downloads folder.
            var localFilePath = ResolveLocalFilePath(item);
            if (string.Equals(Path.GetExtension(localFilePath), ".pak", StringComparison.OrdinalIgnoreCase))
            {
                var entry = await _prebuiltPakImporter.ImportAsync(
                    localFilePath, _dataFolder, _settingsService.Current.UnrealPakExePath, source: source, nexusModId: nexusModId,
                    catalogEntryId: item.CatalogEntryId, name: item.IsCatalog ? item.DisplayName : null);
                if (nexusModId is { } && (entry.IsOpaquePak || entry.ConvertedFromPrebuiltPak))
                {
                    // A converted entry's own Name/Author are still just placeholders (the pak's
                    // filename, "Unknown") — ConvertedFromPrebuiltPak is what lets
                    // FolderLibraryRepository.SetNexusMetadata's cached-entry update (and ToEntry's
                    // own NexusName/NexusAuthor fallback, on the next rescan) actually apply to it,
                    // unlike an ordinary EXMOD import.
                    await RecordNexusMetadataAsync(entry.FolderName, item.ModId);
                }

                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
                statusMessage = $"Imported '{entry.Name}' into your Library.";
                activatedItems = [new PendingDownloadActivatedItem(entry.FolderName, PendingDownloadActivationKind.Library)];
                _activityLog.Log($"Activated pending download '{entry.Name}'.", ActivityEntryKind.Success);
            }
            else
            {
                // A real Nexus download's own extension can't be trusted (a mod author might
                // upload a zip, rar, or 7z, and it could contain an EXMOD-shaped mod, a prebuilt
                // .pak, or a UE4SS mod) — unlike every other import path in this app, there's no
                // dialog filter or button choice telling us which kind this is, so it has to be
                // extracted first and classified from its actual content.
                tempExtractDirectory = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Activate_{Guid.NewGuid():N}");
                AnyArchiveExtractor.ExtractToDirectory(localFilePath, tempExtractDirectory);
                (statusMessage, activatedItems, requirements) =
                    await ClassifyAndImportExtractedModAsync(tempExtractDirectory, item.FileName, source, nexusModId, item.CatalogEntryId, ownUe4ssNames);
            }

            _pendingDownloadStore.SetActivation(item.ModId, item.FileId, activatedItems, requirements);
            item.SetActivated(activatedItems, requirements);
            RefreshVisiblePendingDownloads();
            PendingDownloadStatusMessage = statusMessage;

            // A mod organizer resolves a dependency at download time, with a question — not a
            // warning buried in a status line after the fact.
            await OfferMissingRequirementsAsync(item);
        }
        catch (Exception ex)
        {
            // Same UI boundary as Library's own ImportFolder/ImportFile — a malformed archive or a
            // locked file shows a status message instead of crashing the app. Deliberately leaves
            // the entry in Pending Downloads (and the file on disk) on failure, so nothing is lost
            // and the user can retry or Discard explicitly.
            PendingDownloadStatusMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            if (tempExtractDirectory is not null)
            {
                try
                {
                    Directory.Delete(tempExtractDirectory, recursive: true);
                }
                catch (Exception)
                {
                    // Best-effort scratch cleanup — a locked file here (e.g. antivirus mid-scan)
                    // shouldn't turn a successful import into a reported failure.
                }
            }
        }
    }

    /// <summary>
    /// Figures out what an already-extracted download actually is and imports it the right way,
    /// via the shared ExtractedModClassifier (LibraryViewModel's own manual "Import archive…" uses
    /// the identical detection) — then layers this page's own Nexus-specific follow-up on top: an
    /// opaque pak or a UE4SS mod carries no embedded name/author of its own, so both get a
    /// best-effort real-name/version lookup from the Nexus mod ID this download already came with,
    /// and every kind fires the same reload/activity-log side effects a manual import doesn't need
    /// (Library's UE4SS tab has its own reload path, never wired to this pipeline before this).
    /// </summary>
    private async Task<(string StatusMessage, IReadOnlyList<PendingDownloadActivatedItem> Items, IReadOnlyList<string> Requirements)> ClassifyAndImportExtractedModAsync(
        string extractedDirectory, string originalFileName, string source, int? nexusModId, string? catalogEntryId, IReadOnlySet<string>? ownUe4ssNamesToReuse = null)
    {
        // Only meaningful if the game path is actually configured — with nothing to check against,
        // this degrades to the same "no collision avoidance" behavior importing already had. This
        // download's OWN previously-staged names are allowed back (a reinstall must land under the
        // same name even while the game folder still holds the earlier copy).
        var icarusContentPath = _settingsService.Current.IcarusContentPath;
        var installedUe4ssNames = !string.IsNullOrWhiteSpace(icarusContentPath)
            ? _ue4ssModRepository.ListInstalledInGame(Ue4ssGamePaths.ResolveModsFolder(icarusContentPath))
                .Where(name => ownUe4ssNamesToReuse is null || !ownUe4ssNamesToReuse.Contains(name))
                .ToList()
            : null;

        var classified = await ExtractedModClassifier.ClassifyAndImport(
            extractedDirectory, originalFileName, _libraryRepository, _ue4ssModRepository,
            _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, source: source, nexusModId: nexusModId,
            ue4ssNamesAlreadyInstalledInGame: installedUe4ssNames, catalogEntryId: catalogEntryId);

        var landed = new List<string>();
        foreach (var produced in classified.Items)
        {
            if (produced.Kind == PendingDownloadActivationKind.Library)
            {
                // For EVERY Library kind, not just an opaque/converted pak (the original gating — a real
                // bug found live): an ordinary EXMODZ keeps its author-declared Name/Author (the
                // repository guards those), but still needs the Nexus VERSION recorded, or the update
                // check compares the author's own "1.0" against Nexus's "1" and shows "Update
                // available" forever right after activating. See UpdateVersionComparer.
                if (nexusModId is { } libraryModId)
                {
                    await RecordNexusMetadataAsync(produced.FolderName, libraryModId);
                }

                var entryName = _libraryRepository.GetAll().FirstOrDefault(e => e.FolderName == produced.FolderName)?.Name ?? produced.FolderName;
                landed.Add($"'{entryName}' (Library)");
                _activityLog.Log($"Activated pending download '{entryName}'.", ActivityEntryKind.Success);
            }
            else
            {
                if (nexusModId is { } ue4ssModId)
                {
                    await EnrichUe4ssModFromNexusAsync(produced.FolderName, ue4ssModId);
                }

                landed.Add($"'{produced.FolderName}' (UE4SS mod — enable it on the UE4SS tab, then Apply)");
                _activityLog.Log($"Activated pending download '{produced.FolderName}' as a UE4SS mod.", ActivityEntryKind.Success);
            }
        }

        var statusMessage = landed.Count == 1
            ? $"Activated {landed[0]}."
            : $"Activated {landed.Count} parts: {string.Join(", ", landed)}.";
        statusMessage += DescribeRequirements(classified.Requirements);
        foreach (var warning in classified.Warnings)
        {
            _activityLog.Log($"{originalFileName}: {warning}", ActivityEntryKind.Warning);
        }

        if (classified.Warnings.Count > 0)
        {
            statusMessage += $" Warning: {classified.Warnings[0]}" + (classified.Warnings.Count > 1 ? $" (+{classified.Warnings.Count - 1} more in the Activity panel)" : "");
        }

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        return (statusMessage, classified.Items, classified.Requirements);
    }

    /// <summary>
    /// " Needs: ArcFission Library 1.2.0 or newer — not installed (search Nexus for it); UE4SS — loader."
    /// A best-effort match of each README requirement against what's staged/installed (see
    /// ModReadmeRequirements) — the user's actual complaint was not being told which OTHER
    /// download a mod depended on. Logged as a warning too, so it survives leaving the page.
    /// </summary>
    private string DescribeRequirements(IReadOnlyList<string> requirements)
    {
        if (requirements.Count == 0)
        {
            return "";
        }

        var installedNames = _ue4ssModRepository.GetAll().Concat(_libraryRepository.GetAll().Select(e => e.Name)).ToList();
        var parts = new List<string>();
        var missing = new List<string>();
        foreach (var requirement in requirements)
        {
            if (ModReadmeRequirements.IsLoaderRequirement(requirement))
            {
                parts.Add($"{requirement} (the UE4SS loader — Settings → UE4SS)");
            }
            else if (ModReadmeRequirements.IsSatisfiedBy(requirement, installedNames))
            {
                parts.Add($"{requirement} ✓");
            }
            else
            {
                parts.Add($"{requirement} — NOT installed");
                missing.Add(ModReadmeRequirements.ExtractName(requirement));
            }
        }

        if (missing.Count > 0)
        {
            _activityLog.Log(
                $"This mod needs other downloads you don't have yet: {string.Join(", ", missing)} — find them on the Nexus tab.",
                ActivityEntryKind.Warning);
        }

        return $" Needs: {string.Join("; ", parts)}.";
    }

    /// <summary>
    /// Looks the mod up by the ID the nxm:// download already told us and records what Nexus knows
    /// (name/author/description/version) in the entry's sidecar. For an opaque .pak (no embedded
    /// metadata of its own) or a converted one, that becomes the displayed name/author; for an
    /// ordinary EXMODZ only the version is surfaced (as LibraryEntry.NexusVersion, the like-for-like
    /// side of update-checking) — FolderLibraryRepository.SetNexusMetadata never overwrites a real
    /// author's own declared name. Best-effort only: no signed-in key, a rejected key, or a network
    /// hiccup all fall through silently, since a missing enrichment shouldn't turn an otherwise-
    /// successful import into a reported failure — the entry just keeps showing "Unknown" the way
    /// it always has.
    /// </summary>
    private async Task RecordNexusMetadataAsync(string folderName, int modId)
    {
        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            return;
        }

        try
        {
            var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", modId);
            if (info is not null)
            {
                _libraryRepository.SetNexusMetadata(folderName, info.Name, info.Author, info.Summary, info.Version);
            }
        }
        catch (Exception)
        {
            // Best-effort — see the doc comment above.
        }
    }

    /// <summary>
    /// Same reasoning as RecordNexusMetadataAsync, but for a UE4SS mod: a UE4SS mod carries no
    /// metadata file at all (not even a name/author), so its Nexus mod ID and version are recorded
    /// in the separate Ue4ss_Meta sidecar store instead of LibraryMeta — that's what lets Library's
    /// UE4SS tab show a "Nexus" badge and check for updates.
    ///
    /// The ID itself is saved unconditionally, BEFORE the enrichment call — matching the opaque-pak
    /// path's own ImportPak(..., nexusModId: modId), which records the ID at import time regardless
    /// of what SetNexusMetadata's own best-effort lookup does. Only the version lookup is
    /// best-effort/silently-swallowed: a missing key or a rejected/failed API call still leaves the
    /// mod correctly linked (HasNexusLink true, eligible for CheckForUpdatesAsync), just without a
    /// known starting version yet — versus the original shape, where any enrichment failure meant
    /// the ID was never saved at all, permanently excluding the mod from update-checking with no way
    /// to relink it (Library's UE4SS tab has no manual "Link to Nexus…" action, unlike its Mods tab).
    ///
    /// Load-modify-Save, not a fresh `new Ue4ssModMeta { ... }` — folderName is freshly staged by
    /// this same activation almost always, but a disambiguated/reused folder name could already
    /// carry a user-declared MinUe4ssVersion (see its own doc comment) from a previous mod that once
    /// lived under this same name; a blind replace would silently wipe that out.
    /// </summary>
    private async Task EnrichUe4ssModFromNexusAsync(string folderName, int modId)
    {
        var meta = _ue4ssModMetaStore.Load(folderName);
        meta.NexusModId = modId;
        _ue4ssModMetaStore.Save(folderName, meta);

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            return;
        }

        try
        {
            var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", modId);
            if (info is not null)
            {
                meta.NexusVersion = info.Version;
                meta.NexusName = info.Name;
                _ue4ssModMetaStore.Save(folderName, meta);
            }
        }
        catch (Exception)
        {
            // Best-effort — see the doc comment above. The ID link above already succeeded regardless.
        }
    }

    /// <summary>
    /// The follow-through on a README requirement the activation flagged as NOT installed: jump to
    /// the Nexus page searching for it by name. The first missing one is searched; the rest are
    /// named in the status line so the user knows there are more.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task GetRequirementAsync(PendingDownloadItemViewModel? item)
    {
        if (item is null || item.MissingRequirementNames.Count == 0)
        {
            return;
        }

        if (!await OfferMissingRequirementsAsync(item))
        {
            // No dialog service, or nothing could be resolved: fall back to a Nexus search by name.
            var missing = item.MissingRequirementNames;
            WeakReferenceMessenger.Default.Send(new SearchNexusMessage(missing[0]));
            PendingDownloadStatusMessage = missing.Count == 1
                ? $"Searching Nexus for '{missing[0]}'."
                : $"Searching Nexus for '{missing[0]}' — this download also needs: {string.Join(", ", missing.Skip(1))}.";
        }
    }

    /// <summary>
    /// For every README requirement nothing installed satisfies: look it up on Nexus by name and
    /// ask "'Wireless Power' needs ArcFission Library 1.2.0 or newer. Download 'ArcFission Library'
    /// now?" — Yes runs the normal download-and-activate pipeline for it. Returns true when at least
    /// one requirement was offered (downloaded or declined), false when nothing could be offered at
    /// all (no dialog service, no API key, or no Nexus match for any name) so the caller can fall
    /// back to a plain search. Never throws: a Nexus hiccup just means no offer for that name.
    /// </summary>
    private async Task<bool> OfferMissingRequirementsAsync(PendingDownloadItemViewModel item)
    {
        if (_dialogService is null)
        {
            return false;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            return false;
        }

        var offered = false;
        foreach (var requirementName in item.MissingRequirementNames.ToList())
        {
            NexusModInfo? match;
            try
            {
                var results = await _nexusApiClient.SearchModsAsync(apiKey, "icarus", requirementName);
                match = PickRequirementMatch(results, requirementName);
            }
            catch (Exception)
            {
                match = null;
            }

            if (match is null)
            {
                continue;
            }

            offered = true;
            var requirementLine = item.Requirements.FirstOrDefault(r => string.Equals(ModReadmeRequirements.ExtractName(r), requirementName, StringComparison.OrdinalIgnoreCase)) ?? requirementName;
            var download = _dialogService.Confirm(
                $"'{item.DisplayName}' needs {requirementLine}, which isn't installed.\n\nDownload '{match.Name}' (Nexus mod #{match.ModId}) now?",
                "Missing requirement", ThemedConfirmSeverity.Question);
            if (!download)
            {
                PendingDownloadStatusMessage = $"Skipped '{match.Name}' — '{item.DisplayName}' won't work until it's installed.";
                continue;
            }

            await ResolvePrimaryFileAndFetchAsync(match.ModId, match.Name);
        }

        return offered;
    }

    /// <summary>Exact name first, then a name that contains (or is contained by) the requirement — the first such hit in Nexus's own relevance order; null when nothing is close enough to offer without asking the user to look.</summary>
    internal static NexusModInfo? PickRequirementMatch(IReadOnlyList<NexusModInfo> results, string requirementName)
    {
        static string Norm(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        var wanted = Norm(requirementName);
        if (wanted.Length == 0)
        {
            return null;
        }

        return results.FirstOrDefault(r => Norm(r.Name) == wanted)
            ?? results.FirstOrDefault(r => Norm(r.Name).Contains(wanted, StringComparison.Ordinal) || wanted.Contains(Norm(r.Name), StringComparison.Ordinal));
    }

    private string ResolveLocalFilePath(PendingDownloadItemViewModel item) =>
        File.Exists(item.LocalFilePath) ? item.LocalFilePath : Path.Combine(_pendingDownloadsDirectory, item.FileName);

    [RelayCommand]
    private void DiscardPendingDownload(PendingDownloadItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var localFilePath = ResolveLocalFilePath(item);
            if (File.Exists(localFilePath))
            {
                File.Delete(localFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup — even if the file can't be deleted right now (locked by another
            // process), still drop it from the tracked list; a stray file at worst just sits there
            // unreferenced instead of blocking the user from moving on.
            PendingDownloadStatusMessage = $"Removed from the list, but couldn't delete the file: {ex.Message}";
            _pendingDownloadStore.Remove(item.ModId, item.FileId);
            ReloadPendingDownloads();
            return;
        }

        _pendingDownloadStore.Remove(item.ModId, item.FileId);
        ReloadPendingDownloads();
        PendingDownloadStatusMessage = $"Discarded '{item.FileName}'.";
    }

    private void ReloadPendingDownloads()
    {
        PendingDownloads.Clear();
        foreach (var entry in _pendingDownloadStore.Entries.OrderByDescending(e => e.DownloadedAtUtc))
        {
            PendingDownloads.Add(new PendingDownloadItemViewModel(entry, _libraryRepository, _ue4ssModRepository));
        }

        RefreshVisiblePendingDownloads();
    }

    /// <summary>
    /// What the "Downloads needing attention" list actually shows: by default only downloads that
    /// still need something (never activated, or a part since deleted) — a fully installed one sat
    /// in a list named "needing attention" forever (a real polish complaint). Show installed
    /// brings the history back.
    /// </summary>
    public ObservableCollection<PendingDownloadItemViewModel> VisiblePendingDownloads { get; } = [];

    [ObservableProperty]
    private bool _showInstalledDownloads;

    partial void OnShowInstalledDownloadsChanged(bool value) => RefreshVisiblePendingDownloads();

    public int InstalledDownloadCount => PendingDownloads.Count(p => p.IsInstalled);

    public void RefreshVisiblePendingDownloads()
    {
        VisiblePendingDownloads.Clear();
        foreach (var item in PendingDownloads)
        {
            item.Refresh();
            if (ShowInstalledDownloads || !item.IsInstalled)
            {
                VisiblePendingDownloads.Add(item);
            }
        }

        OnPropertyChanged(nameof(InstalledDownloadCount));
    }
}
