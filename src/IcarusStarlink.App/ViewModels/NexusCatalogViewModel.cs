using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One card in the native Nexus catalog — the API's own mod info plus this machine's own local
/// status. Nexus's v1 API exposes no download-history/notification feed to sync the website's own
/// "you have this" badges from, so these are computed locally instead (which also makes them
/// honest about THIS install rather than any device the account ever touched): LocalBadge says the
/// strongest local relationship (In Library &gt; Downloaded &gt; Tracked), HasUpdate compares the
/// card's live version against the Library copy's. IsTracked is watchlist MEMBERSHIP specifically
/// (not the same as LocalBadge=="Tracked" — a mod can be both tracked AND already in the Library,
/// where LocalBadge shows the stronger "In Library" but the Track toggle still needs to know it's
/// tracked), so the Tracked filter and the card's Track/Untrack button both key off this instead.
/// </summary>
/// <summary>
/// DisplayName is the tracked watchlist entry's own stored Name when IsTracked (which
/// RenameTrackedEntry can override), falling back to the live Mod.Name otherwise — distinct from
/// Mod.Name so a rename is actually visible: without this, the card would always show the live
/// Nexus name regardless of any stored override.
/// </summary>
public sealed record NexusCatalogRow(NexusModInfo Mod, string? LocalBadge, bool HasUpdate, bool IsTracked, string DisplayName);

/// <summary>
/// The whole Nexus page — a real mod list with images driven by the saved API key, so browsing
/// needs no separate website sign-in. The three curated lists (trending / latest added / latest
/// updated) and per-mod Download/Track/Open actions use Nexus's v1 REST API; the search box uses
/// its newer v2 GraphQL API instead (v1 has no search endpoint at all). There's no embedded browser
/// here anymore — it was removed once the native list + search covered everything it was for; a
/// full mod page or a website Mod Manager Download still opens in the user's normal browser via
/// Open page.
/// </summary>
public sealed partial class NexusCatalogViewModel : ObservableObject
{
    private readonly INexusApiClient _nexusApiClient;
    private readonly ICredentialStore _credentialStore;
    private readonly INexusWatchlistStore _watchlistStore;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IPendingDownloadStore _pendingDownloadStore;
    private readonly IDialogService _dialogService;

    /// <summary>
    /// Exposed so the Nexus page's own XAML can reach DownloadsViewModel's Track-by-URL (for a mod
    /// that hasn't shown up in search/All yet) and its batch Check-for-updates — reusing those
    /// commands directly instead of duplicating them here, since tracking a mod not found by the
    /// live API is the one genuine case per-card Track can't cover.
    /// </summary>
    public DownloadsViewModel Downloads { get; }

    /// <summary>Guards against two overlapping loads (a quick list-pill double-switch) finishing out of order — only the newest load's results land.</summary>
    private int _loadVersion;

    /// <summary>The last successful fetch, kept so local badges can be recomputed (a Library import/delete elsewhere, a Track click) without another API round-trip.</summary>
    private IReadOnlyList<NexusModInfo> _lastFetched = [];

    /// <summary>Tracked mods already notified about having an update, keyed by the Nexus version string they were notified for — so a page revisit or filter toggle (both call RebuildRows) doesn't re-log the same update every time, only genuinely new ones. Keyed by version rather than a plain HashSet&lt;int&gt; of mod IDs: a mod ID alone can't tell "already notified for v1.2" apart from "already notified, but v1.3 just came out" — the former should stay quiet, the latter must notify again.</summary>
    private readonly Dictionary<int, string> _notifiedUpdateVersions = [];

    private readonly IActivityLog _activityLog;

    /// <summary>This IS the Nexus page — there is no wrapper ViewModel around it any more (the embedded browser it used to sit beside was removed as redundant with signing in via Settings).</summary>
    public string Title => "Nexus";

