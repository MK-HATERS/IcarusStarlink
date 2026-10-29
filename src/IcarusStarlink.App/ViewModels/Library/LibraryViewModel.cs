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

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ILibraryRepository _repository;
    private readonly IUe4ssModRepository _ue4ssModRepository;
    private readonly IUe4ssModStateService _ue4ssModStateService;
    private readonly IUe4ssModMetaStore _ue4ssModMetaStore;
    private readonly IUe4ssLoaderInstallService _ue4ssLoaderInstallService;
    private readonly ISettingsService _settingsService;
    private readonly IUnrealPakService _unrealPakService;
    private readonly IUassetTextureDecoder _uassetTextureDecoder;
    private readonly IUassetStaticMeshDecoder _uassetStaticMeshDecoder;
    private readonly IUassetSkeletalMeshDecoder _uassetSkeletalMeshDecoder;
    private readonly IUassetSoundDecoder _uassetSoundDecoder;
    private readonly IUassetMaterialDecoder _uassetMaterialDecoder;
    private readonly IOpaquePakAssetPreviewService _opaquePakAssetPreviewService;
    private readonly INexusApiClient _nexusApiClient;
    private readonly ICredentialStore _credentialStore;
    private readonly Func<string, ExmodEditorViewModel> _editorFactory;
    private readonly IActivityLog _activityLog;
    private readonly IActiveDownloadsTracker _activeDownloadsTracker;
    private readonly HttpClient _downloadHttpClient;
    private readonly IPendingDownloadStore _pendingDownloadStore;
    private readonly IModVersionComparer _modVersionComparer;
    private readonly IPrebuiltPakImporter _prebuiltPakImporter;
    private readonly IPrebuiltPakToExmodConverter _prebuiltPakToExmodConverter;
    private readonly IPrebuiltPakSourceStore _prebuiltPakSourceStore;
    private readonly string _thumbnailCacheDirectory;
    private readonly string _pakPreviewCacheDirectory;
    private readonly IDialogService _dialogService;

    /// <summary>Resolved lazily (Merge & Install is constructed on first navigation, not at DI composition time) — same pattern SettingsViewModel already uses to reach it. Only invoked once the user actually adds something to the queue from here, so opening Library alone never forces Merge & Install into existence.</summary>
    private readonly Func<MergeInstallViewModel> _mergeInstallViewModel;

    /// <summary>The shared DownloadsViewModel: the Database page's IMM Database tab binds through this property (its DataContext is this page VM), and this page reads its pending-download status. Not lazy, unlike Nexus below.</summary>
    public DownloadsViewModel Downloads { get; }

    private readonly Func<NexusCatalogViewModel> _nexusCatalogViewModel;

    private readonly string _backupDirectory;

    /// <summary>The extracted game data folder ("Update data folder"'s own output) — needed by CheckModsAgainstCurrentDataAsync to diff each mod's own items against what the game currently defines. Same plain DI-injected string ExmodEditorViewModel/MergeInstallViewModel already take, not a Settings-read.</summary>
    private readonly string _dataFolder;

    private readonly DebounceTimer _searchDebounceTimer;
    private readonly Dictionary<string, LibraryItemViewModel> _itemsByFolderName = [];
    private readonly Dictionary<string, LibraryGroupViewModel> _groupsByKey = [];

    /// <summary>
    /// Open-editor-window count per folder name. Every pass that writes mods (Fix sure problems, the
    /// refresh after a game data update, scale rules) skips a folder with a count above zero, and
    /// the fix window won't open for it: the editor's own _package was loaded from disk before, so
    /// its next Save would write that stale copy straight back over the fix, silently. Skipping the
    /// write (rather than coordinating two writers with a lock, which wouldn't stop the editor's own
    /// later Save from still clobbering it) is the simplest thing that closes the race. A count, not
    /// a HashSet, because nothing stops the same folder being opened in more than one editor window.
    /// </summary>
    private readonly Dictionary<string, int> _openEditorCountByFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last row clicked without Shift held — a Shift-click ranges from here, the same anchor concept Explorer/ListBox multi-select use; a LibraryItemViewModel or a Ue4ssModRowViewModel, since a range spans both kinds of row. Moved by every plain/Ctrl click; a stale anchor pointing at a since-deleted row is simply skipped by FlattenModItems' own lookup.</summary>
    private object? _bulkSelectionAnchor;

    /// <summary>Whether a mod folder is still present in this page's own current listing — used by ModDetailWindow to close itself if the mod it's showing gets deleted while its pop-out window is open.</summary>
    public bool ContainsMod(string folderName) => _itemsByFolderName.ContainsKey(folderName);

    public string Title => "Library";

    /// <summary>Each element is either a LibraryGroupViewModel (a real family) or a bare LibraryItemViewModel (standalone) — WPF picks the right DataTemplate by type, the same routing pattern MainWindow uses for pages.</summary>
    public ObservableCollection<object> RootItems { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private LibraryItemViewModel? _selectedItem;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private int _modCount;

    /// <summary>Count of variant families currently shown (LibraryGroup.IsFamily), not the total root-row count — matches the real app's "N mod(s) · M group(s)" wording.</summary>
    [ObservableProperty]
    private int _groupCount;

    /// <summary>Null when every folder on disk read cleanly — see Reload()'s own comment for what sets it.</summary>
    [ObservableProperty]
    private string? _unreadableFoldersMessage;

    // --- Mods tab column visibility — right-click the column header bar to toggle. Author/Version/
    // Source/Status default on (matches what every row already showed before these were toggleable,
    // so upgrading doesn't change anyone's view); Latest/Imported default off, since they're new and
    // showing them for everyone by default would be exactly the clutter the toolbar redesign was
    // trying to reduce elsewhere on this same page. Not persisted across restarts — same "recomputed/
    // reset each session" precedent the IMM Database tab's own column toggles already established.

    [ObservableProperty]
    private bool _showAuthorColumn = true;

    [ObservableProperty]
    private bool _showVersionColumn = true;

    [ObservableProperty]
    private bool _showSourceColumn = true;

    [ObservableProperty]
    private bool _showStatusColumn = true;

    /// <summary>The mod's currently-known version per the last "Check for updates" run — same data the Update badge's own tooltip already shows, just as a real column so it's visible even for a mod that's already up to date.</summary>
    [ObservableProperty]
    private bool _showLatestColumn;

    /// <summary>When the mod was added to this Library.</summary>
    [ObservableProperty]
    private bool _showImportedColumn;

    /// <summary>
    /// Library's UE4SS tab (Phase 8.5 rework) — the single place to manage UE4SS mods, replacing
    /// the old split between this read-only tab and Merge & Install's own separate Staged/Attached
    /// section. Each row's IsEnabled starts equal to its real current state (present in the game's
    /// own Mods folder or not) and only diverges once toggled — nothing actually moves until Apply.
    /// </summary>
    public ObservableCollection<Ue4ssModRowViewModel> Ue4ssMods { get; } = [];

    [ObservableProperty]
    private bool _hasPendingUe4ssChanges;

    [ObservableProperty]
    private string? _ue4ssStatusMessage;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string? _updateCheckStatusMessage;

    [ObservableProperty]
    private bool _isCheckingStaleness;

    [ObservableProperty]
    private bool _isConvertingAllOpaqueMods;

    /// <summary>Per-folder, not page-wide — see ConvertToExmodAsync's own guard comment.</summary>
    private readonly HashSet<string> _foldersCurrentlyConvertingToExmod = [];

    [ObservableProperty]
    private bool _isCheckingEndorsements;

    [ObservableProperty]
    private string? _endorsementCheckStatusMessage;

    [ObservableProperty]
    private string? _stalenessCheckStatusMessage;

    /// <summary>Whether the "Check results" section above the list is open. A new result from any of
    /// the three whole-library checks opens it again, so a fresh result is never hidden in a section
    /// the user closed earlier.</summary>
    [ObservableProperty]
    private bool _isCheckResultsExpanded = true;

    /// <summary>True when any whole-library check (updates, game data, endorsements) has a result to show.</summary>
    public bool HasCheckResults =>
        UpdateCheckStatusMessage is not null || StalenessCheckStatusMessage is not null || EndorsementCheckStatusMessage is not null;

    /// <summary>The "Check results" section's one-line header summary: which checks have a result.</summary>
    public string CheckResultsSummary =>
        string.Join(" · ", new[]
        {
            UpdateCheckStatusMessage is null ? null : "Updates",
            StalenessCheckStatusMessage is null ? null : "Game data",
            EndorsementCheckStatusMessage is null ? null : "Endorsements",
        }.Where(part => part is not null));

    partial void OnUpdateCheckStatusMessageChanged(string? value) => OnCheckResultChanged(value);
    partial void OnStalenessCheckStatusMessageChanged(string? value) => OnCheckResultChanged(value);
    partial void OnEndorsementCheckStatusMessageChanged(string? value) => OnCheckResultChanged(value);

    private void OnCheckResultChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCheckResults));
        OnPropertyChanged(nameof(CheckResultsSummary));
        if (value is not null)
        {
            IsCheckResultsExpanded = true;
        }
    }

    /// <summary>Clears the "Check results" section (nothing else changes — the badges on the rows stay).</summary>
    [RelayCommand]
    private void ClearCheckResults()
    {
        UpdateCheckStatusMessage = null;
        StalenessCheckStatusMessage = null;
        EndorsementCheckStatusMessage = null;
    }

    /// <summary>The "Check" dropdown's own four whole-library actions (Check for updates/Check
    /// mods against game data/Check endorsements/Convert opaque mods) each run over every mod in
    /// the library and are mutually exclusive in practice — bound to each MenuItem's IsEnabled so a
    /// user can't fire a second one while one is already in flight, matching the re-entrancy guard
    /// each of the four async methods already checks on its own busy flag.</summary>
    public bool CanRunLibraryCheck =>
        !IsCheckingForUpdates && !IsCheckingStaleness && !IsConvertingAllOpaqueMods && !IsCheckingEndorsements;

    partial void OnIsCheckingForUpdatesChanged(bool value) => OnPropertyChanged(nameof(CanRunLibraryCheck));
    partial void OnIsCheckingStalenessChanged(bool value) => OnPropertyChanged(nameof(CanRunLibraryCheck));
    partial void OnIsConvertingAllOpaqueModsChanged(bool value) => OnPropertyChanged(nameof(CanRunLibraryCheck));
    partial void OnIsCheckingEndorsementsChanged(bool value) => OnPropertyChanged(nameof(CanRunLibraryCheck));

    /// <summary>Ctrl/Shift-click multi-select, for "Add to merge queue" — the only way mods reach Merge & Install's queue now that its own Library pane is gone. A plain list (not a HashSet): insertion order isn't meaningful here, but ObservableCollection gives free CollectionChanged notification for HasBulkSelection/BulkSelectedCount.</summary>
    public ObservableCollection<LibraryItemViewModel> BulkSelectedItems { get; } = [];

    /// <summary>UE4SS rows in the same click selection. Kept apart from BulkSelectedItems so pak-only actions (Add to merge queue, Delete, Convert) never see a Lua row.</summary>
    public ObservableCollection<Ue4ssModRowViewModel> BulkSelectedUe4ssRows { get; } = [];

    public bool HasBulkSelection => BulkSelectedCount > 0;

    /// <summary>Every selected row, pak and UE4SS.</summary>
    public int BulkSelectedCount => BulkSelectedItems.Count + BulkSelectedUe4ssRows.Count;

    /// <summary>Selected pak/EXMOD rows only — what "Add to merge queue" acts on.</summary>
    public int BulkSelectedPakCount => BulkSelectedItems.Count;

    /// <summary>Whether every mod currently shown is bulk-selected — drives the header row's own "select all" checkbox. False (not true) when the list is empty, matching Explorer's own convention of an unchecked header checkbox on an empty list.</summary>
    public bool IsAllSelectedForBulk
    {
        get
        {
            var rows = FlattenRows();
            return rows.Count > 0 && BulkSelectedCount == rows.Count;
        }
        set
        {
            if (value)
            {
                SelectAllForBulk("data");
            }
            else
            {
                ClearBulkSelection();
            }
        }
    }

    /// <summary>Null = the default order (pinned mods first, then alphabetical) — set by clicking a sortable column header; a second click on the same header flips SortDescending instead of picking a new column, matching Explorer's own Details-view convention.</summary>
    [ObservableProperty]
    private string? _sortColumn;

    [ObservableProperty]
    private bool _sortDescending;

    [RelayCommand]
    private void SortByColumn(string columnName)
    {
        if (SortColumn == columnName)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = columnName;
            SortDescending = false;
        }

        Reload();
    }

    public LibraryViewModel(
        ILibraryRepository repository, IUe4ssModRepository ue4ssModRepository, IUe4ssModStateService ue4ssModStateService,
        IUe4ssModMetaStore ue4ssModMetaStore, IUe4ssLoaderInstallService ue4ssLoaderInstallService,
        ISettingsService settingsService, IUnrealPakService unrealPakService, IUassetTextureDecoder uassetTextureDecoder,
        IUassetStaticMeshDecoder uassetStaticMeshDecoder, IUassetSkeletalMeshDecoder uassetSkeletalMeshDecoder,
        IUassetSoundDecoder uassetSoundDecoder, IUassetMaterialDecoder uassetMaterialDecoder,
        IOpaquePakAssetPreviewService opaquePakAssetPreviewService,
        INexusApiClient nexusApiClient,
        ICredentialStore credentialStore,
        Func<string, ExmodEditorViewModel> editorFactory, IActivityLog activityLog, HttpClient downloadHttpClient,
        IPendingDownloadStore pendingDownloadStore, IModVersionComparer modVersionComparer,
        DownloadsViewModel downloadsViewModel, Func<NexusCatalogViewModel> nexusCatalogViewModel,
        IActiveDownloadsTracker activeDownloadsTracker, Func<MergeInstallViewModel> mergeInstallViewModel, string backupDirectory,
        string dataFolder, IPrebuiltPakImporter prebuiltPakImporter, IPrebuiltPakToExmodConverter prebuiltPakToExmodConverter,
        IPrebuiltPakSourceStore prebuiltPakSourceStore, string thumbnailCacheDirectory, string pakPreviewCacheDirectory,
        IDialogService dialogService)
    {
        _dialogService = dialogService;
        _modVersionComparer = modVersionComparer;
        _prebuiltPakImporter = prebuiltPakImporter;
        _prebuiltPakToExmodConverter = prebuiltPakToExmodConverter;
        _prebuiltPakSourceStore = prebuiltPakSourceStore;
        _thumbnailCacheDirectory = thumbnailCacheDirectory;
        _pakPreviewCacheDirectory = pakPreviewCacheDirectory;
        _mergeInstallViewModel = mergeInstallViewModel;
        _activeDownloadsTracker = activeDownloadsTracker;
        Downloads = downloadsViewModel;
        _nexusCatalogViewModel = nexusCatalogViewModel;
        _downloadHttpClient = downloadHttpClient;
        _pendingDownloadStore = pendingDownloadStore;
        _repository = repository;
        _ue4ssModRepository = ue4ssModRepository;
        _ue4ssModStateService = ue4ssModStateService;
        _ue4ssModMetaStore = ue4ssModMetaStore;
        _ue4ssLoaderInstallService = ue4ssLoaderInstallService;
        _settingsService = settingsService;
        _showUe4ssBuiltIns = settingsService.Current.LibraryShowUe4ssBuiltIns;
        _unrealPakService = unrealPakService;
        _uassetTextureDecoder = uassetTextureDecoder;
        _uassetStaticMeshDecoder = uassetStaticMeshDecoder;
        _uassetSkeletalMeshDecoder = uassetSkeletalMeshDecoder;
        _uassetSoundDecoder = uassetSoundDecoder;
        _uassetMaterialDecoder = uassetMaterialDecoder;
        _opaquePakAssetPreviewService = opaquePakAssetPreviewService;
        _nexusApiClient = nexusApiClient;
        _credentialStore = credentialStore;
        _editorFactory = editorFactory;
        _activityLog = activityLog;
        _backupDirectory = backupDirectory;
        _dataFolder = dataFolder;

        // Reload() rebuilds every row's ViewModel and re-queries the search index; without
        // debouncing, every keystroke would pay that cost plus re-trigger the still-selected
        // item's EnsureDetailsLoaded (a fresh instance loses its "already loaded" state).
        _searchDebounceTimer = new DebounceTimer(TimeSpan.FromMilliseconds(250), () => Reload());

        // This VM is a DI singleton, constructed once and never re-scanned on its own — without
        // this, a mod imported from Downloads (a different page, sharing the same
        // ILibraryRepository) wouldn't show up here until the user happened to trigger some
        // unrelated reload (a search edit, or this page's own Refresh button).
        // Settings installed or removed UE4SS: re-read the loader and the rows, and say so here too.
        WeakReferenceMessenger.Default.Register<Ue4ssLoaderChangedMessage>(this, (recipient, message) =>
        {
            var self = (LibraryViewModel)recipient;
            self.ReloadInstalledUe4ssMods();
            self.Ue4ssStatusMessage = message.StatusMessage;
        });

        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) =>
        {
            var self = (LibraryViewModel)recipient;
            // A full resync can replace row instances outright — a stale bulk selection referencing
            // an orphaned instance would show a nonzero count with nothing actually highlighted.
            self.ClearBulkSelection();
            // A UE4SS mod downloaded through Downloads' own pipeline sends this same message (see
            // DownloadsViewModel.ClassifyAndImportExtractedModAsync) — without this, the UE4SS tab
            // stayed stale until Refresh was clicked by hand, even though Library's own Mods tab
            // already refreshed correctly. One Reload for both: this page's own actions send this
            // message instead of reloading themselves (a delete used to reload five times).
            self.ReloadUe4ssRows(fullResync: true);
        });

        // A download in progress shows as a stub row here (see Reload's own use of this) — starting
        // or finishing one needs Reload to re-run so the stub appears/disappears promptly, not just
        // whenever something else happens to trigger a reload.
        _activeDownloadsTracker.Current.CollectionChanged += (_, _) => Reload();

        // Without this, the UE4SS mods tab stays stuck on "Set the Icarus Content folder in
        // Settings first." (or an empty list) even right after the user actually sets and saves
        // it — this constructor's own ReloadInstalledUe4ssMods() call below only ever runs once,
        // at whatever moment Library happens to be constructed, which is often before Settings is
        // ever touched. Same fix MergeInstallViewModel's own RefreshHasExistingInstallAsync
        // already applies for the exact same "Content path changed after construction" staleness.
        WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (recipient, _) =>
        {
            var self = (LibraryViewModel)recipient;
            // The game path may have just been set or changed: the risk pill can read the game now
            // (the base-game mount tries again for a new path), so the files are rated again.
            self._fileRisksStale = true;
            self.ReloadInstalledUe4ssMods();
            self.OnPageShown(); // a no-op when the reload above already ran the pass
        });

        // Every successful "Update data folder" run means fresher base game data exists — worth
        // re-attempting conversion for mods that are still opaque, and re-deriving a fresher diff
        // for mods already converted from one, so neither stays frozen against stale data forever.
        // See RefreshConvertedOpaqueModsAsync's own doc comment for why this stays silent-but-
        // logged rather than either fully invisible or a manual action the user has to remember.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, message) =>
        {
            _ = ((LibraryViewModel)recipient).RefreshConvertedOpaqueModsAsync();
        });

        // Merge & Install checked queued mods: the same results on their badges and in Problems.
        WeakReferenceMessenger.Default.Register<ModHealthCheckedMessage>(this, (recipient, message) =>
        {
            var viewModel = (LibraryViewModel)recipient;
            foreach (var (folderName, report) in message.ReportsByFolder)
            {
                viewModel.FindItem(folderName)?.SetHealth(report);
            }

            viewModel.RefreshFilters();
        });

        // Fix… on Merge & Install's check: the same fix window as the row's badge.
        WeakReferenceMessenger.Default.Register<OpenModFixMessage>(this, (recipient, message) =>
            message.Reply(((LibraryViewModel)recipient).OpenModFixFromElsewhereAsync(message.FolderName)));

        // Closing for an app update mid-pass would leave a mod half rewritten.
        WeakReferenceMessenger.Default.Register<BackgroundWorkRequestMessage>(this, (recipient, message) =>
        {
            foreach (var work in ((LibraryViewModel)recipient).DescribeWorkInProgress())
            {
                message.Reply(work);
            }
        });

        BulkSelectedItems.CollectionChanged += (_, _) => RaiseBulkSelectionChanged();
        BulkSelectedUe4ssRows.CollectionChanged += (_, _) => RaiseBulkSelectionChanged();

        // Results from the Downloads pipeline (an import from this page) pop up like this page's own.
        Downloads.PropertyChanged += OnDownloadsPropertyChanged;
        FilterChips[0].IsSelected = true;

        ReloadInstalledUe4ssMods(); // ends with the first Reload

        // Constructors can't be async — same "fire it and let the method itself handle every
        // failure" shape DownloadsViewModel's own launch-time Nexus check already uses. Silent:
        // no "sign in first" nag if unconfigured, that only shows on an explicit button click.
        _ = CheckForUpdatesAsync(isAutomatic: true);
    }

    partial void OnSearchTextChanged(string value) => _searchDebounceTimer.Restart();

    partial void OnSelectedItemChanged(LibraryItemViewModel? value) => value?.EnsureDetailsLoaded();

    private void RaiseBulkSelectionChanged()
    {
        OnPropertyChanged(nameof(HasBulkSelection));
        OnPropertyChanged(nameof(BulkSelectedCount));
        OnPropertyChanged(nameof(BulkSelectedPakCount));
        OnPropertyChanged(nameof(IsAllSelectedForBulk));
        RaisePanelSelectionChanged();
    }

    /// <summary>Shared by Reload's own column-sort switch — SortDescending flips OrderBy/OrderByDescending, generic over TKey so both string columns (StringComparer.OrdinalIgnoreCase) and ImportedAtUtc's own DateTimeOffset share one implementation.</summary>
    private List<LibraryGroup> SortGroups<TKey>(
        IReadOnlyList<LibraryGroup> groups, Func<LibraryGroup, TKey> keySelector, IComparer<TKey>? comparer = null) =>
        [.. (SortDescending ? groups.OrderByDescending(keySelector, comparer) : groups.OrderBy(keySelector, comparer))];

    /// <summary>Sends the current bulk selection (or, with none active, just the passed-in row — a right-click with no prior Ctrl-click) to Merge & Install's queue.</summary>
    [RelayCommand]
    private void AddToMergeQueue(LibraryItemViewModel? item)
    {
        var folderNames = BulkSelectedItems.Count > 0
            ? BulkSelectedItems.Select(i => i.FolderName).ToList()
            : item is not null ? [item.FolderName] : [];

        if (folderNames.Count == 0)
        {
            return;
        }

        _mergeInstallViewModel().AddToQueueByFolderNames(folderNames);
        ClearBulkSelection();
        StatusMessage = folderNames.Count == 1
            ? $"Added '{folderNames[0]}' to the merge queue."
            : $"Added {folderNames.Count} mods to the merge queue.";
    }

    // ---- The Type filter and the UE4SS built-ins toggle (the "On" tick, Status and the pending
    // bars are in LibraryViewModel.InstallStatus.cs). ----

    /// <summary>"All" / "EXMOD" / "Pak" / "UE4SS": narrows the list to one kind. No control on the page sets it since the Library got its two panels (the Type dropdown went); clearing the filters resets it.</summary>
    [ObservableProperty]
    private string _typeFilter = "All";

    partial void OnTypeFilterChanged(string value) => Reload();

    /// <summary>
    /// UE4SS's own bundled mods (BPModLoaderMod, Keybinds, ConsoleEnablerMod…) — hidden unless asked
    /// for: they come with the loader, every install has them, and a real Library was a third
    /// built-ins. Remembered across restarts.
    /// </summary>
    [ObservableProperty]
    private bool _showUe4ssBuiltIns;

    partial void OnShowUe4ssBuiltInsChanged(bool value)
    {
        _settingsService.Current.LibraryShowUe4ssBuiltIns = value;
        _settingsService.Save();
        Reload();
    }

    /// <summary>How many UE4SS built-ins there are — the "(N)" on the Show UE4SS built-ins toggle.</summary>
    [ObservableProperty]
    private int _ue4ssBuiltInCount;

    /// <summary>"Show UE4SS built-ins (6)".</summary>
    public string ShowUe4ssBuiltInsLabel => $"Show UE4SS built-ins ({Ue4ssBuiltInCount})";

    partial void OnUe4ssBuiltInCountChanged(int value) => OnPropertyChanged(nameof(ShowUe4ssBuiltInsLabel));

    private bool MatchesTypeFilter(LibraryGroup group) => TypeFilter switch
    {
        "EXMOD" => group.Entries.Any(e => !e.IsOpaquePak),
        "Pak" => group.Entries.Any(e => e.IsOpaquePak),
        "UE4SS" => false,
        _ => true,
    };

    private bool MatchesSearch(Ue4ssModRowViewModel row) =>
        string.IsNullOrWhiteSpace(SearchText)
        || row.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)
        || (row.NexusName?.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    [RelayCommand]
    private void Refresh()
    {
        try
        {
            _repository.Refresh();
        }
        catch (Exception ex)
        {
            // Same UI boundary as import/delete — Refresh() re-walks Extracted_Mods from disk,
            // which can hit the same locked/permission-denied conditions RescanAll's own per-mod
            // catch already tolerates, except this one is the top-level directory enumeration
            // itself, which isn't wrapped per-folder.
            StatusMessage = $"Refresh failed: {ex.Message}";
            return;
        }

        StatusMessage = "Library refreshed.";
        // Both panels: the UE4SS mods are read from the game folder and staging again too (user
        // report 2026-10-28: Refresh only re-read Extracted_Mods, so a mod put in the game's Mods
        // folder by hand, or a server copy, never showed until the page was left and re-opened).
        ReloadUe4ssRows(fullResync: true);
    }
}
