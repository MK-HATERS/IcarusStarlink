using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Core.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Two independent sections (IMM Database, Nexus Mods) in one VM, matching the real app's two
/// Downloads sub-tabs — kept as one class rather than two, the same single-VM-per-page shape
/// LibraryViewModel already established for this app, since neither section is complex enough on
/// its own to justify its own page/VM.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private static readonly Regex NexusModUrlPattern = new(@"nexusmods\.com/icarus/mods/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IDaedalusCatalogClient _daedalusClient;
    private readonly IJimk72CatalogClient _jimk72Client;
    private readonly IGitHubRepoDateClient _gitHubRepoDateClient;
    private readonly ILibraryRepository _libraryRepository;
    private readonly INexusWatchlistStore _watchlistStore;
    private readonly ISettingsService _settingsService;
    private readonly HttpClient _downloadHttpClient;
    private readonly DispatcherTimer _searchDebounceTimer;

    private IReadOnlyList<CatalogEntry> _allCatalogEntries = [];

    // Keyed by (owner, repo), not by mod — see GitHubRepoDateClient's own doc comment for why
    // that's a deliberate repo-level, not per-file, granularity. Only ever replaced on a
    // *successful* RefreshCatalogAsync fetch (see there) so a transient GitHub failure on a later
    // refresh doesn't wipe out dates a previous successful refresh already resolved.
    private IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset> _repoPushedDates =
        new Dictionary<(string, string), DateTimeOffset>();

    public string Title => "Downloads";

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

    // --- Nexus Mods tab ---
    public ObservableCollection<NexusWatchlistItemViewModel> NexusEntries { get; } = [];

    [ObservableProperty]
    private string _nexusFilterText = "";

    [ObservableProperty]
    private string _newNexusUrl = "";

    [ObservableProperty]
    private NexusWatchlistItemViewModel? _selectedNexusEntry;

    [ObservableProperty]
    private string? _nexusStatusMessage;

    public DownloadsViewModel(
        IDaedalusCatalogClient daedalusClient,
        IJimk72CatalogClient jimk72Client,
        IGitHubRepoDateClient gitHubRepoDateClient,
        ILibraryRepository libraryRepository,
        INexusWatchlistStore watchlistStore,
        ISettingsService settingsService,
        HttpClient downloadHttpClient)
    {
        _daedalusClient = daedalusClient;
        _jimk72Client = jimk72Client;
        _gitHubRepoDateClient = gitHubRepoDateClient;
        _libraryRepository = libraryRepository;
        _watchlistStore = watchlistStore;
        _settingsService = settingsService;
        _downloadHttpClient = downloadHttpClient;

        _showAuthorColumn = settingsService.Current.CatalogShowAuthorColumn;
        _showVersionColumn = settingsService.Current.CatalogShowVersionColumn;
        _showInstalledVersionColumn = settingsService.Current.CatalogShowInstalledVersionColumn;
        _showCompatibilityColumn = settingsService.Current.CatalogShowCompatibilityColumn;
        _showCategoryColumn = settingsService.Current.CatalogShowCategoryColumn;
        _showStatusColumn = settingsService.Current.CatalogShowStatusColumn;
        _showLastUpdatedColumn = settingsService.Current.CatalogShowLastUpdatedColumn;

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyCatalogFilters();
        };

        ReloadNexusEntries();

        // Constructors can't be async; this is the same "fire it and let the method itself
        // handle every failure" shape a WPF event handler already has to use for async work —
        // RefreshCatalogAsync has its own top-level try/catch, so nothing here can produce an
        // unobserved exception.
        _ = RefreshCatalogAsync();
    }

    partial void OnCatalogSearchTextChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnSelectedAuthorChanged(string value) => ApplyCatalogFilters();
    partial void OnSelectedCategoryChanged(string value) => ApplyCatalogFilters();
    partial void OnShowUpdatesOnlyChanged(bool value) => ApplyCatalogFilters();
    partial void OnExtractedOnlyChanged(bool value) => ApplyCatalogFilters();
    partial void OnNotDownloadedOnlyChanged(bool value) => ApplyCatalogFilters();
    partial void OnGameWeekChanged(int value) => ApplyCatalogFilters();
    partial void OnHideOlderWeeksChanged(bool value) => ApplyCatalogFilters();

    partial void OnNexusFilterTextChanged(string value) => ApplyNexusFilter();

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

    [RelayCommand]
    private async Task RefreshCatalogAsync()
    {
        IsLoadingCatalog = true;
        CatalogStatusMessage = "Fetching catalog…";

        try
        {
            var daedalusTask = _daedalusClient.FetchAsync();
            var jimk72Task = _jimk72Client.FetchAsync();
            await Task.WhenAll(daedalusTask, jimk72Task);

            _allCatalogEntries = [.. daedalusTask.Result, .. jimk72Task.Result];

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

            CatalogStatusMessage = $"Loaded {_allCatalogEntries.Count} mods.";
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
    // a mod came from. Verified live during Phase 4 development: this can miss a real match, e.g.
    // Daedalus's own catalog lists "Aberiu's Supply Crates" but the actual downloaded EXMOD's own
    // internal name field is "Aberiu_Supply_Crates" (an underscore-joined FileName-style string,
    // not the curated display name) — a real mod stays correctly imported and fully usable, just
    // not visually flagged "Downloaded" back in this table. The same class of imperfect match
    // DaedalusCatalogClient already accepts for its own tags.json cross-reference; closing it
    // properly would mean Library recording real provenance (which catalog entry a download came
    // from) at import time, which is more surface area than a status badge currently justifies.
    private void ApplyCatalogFilters()
    {
        var previouslySelectedId = SelectedCatalogEntry?.Entry.Id;

        var libraryByKey = _libraryRepository.GetAll()
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

        var rows = query
            .Select(e => new CatalogEntryViewModel(
                e,
                libraryByKey.GetValueOrDefault(CatalogKey.Normalize(e.Name, e.Author)),
                GitHubRepoKey.Extract(e.PakUrl ?? e.ExmodzUrl) is { } repoKey ? _repoPushedDates.GetValueOrDefault(repoKey) : null))
            .Where(row => !ShowUpdatesOnly || row.IsOutdated)
            .Where(row => !ExtractedOnly || row.IsDownloaded)
            .Where(row => !NotDownloadedOnly || !row.IsDownloaded)
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CatalogEntries.Clear();
        foreach (var row in rows)
        {
            CatalogEntries.Add(row);
        }

        CatalogShownCount = rows.Count;
        CatalogTotalCount = _allCatalogEntries.Count;

        // Every row above is a brand-new CatalogEntryViewModel, so without this the DataGrid's
        // two-way SelectedItem binding just drops back to null on every filter/search change (or
        // after Download & extract re-applies filters) even when the same mod still matches —
        // the detail pane and its buttons would disappear for no reason the user did anything
        // wrong. Mirrors ReloadNexusEntries' own selection-by-ID restore just below.
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

        var entry = SelectedCatalogEntry.Entry;
        var downloadUrl = entry.ExmodzUrl ?? entry.PakUrl;
        if (downloadUrl is null)
        {
            CatalogStatusMessage = $"'{entry.Name}' has no downloadable file listed.";
            return;
        }

        var isExmodz = entry.ExmodzUrl is not null;
        var tempPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_{Guid.NewGuid():N}{(isExmodz ? ".EXMODZ" : ".pak")}");

        try
        {
            CatalogStatusMessage = $"Downloading '{entry.Name}'…";
            var bytes = await _downloadHttpClient.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(tempPath, bytes);

            var imported = isExmodz ? _libraryRepository.Import(tempPath) : _libraryRepository.ImportPak(tempPath);
            CatalogStatusMessage = $"Downloaded and imported '{imported.Name}'.";
            ApplyCatalogFilters();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            CatalogStatusMessage = $"Download failed: {ex.Message}";
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

    [RelayCommand]
    private void OpenCatalogReadme()
    {
        if (SelectedCatalogEntry?.Entry.ReadmeUrl is not { } url)
        {
            return;
        }

        OpenUrl(url);
    }

    // --- Nexus Mods tab ---

    [RelayCommand]
    private void AddNexusUrl()
    {
        var match = NexusModUrlPattern.Match(NewNexusUrl);
        if (!match.Success)
        {
            NexusStatusMessage = "That doesn't look like a nexusmods.com/icarus/mods/<id> URL.";
            return;
        }

        var nexusId = int.Parse(match.Groups[1].Value);
        // Nexus's own mod name isn't fetchable without API access (see NexusWatchlistEntry) —
        // this placeholder is meant to be renamed via the editable Name column.
        var entry = new NexusWatchlistEntry { NexusId = nexusId, Url = NewNexusUrl.Trim(), Name = $"Nexus mod #{nexusId}" };

        try
        {
            _watchlistStore.Add(entry);
        }
        catch (Exception ex)
        {
            NexusStatusMessage = $"Couldn't save watchlist: {ex.Message}";
            return;
        }

        NewNexusUrl = "";
        NexusStatusMessage = $"Tracking mod #{nexusId} — rename it in the list.";
        ReloadNexusEntries();
    }

    [RelayCommand]
    private void RemoveNexusEntry()
    {
        if (SelectedNexusEntry is null)
        {
            return;
        }

        var id = SelectedNexusEntry.NexusId;
        // Cancel first: a pending debounced name save firing after Remove() below could write
        // this entry's old name back into the store under an id a later Add() might reuse.
        SelectedNexusEntry.CancelPendingSave();
        try
        {
            _watchlistStore.Remove(id);
        }
        catch (Exception ex)
        {
            NexusStatusMessage = $"Couldn't update watchlist: {ex.Message}";
            return;
        }

        SelectedNexusEntry = null;
        ReloadNexusEntries();
    }

    [RelayCommand]
    private void BrowseOnNexus()
    {
        if (SelectedNexusEntry is not { } selected)
        {
            return;
        }

        OpenUrl(selected.Url);
    }

    /// <summary>
    /// There's no in-app Nexus API to actually check anything against (see NexusWatchlistEntry) —
    /// opening the mod's own page is the honest, useful thing this button can do instead of
    /// either faking a check or being a dead no-op.
    /// </summary>
    [RelayCommand]
    private void CheckForNexusUpdates()
    {
        if (SelectedNexusEntry is not { } selected)
        {
            return;
        }

        NexusStatusMessage = "No in-app Nexus API yet — opening the mod page to check manually.";
        OpenUrl(selected.Url);
    }

    [RelayCommand]
    private void ImportNexusFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the downloaded mod file",
            Filter = "Mod package (*.EXMODZ;*.pak)|*.EXMODZ;*.pak",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var isPak = dialog.FileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
            var imported = isPak ? _libraryRepository.ImportPak(dialog.FileName) : _libraryRepository.Import(dialog.FileName);
            NexusStatusMessage = $"Imported '{imported.Name}'.";
            ApplyCatalogFilters();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            NexusStatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private void ReloadNexusEntries()
    {
        var previouslySelectedId = SelectedNexusEntry?.NexusId;

        NexusEntries.Clear();
        var query = _watchlistStore.Entries.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(NexusFilterText))
        {
            var filter = NexusFilterText.Trim();
            query = query.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var entry in query.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            NexusEntries.Add(new NexusWatchlistItemViewModel(entry, (id, name) => _watchlistStore.UpdateName(id, name)));
        }

        SelectedNexusEntry = NexusEntries.FirstOrDefault(e => e.NexusId == previouslySelectedId);
    }

    private void ApplyNexusFilter() => ReloadNexusEntries();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening the default browser is best-effort UX, not a core operation — swallow
            // rather than crash the app if the OS can't find a handler for the URL.
        }
    }
}