    /// <summary>"All" (the whole catalog via GraphQL, most-endorsed first, paged) plus Nexus's three curated v1 lists. All is the default: it works even before an API key is configured, so the page is never empty on first visit.</summary>
    public static IReadOnlyList<string> BrowseKinds { get; } = ["All", .. Enum.GetValues<NexusModList>().Select(k => k.ToString())];

    public ObservableCollection<NexusCatalogRow> Mods { get; } = [];

    [ObservableProperty]
    private string _selectedKind = "All";

    private const int AllPageSize = 30;

    /// <summary>The catalog's own total in All mode, so page navigation knows how many pages exist — 229 real mods would mean 8 "Load more" clicks to reach the end, so this is real Prev/Next paging (replaces the shown page) rather than an ever-growing appended list.</summary>
    private int _allTotalCount;

    /// <summary>0-based offset of the currently-shown page in All mode — reset to 0 whenever the browse kind, search text, or an explicit Refresh changes what's being paged through.</summary>
    private int _allPageOffset;

    [ObservableProperty]
    private bool _canGoToPreviousPage;

    [ObservableProperty]
    private bool _canGoToNextPage;

    [ObservableProperty]
    private string? _pageDisplay;

    /// <summary>Hides mods this install already has (In Library or sitting in Pending Downloads) — a "what am I missing" view.</summary>
    [ObservableProperty]
    private bool _hideOwned;

    /// <summary>Shows only mods whose live Nexus version differs from the Library copy's — a "what can I update" view.</summary>
    [ObservableProperty]
    private bool _updatesOnly;

    /// <summary>Shows only mods you've tracked (added to Downloads' watchlist) — the Nexus page's own view of "my list", replacing a separate tracked-mods grid with a filter on the same live cards.</summary>
    [ObservableProperty]
    private bool _trackedOnly;

    partial void OnHideOwnedChanged(bool value) => ApplyDisplayFilters();

    partial void OnUpdatesOnlyChanged(bool value) => ApplyDisplayFilters();

    partial void OnTrackedOnlyChanged(bool value) => ApplyDisplayFilters();

    private void ApplyDisplayFilters()
    {
        var searchText = SearchText.Trim();
        RebuildRows();
        UpdateStatusAfterLoad(searchText.Length > 0, searchText.Length == 0 && SelectedKind == "All", searchText);
    }

    /// <summary>Non-empty switches the page from the curated lists to live GraphQL search results; clearing it goes back to the selected list. Debounced so typing doesn't fire a network round-trip per keystroke — same 250ms pattern Library's own search box uses.</summary>
    [ObservableProperty]
    private string _searchText = "";

    private readonly DebounceTimer _searchDebounceTimer;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public NexusCatalogViewModel(
        INexusApiClient nexusApiClient, ICredentialStore credentialStore, INexusWatchlistStore watchlistStore,
        ILibraryRepository libraryRepository, IPendingDownloadStore pendingDownloadStore, DownloadsViewModel downloads,
        IActivityLog activityLog, IDialogService dialogService)
    {
        _nexusApiClient = nexusApiClient;
        _credentialStore = credentialStore;
        _watchlistStore = watchlistStore;
        _libraryRepository = libraryRepository;
        _pendingDownloadStore = pendingDownloadStore;
        Downloads = downloads;
        _activityLog = activityLog;
        _dialogService = dialogService;

        // Activating/deleting a mod elsewhere changes what "In Library" is true for — recompute
        // badges from the cached fetch, deliberately with no network involved (this VM is a
        // singleton that hears the message even while the page isn't shown, so a network refetch
        // here would silently fire on every import anywhere in the app).
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((NexusCatalogViewModel)recipient).RebuildRows());

        _searchDebounceTimer = new DebounceTimer(TimeSpan.FromMilliseconds(250), () => _ = LoadAsync());

