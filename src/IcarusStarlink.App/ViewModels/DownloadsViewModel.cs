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
        string pendingDownloadsDirectory, IPrebuiltPakImporter prebuiltPakImporter, string dataFolder)
    {
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
        _ = RefreshCatalogAsync();

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
            await RefreshCatalogAsync();
        }

        return _allCatalogEntries;
    }

    [RelayCommand]
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

            _allCatalogEntries = [.. daedalusTask.Result, .. jimk72Task.Result];
            // A fresh catalog snapshot invalidates every cached row VM below — bound to grow
            // unboundedly otherwise, since CatalogEntry has value equality and a genuinely changed
            // entry (a new version, say) would just add another cache key rather than replace one.
            _catalogRowCache.Clear();

            // Clearing/rebuilding these disrupts the ComboBoxes' own SelectedItem tracking (WPF
            // can reset a bound SelectedItem to null mid-rebuild) — capture the prior selection
            // and explicitly restore it (or fall back to "All...") afterward, rather than leaving
            // SelectedAuthor/SelectedCategory wherever the rebuild happened to drop them. Without
            // this, a null SelectedAuthor made ApplyCatalogFilters' `e.Author == SelectedAuthor`
            // match nothing at all — the very first live run of this page showed "0 shown / 624
            // in catalog" because of exactly this.
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
                _repoPushedDates = await _gitHubRepoDateClient.FetchPushedDatesAsync(repoKeys);
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

    [RelayCommand]
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

        var isExmodz = catalogEntry.ExmodzUrl is not null;
        var tempPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_{Guid.NewGuid():N}{(isExmodz ? ".EXMODZ" : ".pak")}");

        try
        {
            var bytes = await _downloadHttpClient.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(tempPath, bytes);

            // catalogEntryId is the Database counterpart of the Nexus pipeline's nexusModId — the
            // stable link that keeps the Downloaded badge and update-checking working after a
            // Rename, which name-matching alone can't survive.
            var imported = isExmodz
                ? _libraryRepository.Import(tempPath, source: "Database", catalogEntryId: catalogEntry.Id)
                : await _prebuiltPakImporter.ImportAsync(
                    tempPath, _dataFolder, _settingsService.Current.UnrealPakExePath,
                    source: "Database", catalogEntryId: catalogEntry.Id, name: catalogEntry.Name, author: catalogEntry.Author);
            _activityLog.Log($"Downloaded and imported '{imported.Name}' from the catalog.", ActivityEntryKind.Success);
            return (true, $"Downloaded and imported '{imported.Name}'.");
        }
        catch (Exception ex)
        {
            return (false, $"'{catalogEntry.Name}' failed: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
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
    [RelayCommand]
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
        _activeDownloadsTracker.Start(trackerKey, displayName ?? $"Mod #{nxmUrl.ModId}");
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

            using var response = await _downloadHttpClient.GetAsync(link.Uri);
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
            fileName = DownloadFileNameSanitizer.Sanitize(fileName);

            Directory.CreateDirectory(_pendingDownloadsDirectory);
            var localPath = Path.Combine(_pendingDownloadsDirectory, fileName);
            await using (var fileStream = File.Create(localPath))
            {
                await response.Content.CopyToAsync(fileStream);
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
    [RelayCommand]
    private async Task ActivatePendingDownloadAsync(PendingDownloadItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        string? tempExtractDirectory = null;
        try
        {
            if (item.IsInstalled && item.ActivatedFolderName is { } existingFolderName)
            {
                if (item.ActivatedKind == PendingDownloadActivationKind.Library)
                {
                    _libraryRepository.Delete(existingFolderName);
                }
                else if (item.ActivatedKind == PendingDownloadActivationKind.Ue4ssMod)
                {
                    _ue4ssModRepository.Delete(existingFolderName);
                    // Without this, a Reinstall's own Nexus-link sidecar (Ue4ss_Meta/{folder}.json)
                    // is orphaned on disk — never cleaned up, and a stale link if a different,
                    // unrelated mod later happens to land under the same folder name.
                    _ue4ssModMetaStore.Delete(existingFolderName);
                }
            }

            string statusMessage;
            string activatedFolderName;
            PendingDownloadActivationKind activatedKind;

            if (string.Equals(Path.GetExtension(item.LocalFilePath), ".pak", StringComparison.OrdinalIgnoreCase))
            {
                var entry = await _prebuiltPakImporter.ImportAsync(
                    item.LocalFilePath, _dataFolder, _settingsService.Current.UnrealPakExePath, source: "Nexus", nexusModId: item.ModId);
                if (entry.IsOpaquePak || entry.ConvertedFromPrebuiltPak)
                {
                    // A converted entry's own Name/Author are still just placeholders (the pak's
                    // filename, "Unknown") — ConvertedFromPrebuiltPak is what lets
                    // FolderLibraryRepository.SetNexusMetadata's cached-entry update (and ToEntry's
                    // own NexusName/NexusAuthor fallback, on the next rescan) actually apply to it,
                    // unlike an ordinary EXMOD import.
                    await EnrichOpaquePakFromNexusAsync(entry.FolderName, item.ModId);
                }

                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
                statusMessage = $"Imported '{entry.Name}' into your Library.";
                activatedFolderName = entry.FolderName;
                activatedKind = PendingDownloadActivationKind.Library;
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
                AnyArchiveExtractor.ExtractToDirectory(item.LocalFilePath, tempExtractDirectory);
                (statusMessage, activatedFolderName, activatedKind) =
                    await ClassifyAndImportExtractedModAsync(tempExtractDirectory, item.FileName, item.ModId);
            }

            _pendingDownloadStore.SetActivation(item.ModId, item.FileId, activatedFolderName, activatedKind);
            item.ActivatedFolderName = activatedFolderName;
            item.ActivatedKind = activatedKind;
            PendingDownloadStatusMessage = statusMessage;
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
    private async Task<(string StatusMessage, string FolderName, PendingDownloadActivationKind Kind)> ClassifyAndImportExtractedModAsync(
        string extractedDirectory, string originalFileName, int modId)
    {
        var (entryName, folderName, kind, _) = await ExtractedModClassifier.ClassifyAndImport(
            extractedDirectory, originalFileName, _libraryRepository, _ue4ssModRepository,
            _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, source: "Nexus", nexusModId: modId);

        string statusMessage;
        if (kind == PendingDownloadActivationKind.Library)
        {
            // Not just the classifier's own isOpaquePak — a successfully converted entry
            // (IsOpaquePak now false) still needs the same enrichment, its own Name/Author being
            // placeholders until this runs. Re-fetched rather than threaded through
            // ClassifyAndImport's own return tuple, which only reports IsOpaquePak (also used for
            // the unrelated package-icon glyph binding).
            var needsEnrichment = _libraryRepository.GetAll()
                .Any(e => e.FolderName == folderName && (e.IsOpaquePak || e.ConvertedFromPrebuiltPak));
            if (needsEnrichment)
            {
                await EnrichOpaquePakFromNexusAsync(folderName, modId);
            }

            statusMessage = $"Imported '{entryName}' into your Library.";
            _activityLog.Log($"Activated pending download '{entryName}'.", ActivityEntryKind.Success);
        }
        else
        {
            await EnrichUe4ssModFromNexusAsync(folderName, modId);
            statusMessage = $"Staged '{folderName}' as a UE4SS mod — enable it from Library's UE4SS tab, then click Apply.";
            _activityLog.Log($"Activated pending download '{folderName}' as a UE4SS mod.", ActivityEntryKind.Success);
        }

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        return (statusMessage, folderName, kind);
    }

    /// <summary>
    /// An opaque .pak carries no embedded name/author/description of its own — this looks the mod
    /// up by the ID the nxm:// download already told us, and records what Nexus knows instead.
    /// Best-effort only: no signed-in key, a rejected key, or a network hiccup all fall through
    /// silently, since a missing enrichment shouldn't turn an otherwise-successful import into a
    /// reported failure — the entry just keeps showing "Unknown" the way it always has.
    /// </summary>
    private async Task EnrichOpaquePakFromNexusAsync(string folderName, int modId)
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
    /// Same reasoning as EnrichOpaquePakFromNexusAsync, but for a UE4SS mod: a UE4SS mod carries no
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
                _ue4ssModMetaStore.Save(folderName, meta);
            }
        }
        catch (Exception)
        {
            // Best-effort — see the doc comment above. The ID link above already succeeded regardless.
        }
    }

    [RelayCommand]
    private void DiscardPendingDownload(PendingDownloadItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (File.Exists(item.LocalFilePath))
            {
                File.Delete(item.LocalFilePath);
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
    }
}
