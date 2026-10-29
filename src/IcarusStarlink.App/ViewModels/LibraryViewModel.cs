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

    public IReadOnlyList<string> TypeFilterOptions { get; } = ["All", "EXMOD", "Pak", "UE4SS"];

    /// <summary>"All" / "EXMOD" / "Pak" / "UE4SS" — the Type dropdown next to the search box.</summary>
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

    /// <summary>Snapshots the mod's whole folder — a safety net before a risky manual edit, independent of the EXMOD editor's own transient per-field preview.</summary>
    [RelayCommand]
    private void BackupMod(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            _repository.BackupMod(item.FolderName);
            item.NotifyBackupStateChanged();
            StatusMessage = $"Backed up '{item.Name}'.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
            _activityLog.Log($"Backing up '{item.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>Says why and returns true when the mod can't be changed on disk right now (ModChangeBlockedReason).</summary>
    private bool RefuseWhileBusy(LibraryItemViewModel item)
    {
        if (ModChangeBlockedReason(item) is not { } blocked)
        {
            return false;
        }

        StatusMessage = blocked;
        return true;
    }

    /// <summary>
    /// Replaces the mod's current folder with its own most recent backup — a real point-in-time
    /// restore, so this is gated the same way every other hard-to-reverse action in this app is.
    /// </summary>
    [RelayCommand]
    private void RestoreModBackup(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (RefuseWhileBusy(item))
        {
            return;
        }

        var confirmResult = _dialogService.Confirm(
            $"This replaces the current content of '{item.Name}' with its most recent backup — any edit made since then is lost.\n\nContinue?",
            "Restore backup", ThemedConfirmSeverity.Question);
        if (!(confirmResult) || RefuseWhileBusy(item))
        {
            return;
        }

        try
        {
            // Through the fix service: it takes the same lock as every other writer of the mod.
            var restored = FixService.RestoreBackup(item.FolderName);
            StatusMessage = restored ? $"Restored '{item.Name}' from its latest backup." : $"No backup exists yet for '{item.Name}'.";
            if (restored)
            {
                _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            _activityLog.Log($"Restoring '{item.Name}' from its backup failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Puts the mod back exactly as it was downloaded: the same Reinstall the Downloads page offers
    /// (delete the Library copy, import the kept download again), so fixes, edits and renames made
    /// since are gone, and its remembered fix choices are forgotten so the next check starts fresh.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ReinstallOriginal(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (RefuseWhileBusy(item))
        {
            return;
        }

        if (Downloads.FindOriginalDownload(item.FolderName) is not { } download)
        {
            StatusMessage = $"ISL doesn't have the original download for '{item.Name}' (it was imported from a file, or the download was discarded). Import that file again, or use Restore latest backup.";
            return;
        }

        if (!_dialogService.Confirm(
                $"Reinstall '{item.Name}' from its original download ({download.FileName})?\n\nThis replaces the Library copy: fixes, edits and a rename made since are lost, and its remembered fix choices are forgotten. It's backed up first, so Restore latest backup can bring this copy back.",
                "Reinstall original", ThemedConfirmSeverity.Warning)
            || RefuseWhileBusy(item))
        {
            return;
        }

        item.FlushPendingSave();

        var name = item.Name;
        try
        {
            // Pristine: backed up first, nothing carried over but pin/notes, and the remembered fix
            // choices forgotten — only once it worked (a failed reinstall puts the old copy back and
            // keeps them).
            var result = await Downloads.ActivateAsync(download, pristine: true);
            if (result.Succeeded)
            {
                StatusMessage = Downloads.PendingDownloadStatusMessage is { Length: > 0 } detail ? $"Reinstalled '{name}': {detail}" : $"Reinstalled '{name}'.";
                _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
            }
            else
            {
                StatusMessage = result.Cancelled ? result.Message : $"Reinstall of '{name}' failed: {result.Message}";
                _activityLog.Log(StatusMessage ?? $"Reinstall of '{name}' didn't finish.", result.Cancelled ? ActivityEntryKind.Info : ActivityEntryKind.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reinstall failed: {ex.Message}";
            _activityLog.Log($"Reinstall of '{name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>Opens a small dialog to override how a mod's own name displays in Library — never touches its real folder, FileName, or file content. Reset clears the override; Cancel does nothing.</summary>
    [RelayCommand]
    private void RenameItem(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var result = _dialogService.PromptRename(item.Name);
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            _repository.SetDisplayNameOverride(item.FolderName, result.NewDisplayName);
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't rename: {ex.Message}";
        }
    }

    /// <summary>An inline per-row Edit action (row hover icons) — takes the row's own item directly rather than requiring it to be selected first.</summary>
    [RelayCommand]
    private void EditItem(LibraryItemViewModel? item)
    {
        if (item is null || item.IsOpaquePak)
        {
            return;
        }

        try
        {
            OpenEditor(item.FolderName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open the mod editor: {ex.Message}";
        }
    }

    /// <summary>
    /// A new, non-modal Window + a fresh, transient ExmodEditorViewModel per call — classic IMM's
    /// own real editor supports two independent sessions open at once, which .Show() (not
    /// ShowDialog) matches; this app's other ViewModels stay DI singletons, but the editor
    /// deliberately isn't one.
    /// </summary>
    /// <param name="preSelect">
    /// When given and a matching row exists in the freshly-opened editor's own Items list, selects
    /// it instead of the editor's own default (its first item) — used by the fix window's Open in mod editor so the
    /// editor's existing amber base-diff highlighting is what the user sees first, not an unrelated
    /// row.
    /// </param>
    /// <param name="startWizard">An item template's preset: the new-item wizard opens over the editor straight away.</param>
    private void OpenEditor(string folderName, (string CurrentFile, string ItemName)? preSelect = null, NewItemPreset? startWizard = null)
    {
        var editorViewModel = _editorFactory(folderName);
        if (preSelect is { } target)
        {
            var match = editorViewModel.Items.FirstOrDefault(i => i.CurrentFile == target.CurrentFile && i.ItemName == target.ItemName);
            if (match is not null)
            {
                editorViewModel.SelectedItem = match;
            }
        }

        var window = new ExmodEditorWindow(editorViewModel) { Owner = Application.Current.MainWindow };
        // Locked: Fix sure problems reads it live from its background pass (IsEditorOpenLive).
        lock (_openEditorCountByFolder)
        {
            _openEditorCountByFolder[folderName] = _openEditorCountByFolder.GetValueOrDefault(folderName) + 1;
        }

        window.Closed += (_, _) =>
        {
            lock (_openEditorCountByFolder)
            {
                var remaining = _openEditorCountByFolder[folderName] - 1;
                if (remaining <= 0)
                {
                    _openEditorCountByFolder.Remove(folderName);
                }
                else
                {
                    _openEditorCountByFolder[folderName] = remaining;
                }
            }
        };
        window.Show();
        if (startWizard is not null)
        {
            // After the editor has drawn, so the wizard opens over it.
            window.Dispatcher.BeginInvoke(() => editorViewModel.StartNewItemWizard(startWizard), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Test-only seam — see AssemblyInfo.cs's own InternalsVisibleTo grant. OpenEditor itself constructs a real ExmodEditorWindow, which (like every other real Window in this app) can't run in a unit test host — this lets a test mark a folder as "has an open editor" the same way OpenEditor's own bookkeeping does, without needing a real window.</summary>
    internal void MarkFolderEditorOpenForTesting(string folderName)
    {
        lock (_openEditorCountByFolder)
        {
            _openEditorCountByFolder[folderName] = _openEditorCountByFolder.GetValueOrDefault(folderName) + 1;
        }
    }

    /// <summary>Whether the mod is open in an editor right now; safe from a background thread (the count is only changed under the same lock).</summary>
    private bool IsEditorOpenLive(string folderName)
    {
        lock (_openEditorCountByFolder)
        {
            return _openEditorCountByFolder.ContainsKey(folderName);
        }
    }

    /// <summary>
    /// The context-menu's own Delete — operates on the WHOLE bulk selection, not just the row that
    /// happened to be right-clicked: code-behind's EnsureBulkSelected already folded the
    /// right-clicked row into BulkSelectedItems (either as the sole entry, or added to whatever
    /// Ctrl/Shift-selection already existed) before this menu ever opened, so BulkSelectedItems is
    /// always the right source of truth here — falling back to the passed-in item alone only
    /// covers a call site that isn't the context menu at all.
    /// </summary>
    [RelayCommand]
    private void DeleteItem(LibraryItemViewModel? item)
    {
        var items = BulkSelectedItems.Count > 0 ? BulkSelectedItems.ToList() : item is not null ? [item] : [];
        DeleteMods(items);
    }

    /// <summary>
    /// Deletes exactly the given mod, ignoring the list's click selection — for the mod detail
    /// window, which can show a different mod (Prev/Next) than the one selected in the list. It
    /// used DeleteItem, which deletes the list's selection whenever there is one.
    /// </summary>
    [RelayCommand]
    private void DeleteOneItem(LibraryItemViewModel? item) => DeleteMods(item is null ? [] : [item]);

    /// <summary>Deletes every mod in the Library, regardless of any current selection — its own menu item since this is a much larger blast radius than the ordinary per-selection Delete above; DeleteMods' own count-naming confirmation still gates it.</summary>
    [RelayCommand]
    private void DeleteAll() => DeleteMods([.. AllLibraryItems()]);

    private LibraryModRemover? _modRemover;

    private LibraryModRemover ModRemover => _modRemover ??= new LibraryModRemover(_repository, _prebuiltPakSourceStore, _pakPreviewCacheDirectory, FixService);

    private void DeleteMods(IReadOnlyList<LibraryItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        // A package (one Nexus download that landed as a Lua mod plus asset paks) is deleted whole:
        // the selection is widened to every sibling part, and the UE4SS parts come out too.
        // Half a WirelessPower is a broken WirelessPower, and the user has no way to know which
        // three rows belong together otherwise.
        var selectedFolders = new HashSet<string>(items.Select(i => i.FolderName), StringComparer.OrdinalIgnoreCase);
        var packages = items
            .Select(PackageOf)
            .OfType<ModPackage>()
            .Distinct()
            .ToList();
        var siblingItems = AllLibraryItems()
            .Where(i => !selectedFolders.Contains(i.FolderName) && packages.Any(p => p.LibraryFolderNames.Contains(i.FolderName, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var ue4ssParts = packages.SelectMany(p => p.Ue4ssFolderNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (siblingItems.Count > 0)
        {
            items = [.. items, .. siblingItems];
        }

        // Not while a check or fix pass rewrites mods, one of them is being converted again, or its
        // editor is open (the fix window's guards): asked to delete, it would race the pass, and the
        // editor's next Save would bring the deleted mod back half-made.
        if (items.Select(ModChangeBlockedReason).FirstOrDefault(r => r is not null) is { } blocked)
        {
            StatusMessage = blocked;
            return;
        }

        // Deleting removes each mod's real folder from disk and can't be undone from here — and a
        // right-click menu is far easier to hit by accident than a dedicated button, so this always
        // asks first, matching how every other irreversible action in this app behaves. Names the
        // real count rather than a generic "these mods" so a fat-fingered bulk selection is obvious
        // before it's too late to back out.
        string message;
        if (packages.Count == 1 && items.Count + ue4ssParts.Count == packages[0].PartCount)
        {
            var package = packages[0];
            var partLines = items.Select(i => $"  • {i.Name} (Library)")
                .Concat(ue4ssParts.Select(n => $"  • {n} (UE4SS mod)"));
            message = $"Delete '{package.DisplayName}' and all {package.PartCount} of its parts?\n\n{string.Join("\n", partLines)}\n\nThe folders are removed from disk; an enabled UE4SS part is taken out of the game's Mods folder (backed up first). Run Merge & Install afterwards to drop the paks from the game. Each Library part is backed up first (Backups\\Mods).";
        }
        else
        {
            message = items.Count == 1
                ? $"Delete '{items[0].Name}' from your Library?\n\nIts folder is removed from disk (a backup is kept under Backups\\Mods). Run Merge & Install afterwards to drop it from the game."
                : $"Delete {items.Count} mods from your Library?\n\nTheir folders are removed from disk (each is backed up first, under Backups\\Mods). Run Merge & Install afterwards to drop them from the game.";
            if (ue4ssParts.Count > 0)
            {
                message += $"\n\nThese came with UE4SS mods from the same download, which are removed too: {string.Join(", ", ue4ssParts)}.";
            }
        }

        var confirm = _dialogService.Confirm(message, "Delete mod(s)", ThemedConfirmSeverity.Warning);
        if (!(confirm))
        {
            return;
        }

        var deletedCount = 0;
        var failures = new List<string>();
        foreach (var ue4ssPart in ue4ssParts)
        {
            var failure = DeleteUe4ssModCore(ue4ssPart);
            if (failure is null)
            {
                _activityLog.Log($"Deleted UE4SS mod '{ue4ssPart}' (part of the same download).", ActivityEntryKind.Info);
            }
            else
            {
                failures.Add($"{ue4ssPart}: {failure}");
            }
        }

        foreach (var item in items)
        {
            var name = item.Name;
            // Saved now, not dropped: the backup below keeps the notes typed a moment ago, and a
            // timer left pending can't fire after the delete and write them into a different mod
            // that reuses the freed folder name.
            item.FlushPendingSave();
            try
            {
                // Backed up first, so a delete (an easy right-click to hit by accident) isn't
                // permanent — a mod that can't be backed up isn't deleted. Its remembered fix
                // choices, saved source pak and preview cache go with it (LibraryModRemover).
                ModRemover.Delete(item.FolderName);
                deletedCount++;
                if (SelectedItem == item)
                {
                    SelectedItem = null;
                }

                _activityLog.Log($"Deleted '{name}'.", ActivityEntryKind.Info);
            }
            catch (Exception ex)
            {
                // Same UI boundary as ImportPaths: deleting a folder that's locked by another
                // process (Explorer preview, an antivirus scan, ...) throws IOException/
                // UnauthorizedAccessException — one locked mod shouldn't abort the rest of a batch.
                failures.Add($"{name}: {ex.Message}");
            }
        }

        ClearBulkSelection();
        var nextStep = deletedCount > 0 ? " Run Merge & Install to remove it from the game." : "";
        StatusMessage = failures.Count switch
        {
            0 when deletedCount == 1 && ue4ssParts.Count == 0 => $"Deleted '{items[0].Name}'.{nextStep}",
            0 when packages.Count == 1 && ue4ssParts.Count > 0 => $"Deleted '{packages[0].DisplayName}' ({deletedCount + ue4ssParts.Count} parts).{nextStep}",
            0 => $"Deleted {deletedCount} mod(s).{nextStep}",
            _ => $"Deleted {deletedCount} of {items.Count} — {string.Join("; ", failures)}",
        };
        // Its own handler reloads both panels (the UE4SS parts too): no Reload of its own here.
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
    }

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
            foreach (var item in items)
            {
            }

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
            ? Ue4ssMods.Where(row => !row.IsLinkedToLibraryMod && (ShowUe4ssBuiltIns || !row.IsBuiltIn) && MatchesSearch(row)).Select(row => (SortName: row.Name, Pinned: false, Row: (object)row)).ToList()
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

        Ue4ssBuiltInCount = Ue4ssMods.Count(row => row.IsBuiltIn && !row.IsLinkedToLibraryMod);
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