        // Same fire-and-forget-with-own-try/catch shape DownloadsViewModel's constructor already
        // uses for its catalog fetch — LoadAsync can't leak an unobserved exception.
        _ = LoadAsync();
    }

    partial void OnSelectedKindChanged(string value) => _ = LoadAsync();

    partial void OnSearchTextChanged(string value) => _searchDebounceTimer.Restart();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    /// <summary>A fresh load — kind/search change or an explicit Refresh — always starts back at page 1 of All, since the offset from a previous browse/search context wouldn't mean anything here.</summary>
    private Task LoadAsync()
    {
        _allPageOffset = 0;
        return LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        var version = ++_loadVersion;
        var searchText = SearchText.Trim();
        var isSearch = searchText.Length > 0;
        var isAll = !isSearch && SelectedKind == "All";
        CanGoToPreviousPage = false;
        CanGoToNextPage = false;
        PageDisplay = null;

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        // Search and the All list work even with no key (the v2 GraphQL endpoint answers
        // unauthenticated — confirmed live); only the curated v1 lists genuinely require one.
        if (apiKey is null && !isSearch && !isAll)
        {
            Mods.Clear();
            StatusMessage = "Sign in with your Nexus API key in Settings to use this list — All and search work without one.";
            return;
        }

        IsLoading = true;
        StatusMessage = null;
        try
        {
            IReadOnlyList<NexusModInfo> mods;
            if (isSearch)
            {
                mods = await _nexusApiClient.SearchModsAsync(apiKey, "icarus", searchText);
            }
            else if (isAll)
            {
                var page = await _nexusApiClient.ListAllModsAsync(apiKey, "icarus", offset: _allPageOffset, count: AllPageSize);
                mods = page.Mods;
                _allTotalCount = page.TotalCount;
            }
            else
            {
                mods = await _nexusApiClient.GetModListAsync(apiKey!, "icarus", Enum.Parse<NexusModList>(SelectedKind));
            }

            if (version != _loadVersion)
            {
                return;
            }

            // A null Name means the mod is under moderation (Nexus's own documented shape) — a
            // card with no name, no summary, and nothing to open isn't worth rendering.
            _lastFetched = [.. mods.Where(m => m.Name is not null)];
            if (isAll)
            {
                CanGoToPreviousPage = _allPageOffset > 0;
                CanGoToNextPage = _allPageOffset + AllPageSize < _allTotalCount;
                var totalPages = Math.Max(1, (int)Math.Ceiling(_allTotalCount / (double)AllPageSize));
                PageDisplay = $"Page {_allPageOffset / AllPageSize + 1} of {totalPages}";
            }

            RebuildRows();
            UpdateStatusAfterLoad(isSearch, isAll, searchText);
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                StatusMessage = $"Couldn't load: {ex.Message}";
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private Task NextPageAsync()
    {
        if (IsLoading || !CanGoToNextPage)
        {
            return Task.CompletedTask;
        }

        _allPageOffset += AllPageSize;
        return LoadPageAsync();
    }

    [RelayCommand]
    private Task PreviousPageAsync()
    {
        if (IsLoading || !CanGoToPreviousPage)
        {
            return Task.CompletedTask;
        }

        _allPageOffset = Math.Max(0, _allPageOffset - AllPageSize);
        return LoadPageAsync();
    }

    private void UpdateStatusAfterLoad(bool isSearch, bool isAll, string searchText)
    {
        var filtered = HideOwned || UpdatesOnly || TrackedOnly;
        StatusMessage = Mods.Count == 0
            ? (_lastFetched.Count > 0 && filtered ? "Nothing matches the filters."
                : isSearch ? $"No mods match '{searchText}'."
                : "Nexus returned nothing for this list right now.")
            : isSearch ? $"{Mods.Count} result(s) for '{searchText}'."
            : isAll ? $"{Mods.Count} shown of {_allTotalCount} total."
            : null;
    }

    private void RebuildRows()
    {
        var libraryByNexusId = _libraryRepository.GetAll()
            .Where(e => e.NexusModId is not null)
            .GroupBy(e => e.NexusModId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var pendingModIds = _pendingDownloadStore.Entries.Select(e => e.ModId).ToHashSet();
        var trackedEntriesByModId = _watchlistStore.Entries.ToDictionary(e => e.NexusId);
        var trackedModIds = trackedEntriesByModId.Keys.ToHashSet();

        Mods.Clear();
        foreach (var mod in _lastFetched)
        {
            var libraryEntry = libraryByNexusId.GetValueOrDefault(mod.ModId);
            var badge = libraryEntry is not null ? "In Library"
                : pendingModIds.Contains(mod.ModId) ? "Downloaded"
                : trackedModIds.Contains(mod.ModId) ? "Tracked"
                : null;
            var hasUpdate = libraryEntry is not null
                && !string.IsNullOrEmpty(libraryEntry.Version)
                && !string.IsNullOrEmpty(mod.Version)
                && !string.Equals(libraryEntry.Version, mod.Version, StringComparison.OrdinalIgnoreCase);
            var isTracked = trackedModIds.Contains(mod.ModId);

            // Surface a genuinely new update on a tracked mod through the Activity panel instead
            // of a dedicated "check for updates" button — this already runs on every load/refresh/
            // filter-toggle, so there's nothing to click; _notifiedUpdateVersions keeps a revisit
            // from re-announcing the same update every time RebuildRows runs, while still
            // re-notifying if the mod has since moved on to a newer update than the one last
            // announced (a plain "already notified this mod ID" set — the original shape here —
            // could never do that: once an ID was added it stayed notified forever).
            if (isTracked && hasUpdate && !string.Equals(_notifiedUpdateVersions.GetValueOrDefault(mod.ModId), mod.Version, StringComparison.OrdinalIgnoreCase))
            {
                _notifiedUpdateVersions[mod.ModId] = mod.Version;
                _activityLog.Log($"Update available for tracked mod '{mod.Name}' (v{libraryEntry!.Version} installed → v{mod.Version} on Nexus).", ActivityEntryKind.Info);
            }

            // The display filters: HideOwned drops what this install already has (In Library or a
            // file waiting in Pending Downloads — Tracked is just a bookmark, not ownership);
            // UpdatesOnly keeps only cards whose live version differs from the Library copy's;
            // TrackedOnly keys off watchlist membership directly, not the LocalBadge string, since
            // a mod already in the Library still needs to show here if it's also tracked.
            if (HideOwned && badge is "In Library" or "Downloaded")
            {
                continue;
            }

            if (UpdatesOnly && !hasUpdate)
            {
                continue;
            }

            if (TrackedOnly && !isTracked)
            {
                continue;
            }

            var displayName = isTracked && trackedEntriesByModId.TryGetValue(mod.ModId, out var trackedEntry)
                ? trackedEntry.Name
                : mod.Name ?? $"Nexus mod #{mod.ModId}";
            Mods.Add(new NexusCatalogRow(mod, badge, hasUpdate, isTracked, displayName));
        }
    }

    /// <summary>
    /// Direct download from the card — delegates the whole "find the primary file, fetch it"
    /// pipeline to DownloadsViewModel.ResolvePrimaryFileAndFetchAsync, the app's own always-
    /// available download path independent of whether the nxm:// protocol handler is registered.
    /// </summary>
    [RelayCommand]
    private async Task DownloadModAsync(NexusModInfo? mod)
    {
        if (mod is null)
        {
            return;
        }

        StatusMessage = $"Finding '{mod.Name}''s main file…";
        await Downloads.ResolvePrimaryFileAndFetchAsync(mod.ModId, mod.Name);
        StatusMessage = Downloads.PendingDownloadStatusMessage;
        RebuildRows();
    }

    /// <summary>
    /// Nexus's own real per-version changelog (a separate endpoint from a file's own
    /// changelog_html — this one is the mod-wide history shown on its own page). Looks up the
    /// currently-shown version first (what someone browsing the card actually wants to know) and
    /// falls back to the whole history if that exact version string isn't a key in the response
    /// (a mismatch between the mod's own displayed version and its changelog's own version
    /// labels is possible — an author can word these differently).
    /// </summary>
    [RelayCommand]
    private async Task ShowChangelogAsync(NexusModInfo? mod)
    {
        if (mod is null)
        {
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            StatusMessage = "Sign in with your Nexus API key in Settings first.";
            return;
        }

        StatusMessage = $"Loading '{mod.Name}''s changelog…";
        try
        {
            var changelogs = await _nexusApiClient.GetChangelogsAsync(apiKey, "icarus", mod.ModId);
            StatusMessage = null;

            if (changelogs.Count == 0)
            {
                StatusMessage = $"'{mod.Name}' has no changelog recorded.";
                return;
            }

            IReadOnlyList<ChangelogVersionEntry> versions = changelogs.TryGetValue(mod.Version, out var currentLines)
                ? [new ChangelogVersionEntry(mod.Version, currentLines)]
                : [.. changelogs.Select(kv => new ChangelogVersionEntry(kv.Key, kv.Value))];

            new NexusChangelogWindow(mod.Name ?? $"Mod #{mod.ModId}", versions) { Owner = Application.Current.MainWindow }.Show();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load changelog: {ex.Message}";
        }
    }

    /// <summary>
    /// Toggles the mod's membership in Downloads' Nexus watchlist — Track adds it (with its real
    /// name straight away, unlike the Add-mod-URL path's "Nexus mod #N" placeholder, since the API
    /// already told us the name here); clicking again on an already-tracked mod removes it. One
    /// command for both directions, so the same card button works whichever state it's in.
    /// </summary>
    [RelayCommand]
    private void TrackMod(NexusModInfo? mod)
    {
        if (mod is null)
        {
            return;
        }

        try
        {
            if (_watchlistStore.Entries.Any(e => e.NexusId == mod.ModId))
            {
                _watchlistStore.Remove(mod.ModId);
                StatusMessage = $"Stopped tracking '{mod.Name}'.";
            }
            else
            {
                _watchlistStore.Add(new NexusWatchlistEntry
                {
                    NexusId = mod.ModId,
                    Url = NexusModWebUrl.For(mod.ModId),
                    Name = mod.Name ?? $"Nexus mod #{mod.ModId}",
                    Author = mod.Author ?? "",
                    Version = mod.Version ?? "",
                });
                StatusMessage = $"Tracking '{mod.Name}' — use the Tracked filter above to see everything you're tracking.";
            }

            RebuildRows();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save the watchlist: {ex.Message}";
        }
    }

    /// <summary>
    /// Overrides a tracked entry's own display name — same rename pattern LibraryViewModel.RenameItem
    /// uses, reusing the same RenameModDialog, with the live Nexus name (not null) as Reset's target:
    /// a tracked watchlist entry always has SOME name, so "reset" here means "back to what Nexus
    /// itself currently calls it" rather than Library's own "clear the override entirely" meaning.
    /// Only meaningful for a currently-tracked row — RenameTrackedEntry_CanExecute (via IsTracked)
    /// keeps the command disabled otherwise, matching how a rename affordance shouldn't be visible
    /// for a row with no stored override to change in the first place.
    /// </summary>
    [RelayCommand]
    private void RenameTrackedEntry(NexusCatalogRow? row)
    {
        if (row is null || !row.IsTracked)
        {
            return;
        }

        var result = _dialogService.PromptRename(
            row.DisplayName,
            description: "Overrides how this tracked mod displays here — never touches anything on Nexus itself.",
            resetValue: row.Mod.Name ?? $"Nexus mod #{row.Mod.ModId}",
            resetLabel: "Reset to Nexus name",
            resetTooltip: "Goes back to whatever Nexus itself currently calls this mod");
        if (result.Cancelled || result.NewDisplayName is not { } newName)
        {
            return;
        }

        try
        {
            _watchlistStore.UpdateName(row.Mod.ModId, newName);
            RebuildRows();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't rename: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenModPage(NexusModInfo? mod)
    {
        if (mod is null)
        {
            return;
        }

        UrlOpener.TryOpen(NexusModWebUrl.For(mod.ModId));
    }
}
