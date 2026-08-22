using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Catalog.Nexus;
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
/// card's live version against the Library copy's.
/// </summary>
public sealed record NexusCatalogRow(NexusModInfo Mod, string? LocalBadge, bool HasUpdate);

/// <summary>
/// The native "Browse" half of the Nexus page (W1) — a real mod list with images driven by the
/// saved API key, so browsing needs no second website sign-in the way the embedded browser does.
/// Deliberately scoped to what Nexus's v1 API actually offers: the three curated lists
/// (trending / latest added / latest updated) plus per-mod Download/Track/Open actions — v1 has no
/// search endpoint at all (that's the newer GraphQL API, its own future research item), so search
/// and full mod pages stay the embedded browser's job via the page's own "Full site" mode.
/// </summary>
public sealed partial class NexusCatalogViewModel : ObservableObject
{
    private readonly INexusApiClient _nexusApiClient;
    private readonly ICredentialStore _credentialStore;
    private readonly INexusWatchlistStore _watchlistStore;
    private readonly ILibraryRepository _libraryRepository;
    private readonly IPendingDownloadStore _pendingDownloadStore;
    private readonly DownloadsViewModel _downloads;

    /// <summary>Guards against two overlapping loads (a quick list-pill double-switch) finishing out of order — only the newest load's results land.</summary>
    private int _loadVersion;

    /// <summary>The last successful fetch, kept so local badges can be recomputed (a Library import/delete elsewhere, a Track click) without another API round-trip.</summary>
    private IReadOnlyList<NexusModInfo> _lastFetched = [];

    public static IReadOnlyList<NexusModList> ListKinds { get; } = Enum.GetValues<NexusModList>();

    public ObservableCollection<NexusCatalogRow> Mods { get; } = [];

    [ObservableProperty]
    private NexusModList _selectedList = NexusModList.Trending;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    public NexusCatalogViewModel(
        INexusApiClient nexusApiClient, ICredentialStore credentialStore, INexusWatchlistStore watchlistStore,
        ILibraryRepository libraryRepository, IPendingDownloadStore pendingDownloadStore, DownloadsViewModel downloads)
    {
        _nexusApiClient = nexusApiClient;
        _credentialStore = credentialStore;
        _watchlistStore = watchlistStore;
        _libraryRepository = libraryRepository;
        _pendingDownloadStore = pendingDownloadStore;
        _downloads = downloads;

        // Activating/deleting a mod elsewhere changes what "In Library" is true for — recompute
        // badges from the cached fetch, deliberately with no network involved (this VM is a
        // singleton that hears the message even while the page isn't shown, so a network refetch
        // here would silently fire on every import anywhere in the app).
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((NexusCatalogViewModel)recipient).RebuildRows());

        // Same fire-and-forget-with-own-try/catch shape DownloadsViewModel's constructor already
        // uses for its catalog fetch — LoadAsync can't leak an unobserved exception.
        _ = LoadAsync();
    }

    partial void OnSelectedListChanged(NexusModList value) => _ = LoadAsync();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            Mods.Clear();
            StatusMessage = "Sign in with your Nexus API key in Settings to browse here — or use Full site below.";
            return;
        }

        IsLoading = true;
        StatusMessage = null;
        try
        {
            var mods = await _nexusApiClient.GetModListAsync(apiKey, "icarus", SelectedList);
            if (version != _loadVersion)
            {
                return;
            }

            // A null Name means the mod is under moderation (Nexus's own documented shape) — a
            // card with no name, no summary, and nothing to open isn't worth rendering.
            _lastFetched = [.. mods.Where(m => m.Name is not null)];
            RebuildRows();

            StatusMessage = Mods.Count == 0 ? "Nexus returned nothing for this list right now." : null;
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
            {
                StatusMessage = $"Couldn't load the list: {ex.Message}";
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

    private void RebuildRows()
    {
        var libraryByNexusId = _libraryRepository.GetAll()
            .Where(e => e.NexusModId is not null)
            .GroupBy(e => e.NexusModId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var pendingModIds = _pendingDownloadStore.Entries.Select(e => e.ModId).ToHashSet();
        var trackedModIds = _watchlistStore.Entries.Select(e => e.NexusId).ToHashSet();

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

            Mods.Add(new NexusCatalogRow(mod, badge, hasUpdate));
        }
    }

    /// <summary>
    /// Direct download from the card — looks up the mod's primary file (the half a download needs
    /// that the browse lists don't carry), then hands "nxm://icarus/mods/{id}/files/{fileId}" to
    /// DownloadsViewModel.FetchAndDownloadAsync: the exact pipeline a real website Mod Manager
    /// Download click already runs, landing the file in Pending Downloads the same way. A
    /// synthesized link has no key/expires, which the download_link endpoint only accepts for a
    /// premium account — a non-premium key gets Nexus's rejection surfaced with a pointer at the
    /// website's own Mod Manager Download button, which works for everyone.
    /// </summary>
    [RelayCommand]
    private async Task DownloadModAsync(NexusModInfo? mod)
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

        StatusMessage = $"Finding '{mod.Name}''s main file…";
        try
        {
            var files = await _nexusApiClient.GetModFilesAsync(apiKey, "icarus", mod.ModId);
            var file = files.FirstOrDefault(f => f.IsPrimary)
                ?? files.FirstOrDefault(f => string.Equals(f.CategoryName, "MAIN", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault();
            if (file is null)
            {
                StatusMessage = $"'{mod.Name}' has no downloadable files listed.";
                return;
            }

            await _downloads.FetchAndDownloadAsync($"nxm://icarus/mods/{mod.ModId}/files/{file.FileId}");
            StatusMessage = _downloads.PendingDownloadStatusMessage;
            RebuildRows();
        }
        catch (InvalidOperationException ex)
        {
            // Nexus's own rejection — for a non-premium account, direct API downloads aren't
            // allowed at all; the website's Mod Manager Download button is the path that works.
            StatusMessage = $"{ex.Message} Non-premium accounts can't download via the API directly — use Open page and its Mod Manager Download button instead.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
        }
    }

    /// <summary>Adds the mod to Downloads' Nexus watchlist — with its real name straight away, unlike the Add-mod-URL path's "Nexus mod #N" placeholder (there we only have a URL; here the API already told us the name).</summary>
    [RelayCommand]
    private void TrackMod(NexusModInfo? mod)
    {
        if (mod is null)
        {
            return;
        }

        try
        {
            _watchlistStore.Add(new NexusWatchlistEntry
            {
                NexusId = mod.ModId,
                Url = NexusModWebUrl.For(mod.ModId),
                Name = mod.Name ?? $"Nexus mod #{mod.ModId}",
            });
            StatusMessage = $"Tracking '{mod.Name}' — see Downloads → Nexus Mods.";
            RebuildRows();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save the watchlist: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenModPage(NexusModInfo? mod)
    {
        if (mod is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(NexusModWebUrl.For(mod.ModId)) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort UX, same convention as every other open-a-link in this app.
        }
    }
}
