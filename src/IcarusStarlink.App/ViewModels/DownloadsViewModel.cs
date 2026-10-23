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
/// Two independent parts in one shared VM: the IMM Database catalog (the Database page's IMM
/// Database tab) and the downloads list (the Downloads page) — one instance, also used by the
/// Nexus page for its downloads.
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
    private readonly ModReplacementService _modReplacement;

    /// <summary>How every copy of a mod is replaced (this page's Activate/Reinstall, and Library's Get update and Reinstall original).</summary>
    public ModReplacementService ModReplacement => _modReplacement;

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
        IDialogService? dialogService = null,
        ModReplacementService? modReplacement = null)
    {
        // The app passes the shared one (with the fix service and the saved source paks); a
        // default is enough to replace copies safely when nothing else is wired up.
        _modReplacement = modReplacement ?? new ModReplacementService(
            libraryRepository, ue4ssModRepository, ue4ssModMetaStore, new IcarusStarlink.PakIO.Install.Ue4ssModStateService(ue4ssModRepository),
            settingsService, Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(pendingDownloadsDirectory)) ?? pendingDownloadsDirectory, "Backups"),
            dialogService);
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
            // Refreshes every row AND re-sorts them into the visible list — a mod deleted in
            // Library must come back out from under "Show installed" as removed. Send is
            // synchronous, so a disk error here would surface in whatever import or delete sent
            // the message — it is reported on this page instead.
            try
            {
                self.RefreshVisiblePendingDownloads();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                self.PendingDownloadStatusMessage = $"Couldn't refresh the downloads list: {ex.Message}";
            }
        });
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

    /// <summary>Records that a download was installed by something other than Activate (Library's Get update imports the catalog file itself).</summary>
    public void RecordActivation(PendingDownloadItemViewModel item, IReadOnlyList<PendingDownloadActivatedItem> items)
    {
        _pendingDownloadStore.SetActivation(item.ModId, item.FileId, items, []);
        item.SetActivated(items, []);
        RefreshVisiblePendingDownloads();
    }

    /// <summary>The kept download a Library mod was installed from (its file still on disk), or null.</summary>
    public PendingDownloadItemViewModel? FindOriginalDownload(string libraryFolderName) =>
        PendingDownloads.FirstOrDefault(d =>
            d.ActivatedItems.Any(i => i.Kind == PendingDownloadActivationKind.Library && string.Equals(i.FolderName, libraryFolderName, StringComparison.OrdinalIgnoreCase))
            && File.Exists(ResolveLocalFilePath(d)));

    private void ReloadPendingDownloads()
    {
        PendingDownloads.Clear();
        foreach (var entry in _pendingDownloadStore.Entries.OrderByDescending(e => e.DownloadedAtUtc))
        {
            PendingDownloads.Add(new PendingDownloadItemViewModel(entry, _libraryRepository, _ue4ssModRepository, Ue4ssNamesInGame));
        }

        RefreshVisiblePendingDownloads();
    }

    /// <summary>UE4SS mods currently enabled (sitting in the game's Mods folder rather than staging) — empty when the game path isn't set.</summary>
    private IReadOnlyList<string> Ue4ssNamesInGame()
    {
        var icarusContentPath = _settingsService.Current.IcarusContentPath;
        return !string.IsNullOrWhiteSpace(icarusContentPath)
            ? _ue4ssModRepository.ListInstalledInGame(Ue4ssGamePaths.ResolveModsFolder(icarusContentPath))
            : [];
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
