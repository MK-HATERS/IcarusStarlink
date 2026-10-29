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
    /// <summary>How every copy of a mod is replaced (this page's Activate/Reinstall, and Library's Get update and Reinstall original).</summary>
    private readonly ModReplacementService _modReplacement;

    /// <summary>UE4SS names staged and in the game — one answer for every check on this page.</summary>
    private readonly Ue4ssInstalledNames _ue4ssNames;

    /// <summary>
    /// One install at a time. The nxm:// hand-off, Download &amp; extract, Download selected and the
    /// Nexus cards could each run an import at once, and Library import isn't safe for concurrent
    /// writers (two "which copies does this replace?" lookups could each miss the other's new mod).
    /// Downloads themselves still run side by side; only the install step waits its turn.
    /// </summary>
    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>Downloads and installs running now — what BackgroundWorkRequestMessage is answered from.</summary>
    private int _downloadsRunning;
    private int _installsRunning;

    /// <summary>Cancelled by Cancel downloads, then replaced so later downloads start fresh.</summary>
    private CancellationTokenSource _downloadCancellation = new();

    /// <summary>Drives the Downloads page's Cancel downloads button.</summary>
    [ObservableProperty]
    private bool _isDownloading;

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
        _ue4ssNames = new Ue4ssInstalledNames(ue4ssModRepository, settingsService);

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
        _catalogLoad = LoadCatalogAsync();

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

        // An app update or closing the window must not cut a download or an install off halfway.
        WeakReferenceMessenger.Default.Register<BackgroundWorkRequestMessage>(this, (recipient, message) =>
        {
            var self = (DownloadsViewModel)recipient;
            if (Volatile.Read(ref self._downloadsRunning) > 0)
            {
                message.Reply("a Nexus download");
            }

            if (Volatile.Read(ref self._installsRunning) > 0)
            {
                message.Reply("a mod being installed");
            }
        });
    }

    [RelayCommand]
    private void DiscardPendingDownload(PendingDownloadItemViewModel? item)
    {
        if (item is not null)
        {
            Discard([item]);
        }
    }

    /// <summary>The rows ticked in the Downloads list (DataGrid.SelectedItems isn't bindable: the view's code-behind hands them over).</summary>
    private IReadOnlyList<PendingDownloadItemViewModel> _selectedPendingDownloads = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDiscardSelected))]
    [NotifyCanExecuteChangedFor(nameof(DiscardSelectedDownloadsCommand))]
    private int _selectedPendingDownloadCount;

    /// <summary>The toolbar's Discard selected shows for two or more rows; one row has its own Discard.</summary>
    public bool CanDiscardSelected => SelectedPendingDownloadCount > 1;

    public void SetSelectedPendingDownloads(IReadOnlyList<PendingDownloadItemViewModel> items)
    {
        _selectedPendingDownloads = items;
        SelectedPendingDownloadCount = items.Count;
    }

    [RelayCommand(CanExecute = nameof(CanDiscardSelected))]
    private void DiscardSelectedDownloads() => Discard([.. _selectedPendingDownloads]);

    /// <summary>
    /// Deletes each download's file and drops it from the list. The file of a download that's
    /// installed is what Library's Reinstall original and this page's Reinstall put back, so those
    /// ask first (once, naming them); a download never installed, or since removed, goes in one
    /// click. No deletes nothing. The installed mods themselves always stay.
    /// </summary>
    private void Discard(IReadOnlyList<PendingDownloadItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (_dialogService is not null)
        {
            // One read of the Library for the question, not a stale one from the last refresh.
            var installed = InstalledModsSnapshot.Take(_libraryRepository, _ue4ssNames);
            foreach (var item in items)
            {
                item.Refresh(installed);
            }

            var installedItems = items.Where(i => i.HasInstalledParts).ToList();
            if (installedItems.Count > 0 && !_dialogService.Confirm(DiscardQuestion(items.Count, installedItems), "Discard download", ThemedConfirmSeverity.Warning))
            {
                PendingDownloadStatusMessage = "Nothing discarded.";
                return;
            }
        }

        var notDeleted = new List<string>();
        foreach (var item in items)
        {
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
                notDeleted.Add($"{item.FileName}: {ex.Message}");
            }

            _pendingDownloadStore.Remove(item.ModId, item.FileId);
        }

        ReloadPendingDownloads();
        PendingDownloadStatusMessage = notDeleted.Count > 0
            ? $"Removed from the list, but couldn't delete the file: {string.Join("; ", notDeleted)}"
            : items.Count == 1 ? $"Discarded '{items[0].FileName}'." : $"Discarded {items.Count} downloads.";
    }

    /// <summary>What Discard asks when some of the downloads are installed: what is lost, and that the mods stay.</summary>
    internal static string DiscardQuestion(int discarding, IReadOnlyList<PendingDownloadItemViewModel> installed)
    {
        if (discarding == 1)
        {
            return $"'{installed[0].DisplayName}' is installed. Discard deletes its downloaded file.\n\n"
                + "The mod stays in your Library, but Reinstall original won't be able to put this mod back as its author made it, "
                + "and Reinstall here won't work either — you'd have to download it again.\n\nDiscard it?";
        }

        var names = string.Join("\n", installed.Take(10).Select(i => $"• {i.DisplayName}"))
            + (installed.Count > 10 ? $"\n• …and {installed.Count - 10} more" : "");
        var these = installed.Count == 1 ? "This one is installed" : $"These {installed.Count} are installed";
        return $"Discard deletes the downloaded files of {discarding} downloads. {these}:\n\n{names}\n\n"
            + "Those mods stay in your Library, but Reinstall original won't be able to put them back as their authors made them, "
            + $"and Reinstall here won't work either — you'd have to download them again.\n\nDiscard all {discarding}?";
    }

    /// <summary>
    /// Saves what a download installed and shows it on its row. The row the caller holds can be
    /// stale (another download landing reloads the list), so the row now listed for the same file
    /// is updated too.
    /// </summary>
    private void MarkActivated(PendingDownloadItemViewModel item, IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<string> requirements)
    {
        _pendingDownloadStore.SetActivation(item.ModId, item.FileId, items, requirements);
        item.SetActivated(items, requirements);
        if (PendingDownloads.FirstOrDefault(p => p.ModId == item.ModId && p.FileId == item.FileId) is { } current && !ReferenceEquals(current, item))
        {
            current.SetActivated(items, requirements);
        }

        RefreshVisiblePendingDownloads();
    }

    /// <summary>The kept download a Library mod was installed from (its file still on disk), or null.</summary>
    public PendingDownloadItemViewModel? FindOriginalDownload(string libraryFolderName) =>
        PendingDownloads.FirstOrDefault(d =>
            d.ActivatedItems.Any(i => i.Kind == PendingDownloadActivationKind.Library && string.Equals(i.FolderName, libraryFolderName, StringComparison.OrdinalIgnoreCase))
            && File.Exists(ResolveLocalFilePath(d)));

    /// <summary>
    /// Matches the list to the store, keeping each row whose file is unchanged — overlapping
    /// downloads used to rebuild every row, so an Activate still running on one then updated a row
    /// no longer in the list (it kept saying "Activate").
    /// </summary>
    private void ReloadPendingDownloads()
    {
        var rows = _pendingDownloadStore.Entries
            .OrderByDescending(e => e.DownloadedAtUtc)
            .Select(entry => PendingDownloads.FirstOrDefault(row => row.IsSameDownload(entry))
                ?? new PendingDownloadItemViewModel(entry, _libraryRepository, _ue4ssModRepository, _ue4ssNames))
            .ToList();
        ObservableCollectionSync.SyncTo(PendingDownloads, rows);

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
        // One read of the Library and the game's Mods folder for every row.
        var installed = InstalledModsSnapshot.Take(_libraryRepository, _ue4ssNames);
        foreach (var item in PendingDownloads)
        {
            item.Refresh(installed);
        }

        ObservableCollectionSync.SyncTo(VisiblePendingDownloads, [.. PendingDownloads.Where(item => ShowInstalledDownloads || !item.IsInstalled)]);
        OnPropertyChanged(nameof(InstalledDownloadCount));
        OnPropertyChanged(nameof(DownloadsEmptyText));
    }

    /// <summary>What the Downloads page says instead of an empty table; null while it has rows.</summary>
    public string? DownloadsEmptyText => VisiblePendingDownloads.Count > 0
        ? null
        : PendingDownloads.Count == 0
            ? "Nothing downloaded yet — get mods from Database, or click Mod Manager Download on nexusmods.com."
            : InstalledDownloadCount == 1
                ? "Your one download is installed — tick Show installed to see it."
                : $"All {InstalledDownloadCount} downloads are installed — tick Show installed to see them.";

    [RelayCommand]
    private void CancelDownloads()
    {
        var cancelling = _downloadCancellation;
        _downloadCancellation = new CancellationTokenSource();
        cancelling.Cancel();
        PendingDownloadStatusMessage = "Cancelling downloads…";
    }

    /// <summary>The Downloads page's "Downloading" list: every download in progress, then installing. Empty (and hidden) when nothing is.</summary>
    public ObservableCollection<ActiveDownloadViewModel> ActiveDownloads { get; } = [];

    public bool HasActiveDownloads => ActiveDownloads.Count > 0;

    /// <summary>
    /// Counts a download as running (for Cancel downloads and the busy check) and shows its row in
    /// the Downloading list. The row's Token is what the download runs with: its own Cancel and
    /// Cancel downloads both stop it. Always pair with FinishDownloadRow in a finally.
    /// </summary>
    private ActiveDownloadViewModel BeginDownload(string name)
    {
        Interlocked.Increment(ref _downloadsRunning);
        IsDownloading = true;
        var row = new ActiveDownloadViewModel(name, _downloadCancellation.Token) { CountedAsDownloading = true };
        ActiveDownloads.Add(row);
        OnPropertyChanged(nameof(HasActiveDownloads));
        return row;
    }

    /// <summary>The bytes are in: no longer a download (Cancel downloads must not cut the install off). Safe to call twice.</summary>
    private void EndDownload(ActiveDownloadViewModel row)
    {
        if (!row.CountedAsDownloading)
        {
            return;
        }

        row.CountedAsDownloading = false;
        if (Interlocked.Decrement(ref _downloadsRunning) == 0)
        {
            IsDownloading = false;
        }
    }

    /// <summary>The download (and its install, if any) is over, however it went: the row goes.</summary>
    private void FinishDownloadRow(ActiveDownloadViewModel row)
    {
        EndDownload(row);
        ActiveDownloads.Remove(row);
        OnPropertyChanged(nameof(HasActiveDownloads));
        row.Dispose();
    }
}
