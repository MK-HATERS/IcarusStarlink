using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;
using Microsoft.Win32;

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

    /// <summary>The IMM Database tab now lives directly on this page (folded in from the former standalone Downloads page), so DownloadsViewModel is needed immediately on Library's own first render — no more lazy resolution for this one. Nexus stays its own separate page and stays lazy.</summary>
    public DownloadsViewModel Downloads { get; }

    private readonly Func<NexusCatalogViewModel> _nexusCatalogViewModel;

    /// <summary>0 = Mods, 1 = UE4SS mods, 2 = IMM Database — set programmatically by "Find in Database" (a Library row's own context-menu action) to jump straight to the right tab.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    private readonly string _backupDirectory;

    /// <summary>The extracted game data folder ("Update data folder"'s own output) — needed by CheckModsAgainstCurrentDataAsync to diff each mod's own items against what the game currently defines. Same plain DI-injected string ExmodEditorViewModel/MergeInstallViewModel already take, not a Settings-read.</summary>
    private readonly string _dataFolder;

    private readonly DebounceTimer _searchDebounceTimer;
    private readonly Dictionary<string, LibraryItemViewModel> _itemsByFolderName = [];
    private readonly Dictionary<string, LibraryGroupViewModel> _groupsByKey = [];

    /// <summary>
    /// Open-editor-window count per folder name — CheckModsAgainstCurrentDataAsync's own auto-fix
    /// pass consults this (a snapshot taken before its background Task.Run, see there) to skip
    /// writing to any folder with a count above zero. Without this, its background backup+rewrite
    /// could land while an editor window for that same mod is open: the editor's own _package was
    /// loaded from disk before the auto-fix ran and has no idea it happened, so the very next click
    /// of the editor's own Save button would write that stale in-memory copy straight back over the
    /// just-applied auto-fix — silently discarding it with no error, and leaving the repository
    /// record inconsistent with what SetStaleItems/NotifyRepairedFromDisk just told the UI moments
    /// earlier. Skipping the write entirely (rather than trying to coordinate two writers with a
    /// lock, which wouldn't stop the editor's own later Save from still clobbering it) is the
    /// simplest fix that actually closes the race — the next staleness check run picks up that mod
    /// once every editor window for it has closed. A count, not a HashSet, because nothing stops the
    /// same folder being opened in more than one editor window at once.
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
                SelectAllForBulk();
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
            self.Reload(fullResync: true);
            // A UE4SS mod downloaded through Downloads' own pipeline sends this same message (see
            // DownloadsViewModel.ClassifyAndImportExtractedModAsync) — without this, the UE4SS tab
            // stayed stale until Refresh was clicked by hand, even though Library's own Mods tab
            // already refreshed correctly.
            self.ReloadInstalledUe4ssMods();
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
            ((LibraryViewModel)recipient).ReloadInstalledUe4ssMods();
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

        BulkSelectedItems.CollectionChanged += (_, _) => RaiseBulkSelectionChanged();
        BulkSelectedUe4ssRows.CollectionChanged += (_, _) => RaiseBulkSelectionChanged();

        Reload();
        ReloadInstalledUe4ssMods();

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
    }

    private static bool IsRowSelected(object row) => row switch
    {
        LibraryItemViewModel item => item.IsSelectedForBulk,
        Ue4ssModRowViewModel lua => lua.IsSelectedForBulk,
        _ => false,
    };

    private void SetRowSelected(object row, bool selected)
    {
        switch (row)
        {
            case LibraryItemViewModel item when item.IsSelectedForBulk != selected:
                item.IsSelectedForBulk = selected;
                if (selected)
                {
                    BulkSelectedItems.Add(item);
                }
                else
                {
                    BulkSelectedItems.Remove(item);
                }

                break;
            case Ue4ssModRowViewModel lua when lua.IsSelectedForBulk != selected:
                lua.IsSelectedForBulk = selected;
                if (selected)
                {
                    BulkSelectedUe4ssRows.Add(lua);
                }
                else
                {
                    BulkSelectedUe4ssRows.Remove(lua);
                }

                break;
        }
    }

    /// <summary>Ctrl-click: adds or removes this one row (pak or UE4SS) and makes it the Shift-click anchor.</summary>
    public void ToggleBulkSelection(object row)
    {
        SetRowSelected(row, !IsRowSelected(row));
        _bulkSelectionAnchor = row;
    }

    public void ClearBulkSelection()
    {
        foreach (var item in BulkSelectedItems)
        {
            item.IsSelectedForBulk = false;
        }

        foreach (var lua in BulkSelectedUe4ssRows)
        {
            lua.IsSelectedForBulk = false;
        }

        BulkSelectedItems.Clear();
        BulkSelectedUe4ssRows.Clear();
    }

    /// <summary>Plain click (no modifier): selects just this row (it turns grey) and makes it the anchor for a later Shift-click, like Explorer and Mod Organizer.</summary>
    public void SetBulkSelectionAnchor(object row)
    {
        ClearBulkSelection();
        SetRowSelected(row, true);
        _bulkSelectionAnchor = row;
    }

    /// <summary>Right-click: a row that's already part of a multi-selection stays part of it (so right-clicking any one of several Ctrl-selected rows adds all of them); right-clicking a row outside the current selection replaces it with just that row — the same convention Explorer uses.</summary>
    public void EnsureBulkSelected(object row)
    {
        if (!IsRowSelected(row))
        {
            ClearBulkSelection();
            SetRowSelected(row, true);
        }

        _bulkSelectionAnchor = row;
    }

    /// <summary>
    /// Ticking or unticking ON on a row that is part of a multi-selection applies the same state
    /// to every selected row: paks/EXMODs join or leave the merge queue, UE4SS mods are ticked for
    /// the next Apply. Returns how many other rows changed.
    /// </summary>
    public int ApplyEnabledToSelection(object clickedRow, bool enabled)
    {
        if (!IsRowSelected(clickedRow) || BulkSelectedCount < 2)
        {
            return 0;
        }

        var changed = 0;
        foreach (var item in BulkSelectedItems.ToList().Where(i => !ReferenceEquals(i, clickedRow) && i.IsEnabled != enabled))
        {
            item.IsEnabled = enabled;
            changed++;
        }

        foreach (var lua in BulkSelectedUe4ssRows.ToList().Where(r => !ReferenceEquals(r, clickedRow) && r.IsEnabled != enabled))
        {
            lua.IsEnabled = enabled;
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// RootItems in visible order, with any variant family's own children expanded inline — the
    /// same flattened order Shift-click range-select and Select all both need. Deliberately doesn't
    /// consult the TreeView's own live expand/collapse state (fragile to reach for from a
    /// ViewModel, and TreeView-specific) — a range or "select all" acts on every real mod
    /// regardless of whether its family group happens to be collapsed right now, matching how this
    /// app's own bulk actions (Add to merge queue, Delete) already treat a family as a transparent
    /// container rather than something that can hide its members from a bulk operation.
    /// </summary>
    private List<object> FlattenRows() =>
        [.. RootItems.SelectMany(entry => entry switch
        {
            LibraryItemViewModel or Ue4ssModRowViewModel => (IEnumerable<object>)[entry],
            LibraryGroupViewModel group => group.Items,
            _ => [],
        })];

    private List<LibraryItemViewModel> FlattenModItems() =>
        [.. RootItems.SelectMany(entry => entry switch
        {
            LibraryItemViewModel item => (IEnumerable<LibraryItemViewModel>)[item],
            LibraryGroupViewModel group => group.Items,
            _ => [],
        })];

    /// <summary>Used by ModDetailWindow's own Prev/Next navigation — the same flattened visible
    /// order FlattenModItems already produces for Shift-click/Select all, so "next" matches
    /// whatever's actually currently shown (respecting the active search/sort), not some other
    /// ordering. Null at either end of the list rather than wrapping around.</summary>
    public LibraryItemViewModel? GetAdjacentItem(LibraryItemViewModel current, int direction)
    {
        var flattened = FlattenModItems();
        var index = flattened.IndexOf(current);
        if (index < 0)
        {
            return null;
        }

        var targetIndex = index + direction;
        return targetIndex >= 0 && targetIndex < flattened.Count ? flattened[targetIndex] : null;
    }

    /// <summary>Shared by Reload's own column-sort switch — SortDescending flips OrderBy/OrderByDescending, generic over TKey so both string columns (StringComparer.OrdinalIgnoreCase) and ImportedAtUtc's own DateTimeOffset share one implementation.</summary>
    private List<LibraryGroup> SortGroups<TKey>(
        IReadOnlyList<LibraryGroup> groups, Func<LibraryGroup, TKey> keySelector, IComparer<TKey>? comparer = null) =>
        [.. (SortDescending ? groups.OrderByDescending(keySelector, comparer) : groups.OrderBy(keySelector, comparer))];

    /// <summary>Shift-click: selects every mod between the last plain/Ctrl-clicked anchor and this one (inclusive), replacing whatever was selected before — matching Explorer's own Shift-click convention. Falls back to selecting just this row alone if there's no anchor yet (e.g. the very first click in a fresh session was a Shift-click).</summary>
    public void SelectBulkRange(object target)
    {
        var flattened = FlattenRows();
        var anchorIndex = _bulkSelectionAnchor is not null ? flattened.IndexOf(_bulkSelectionAnchor) : -1;
        var targetIndex = flattened.IndexOf(target);
        if (targetIndex < 0)
        {
            return;
        }

        if (anchorIndex < 0)
        {
            anchorIndex = targetIndex;
        }

        ClearBulkSelection();
        var (start, end) = anchorIndex <= targetIndex ? (anchorIndex, targetIndex) : (targetIndex, anchorIndex);
        for (var i = start; i <= end; i++)
        {
            SetRowSelected(flattened[i], true);
        }

        // Deliberately NOT updated to target — Explorer's own Shift-click keeps ranging from the
        // original anchor on a repeated Shift-click, so shrinking/growing a range with successive
        // Shift-clicks stays possible instead of the anchor chasing the most recent click.
    }

    /// <summary>Context menu's "Select all" — every mod currently shown (respects the active search filter, since RootItems already reflects it), not the whole unfiltered Library.</summary>
    [RelayCommand]
    private void SelectAllForBulk()
    {
        ClearBulkSelection();
        foreach (var row in FlattenRows())
        {
            SetRowSelected(row, true);
        }

        _bulkSelectionAnchor = null;
    }

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

    [RelayCommand]
    private void ReloadInstalledUe4ssMods()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            Ue4ssMods.Clear();
            HasPendingUe4ssChanges = false;
            IsUe4ssLoaderInstalled = true; // nothing to check against yet, so nothing to warn about
            Ue4ssStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath);
            var states = _ue4ssModStateService.GetAll(modsFolder);

            // Read once per reload, not once per mod below — it's the same installed loader
            // regardless of which mod row is asking, same as SettingsViewModel.RefreshUe4ssStatus's
            // own single call.
            var loaderStatus = _ue4ssLoaderInstallService.GetStatus(_settingsService.Current.IcarusContentPath!);
            var installedLoaderVersion = loaderStatus.InstalledVersion;
            IsUe4ssLoaderInstalled = loaderStatus.IsInstalled;

            // This reload also runs on every LibraryChangedMessage (an unrelated import/delete/link
            // elsewhere in the app, not just this page's own Refresh/Apply) — rebuilding every row
            // from scratch would otherwise silently drop an unsaved pending enable/disable toggle
            // and any already-fetched "Update available" badge each time, neither of which is
            // persisted anywhere else. Snapshot by name first, then reapply onto the fresh rows.
            var previousByName = Ue4ssMods.ToDictionary(m => m.Name);

            // The old row objects are about to go: carry their selection over by name.
            var selectedNames = BulkSelectedUe4ssRows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var anchorName = (_bulkSelectionAnchor as Ue4ssModRowViewModel)?.Name;
            BulkSelectedUe4ssRows.Clear();

            Ue4ssMods.Clear();
            foreach (var state in states)
            {
                var meta = _ue4ssModMetaStore.Load(state.Name);
                // IsFrameworkOwned, not membership in ListUserAddedMods — that list only enumerates
                // what's currently IN the game's real Mods folder, so a disabled/staged mod (built-in
                // or not) would silently misclassify as "user-added" by simple absence from it.
                var isBuiltIn = _ue4ssLoaderInstallService.IsFrameworkOwned(_settingsService.Current.IcarusContentPath!, state.Name);
                var row = new Ue4ssModRowViewModel(
                    state.Name, state.IsEnabled, isBuiltIn, meta.NexusModId, meta.NexusVersion,
                    meta.MinUe4ssVersion, installedLoaderVersion, RecomputeHasPendingUe4ssChanges, meta.NexusName);
                if (previousByName.TryGetValue(state.Name, out var previous))
                {
                    if (previous.IsDirty)
                    {
                        row.IsEnabled = previous.IsEnabled;
                    }

                    row.LatestVersion = previous.LatestVersion;
                    row.LatestNexusFileId = previous.LatestNexusFileId;
                }

                if (selectedNames.Contains(row.Name))
                {
                    SetRowSelected(row, true);
                }

                if (anchorName is not null && string.Equals(anchorName, row.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _bulkSelectionAnchor = row;
                }

                row.LoaderMissing = !IsUe4ssLoaderInstalled;
                row.NexusFileId = meta.NexusFileId;
                Ue4ssMods.Add(row);
            }
            RecomputeHasPendingUe4ssChanges();

            // A sidecar whose mod folder is gone (deleted by hand, renamed) is dead weight that can
            // attach a stale Nexus link to an unrelated mod landing under the same name later —
            // found live as two orphaned "ArcFissionLibrary … _2.json" files.
            var liveNames = new HashSet<string>(states.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _ue4ssModMetaStore.ListFolderNames().Where(n => !liveNames.Contains(n)).ToList())
            {
                _ue4ssModMetaStore.Delete(stale);
            }

            _packagesByNexusModId = BuildPackages();
            Ue4ssStatusMessage = states.Count == 0 ? "No UE4SS mods found." : null;

            // The UE4SS rows live in the same list as everything else — rebuild it with the fresh rows.
            Reload();
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't read the game's UE4SS Mods folder: {ex.Message}";
        }
    }

    private void RecomputeHasPendingUe4ssChanges()
    {
        HasPendingUe4ssChanges = Ue4ssMods.Any(m => m.IsDirty);
        RefreshEnabledAndStatus();
    }

    // ---- The single Mods list: Type filter, the "On" checkbox's link to the merge queue, Status,
    // and the pending-changes bar. ----

    public IReadOnlyList<string> TypeFilterOptions { get; } = ["All", "EXMOD", "Pak", "UE4SS"];

    /// <summary>"All" / "EXMOD" / "Pak" / "UE4SS" — the Type dropdown next to the search box.</summary>
    [ObservableProperty]
    private string _typeFilter = "All";

    partial void OnTypeFilterChanged(string value) => Reload();

    /// <summary>How many UE4SS rows the list is showing (stand-alone Lua mods; a Lua half folded into a Mods-tab row doesn't count).</summary>
    [ObservableProperty]
    private int _ue4ssCount;

    /// <summary>True when an "On" tick differs from what the installed merged pak contains (Merge &amp; Install needed) or a UE4SS tick differs from disk (Apply needed).</summary>
    [ObservableProperty]
    private bool _hasPendingChanges;

    /// <summary>"2 changes not in the game yet: 1 UE4SS change and 1 pak mod".</summary>
    [ObservableProperty]
    private string _pendingChangesSummary = "";

    [ObservableProperty]
    private bool _hasPendingPakChanges;

    private bool _mergeQueueSubscribed;

    private MergeInstallViewModel? TryGetMergeInstall()
    {
        try
        {
            return _mergeInstallViewModel();
        }
        catch (Exception)
        {
            // A test harness without a Merge & Install page: the "On" checkbox just has no queue to talk to.
            return null;
        }
    }

    private void EnsureMergeQueueSubscription()
    {
        if (_mergeQueueSubscribed)
        {
            return;
        }

        var mergeInstall = TryGetMergeInstall();
        if (mergeInstall is null)
        {
            return;
        }

        _mergeQueueSubscribed = true;
        mergeInstall.Queue.CollectionChanged += (_, _) => RefreshEnabledAndStatus();
        mergeInstall.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MergeInstallViewModel.LastInstalledModNames))
            {
                RefreshEnabledAndStatus();
            }
        };
        _ = mergeInstall.RefreshInstalledModNamesAsync();
    }

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

    /// <summary>The user ticked/unticked a pak or EXMOD row: that IS adding it to / removing it from the merge queue; a mod's Lua half follows the same tick.</summary>
    private void OnItemEnabledToggled(object? sender, bool enabled)
    {
        if (sender is not LibraryItemViewModel item)
        {
            return;
        }

        var mergeInstall = TryGetMergeInstall();
        if (mergeInstall is null)
        {
            return;
        }

        if (enabled)
        {
            mergeInstall.AddToQueueByFolderNames([item.FolderName]);
            if (!mergeInstall.IsQueued(item.FolderName))
            {
                // Refused (already covered by a queued merged pack, for instance) — the queue says why.
                item.SetEnabledFromQueue(false);
                StatusMessage = mergeInstall.StatusMessage;
            }
        }
        else
        {
            mergeInstall.RemoveFromQueueByFolderNames([item.FolderName]);
        }

        foreach (var luaRow in Ue4ssMods.Where(r => item.LinkedUe4ssNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
        {
            luaRow.IsEnabled = enabled;
        }

        RefreshEnabledAndStatus();
    }

    /// <summary>
    /// Recomputes every row's "On" tick (from the merge queue) and Status (from the installed
    /// merged pak's manifest plus the Lua half's own state), and the pending-changes bar.
    /// </summary>
    private void RefreshEnabledAndStatus()
    {
        var mergeInstall = TryGetMergeInstall();
        var installed = new HashSet<string>(mergeInstall?.LastInstalledModNames ?? [], StringComparer.OrdinalIgnoreCase);
        var pendingPak = 0;
        var inGameNotTicked = 0;
        foreach (var item in _itemsByFolderName.Values)
        {
            var queued = mergeInstall?.IsQueued(item.FolderName) ?? false;
            item.SetEnabledFromQueue(queued);

            // The manifest lists an EXMOD by name and an opaque entry by each pak's file name.
            var inGame = item.IsOpaquePak && item.PakFileNames.Count > 0
                ? item.PakFileNames.All(p => installed.Contains(Path.GetFileNameWithoutExtension(p)))
                : installed.Contains(item.Name) || installed.Contains(item.FolderName);

            var luaRows = Ue4ssMods.Where(r => item.LinkedUe4ssNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            var luaDirty = luaRows.Any(r => r.IsDirty);
            var luaInGame = luaRows.Count > 0 && luaRows.All(r => r.RealIsEnabled);

            string status;
            var detail = "";
            if (luaDirty)
            {
                status = "Needs Apply";
                detail = queued && !inGame ? "then Merge & Install for the paks" : "";
            }
            else if (queued && !inGame)
            {
                status = "Needs Merge & Install";
                detail = luaRows.Count > 0 ? (luaInGame ? "scripts in game · paks not yet" : "paks not in the game yet") : "";
            }
            else if (!queued && inGame)
            {
                status = "In game";
                detail = "untick + Merge & Install to remove";
            }
            else if (queued && inGame)
            {
                status = "In game";
                detail = luaRows.Count > 0 && !luaInGame ? "paks in game · scripts not enabled" : "";
            }
            else
            {
                status = "Not enabled";
            }

            if (queued != inGame)
            {
                pendingPak++;
            }

            if (inGame && !queued && !item.IsMergedPack)
            {
                inGameNotTicked++;
            }

            item.InstallStatus = status;
            item.InstallStatusDetail = detail;
        }

        var pendingLua = Ue4ssMods.Count(m => m.IsDirty);
        InGameNotTickedCount = inGameNotTicked;
        // ArcFission Library rebuilt its data since the merge: its new rows need a Merge & Install.
        var arcFission = mergeInstall?.ArcFissionNeedsMerge == true;
        HasPendingPakChanges = pendingPak > 0 || arcFission;
        HasPendingChanges = HasPendingPakChanges || pendingLua > 0;
        var parts = new List<string>();
        if (pendingLua > 0)
        {
            parts.Add(pendingLua == 1 ? "1 UE4SS change" : $"{pendingLua} UE4SS changes");
        }

        if (pendingPak > 0)
        {
            parts.Add(pendingPak == 1 ? "1 pak mod" : $"{pendingPak} pak mods");
        }

        if (arcFission)
        {
            parts.Add("ArcFission's data (WirelessPower and other ArcFission mods) changed since the last merge");
        }

        PendingChangesSummary = parts.Count == 0
            ? ""
            : $"Not in the game yet: {string.Join(" and ", parts)}";

        // A ticked Lua mod (on its own, or the Lua half of a pak mod) with no UE4SS in the game can't run.
        ShowUe4ssLoaderMissing = !IsUe4ssLoaderInstalled
            && (Ue4ssMods.Any(m => m.IsEnabled) || _itemsByFolderName.Values.Any(i => i.IsEnabled && i.LinkedUe4ssNames.Count > 0));
    }

    /// <summary>Whether the game has the UE4SS loader. True until the first read, and when no game folder is set, so nothing warns before there's anything to check.</summary>
    [ObservableProperty]
    private bool _isUe4ssLoaderInstalled = true;

    /// <summary>The "UE4SS isn't installed" bar under the list.</summary>
    [ObservableProperty]
    private bool _showUe4ssLoaderMissing;

    public string BundledUe4ssVersion => BundledUe4ss.Version;

    /// <summary>The bar's Install button: Settings owns installing the loader (confirmation, game-running check, backup), so it is asked to do it.</summary>
    [RelayCommand]
    private void InstallUe4ssLoader() => WeakReferenceMessenger.Default.Send(new InstallUe4ssRequestedMessage());

    /// <summary>The pending bar's "Merge &amp; Install" button — hands over to that page rather than building here, since conflicts are reviewed there.</summary>
    [RelayCommand]
    private void GoToMergeInstall() => WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("merge"));

    /// <summary>
    /// One Nexus download that landed as several things — a Lua mod plus its asset paks (WirelessPower)
    /// — is one "package": every part carries the same Nexus mod ID (LibraryEntry.NexusModId,
    /// Ue4ssModMeta.NexusModId). Most Nexus mods are a single part and never form one.
    /// </summary>
    private sealed record ModPackage(int NexusModId, string DisplayName, IReadOnlyList<string> LibraryFolderNames, IReadOnlyList<string> Ue4ssFolderNames)
    {
        public int PartCount => LibraryFolderNames.Count + Ue4ssFolderNames.Count;

        /// <summary>On the Library row: the mod also has a Lua half living on the UE4SS tab.</summary>
        public string LibraryLabel => Ue4ssFolderNames.Count == 1 ? "includes a Lua mod (its UE4SS row)" : $"includes {Ue4ssFolderNames.Count} Lua mods (their UE4SS rows)";

        /// <summary>On the UE4SS row: this Lua mod is one half of a Library mod.</summary>
        public string Ue4ssLabel => $"Lua part of '{DisplayName}'";
    }

    private IReadOnlyDictionary<int, ModPackage> _packagesByNexusModId = new Dictionary<int, ModPackage>();

    /// <summary>Groups every Library entry and staged UE4SS mod by Nexus mod ID; only groups with two or more parts count.</summary>
    private Dictionary<int, ModPackage> BuildPackages()
    {
        var libraryByNexusId = _repository.GetAll()
            .Where(e => e.NexusModId is not null)
            .GroupBy(e => e.NexusModId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var ue4ssByNexusId = new Dictionary<int, List<(string Folder, string? NexusName)>>();
        foreach (var folderName in SafeStagedUe4ssNames())
        {
            var meta = _ue4ssModMetaStore.Load(folderName);
            if (meta.NexusModId is { } id)
            {
                if (!ue4ssByNexusId.TryGetValue(id, out var list))
                {
                    ue4ssByNexusId[id] = list = [];
                }

                list.Add((folderName, meta.NexusName));
            }
        }

        var packages = new Dictionary<int, ModPackage>();
        foreach (var id in libraryByNexusId.Keys.Union(ue4ssByNexusId.Keys))
        {
            var libraryEntries = libraryByNexusId.GetValueOrDefault(id) ?? [];
            var ue4ss = ue4ssByNexusId.GetValueOrDefault(id) ?? [];
            if (libraryEntries.Count + ue4ss.Count < 2)
            {
                continue;
            }

            // The Nexus title: the UE4SS sidecar records it verbatim; a Library part's name is
            // "<title> — <part>" (see DownloadsViewModel.RecordNexusMetadataAsync), so cut at the dash.
            var displayName = ue4ss.Select(u => u.NexusName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? libraryEntries.Select(e => e.Name.Split(" — ")[0]).FirstOrDefault()
                ?? $"Nexus mod #{id}";
            packages[id] = new ModPackage(id, displayName, [.. libraryEntries.Select(e => e.FolderName)], [.. ue4ss.Select(u => u.Folder)]);
        }

        foreach (var item in AllLibraryItems())
        {
            item.LinkedUe4ssNames = item.NexusModId is { } id && packages.TryGetValue(id, out var package) && package.LibraryFolderNames.Count > 0
                ? package.Ue4ssFolderNames
                : [];
        }

        foreach (var row in Ue4ssMods)
        {
            row.LinkedLibraryModName = row.NexusModId is { } id && packages.TryGetValue(id, out var package) && package.LibraryFolderNames.Count > 0
                ? package.DisplayName
                : null;
        }

        return packages;
    }

    /// <summary>Staged UE4SS names, or none when staging can't be read — package grouping must never break the Library reload.</summary>
    private IReadOnlyList<string> SafeStagedUe4ssNames()
    {
        try
        {
            return _ue4ssModRepository.GetAll();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Removes one UE4SS mod completely: out of the game's Mods folder if it's enabled there (backed
    /// up first, through the same Apply path the Disable checkbox uses), then out of staging, then
    /// its Nexus sidecar. Returns a failure reason or null.
    /// </summary>
    private string? DeleteUe4ssModCore(string folderName)
    {
        try
        {
            var contentPath = _settingsService.Current.IcarusContentPath;
            if (!string.IsNullOrWhiteSpace(contentPath))
            {
                var modsFolder = Ue4ssGamePaths.ResolveModsFolder(contentPath);
                if (_ue4ssModRepository.ListInstalledInGame(modsFolder).Contains(folderName, StringComparer.OrdinalIgnoreCase))
                {
                    var failures = _ue4ssModStateService.Apply(modsFolder, new Dictionary<string, bool> { [folderName] = false }, _backupDirectory);
                    if (failures.Count > 0)
                    {
                        return failures[0].Reason;
                    }
                }
            }

            if (_ue4ssModRepository.GetAll().Contains(folderName, StringComparer.OrdinalIgnoreCase))
            {
                _ue4ssModRepository.Delete(folderName);
            }

            _ue4ssModMetaStore.Delete(folderName);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The UE4SS tab's own Delete — the verb that was missing entirely (a real report: "I can't delete UE4SS mods inside ISL"). A package member is deleted together with its Library paks via DeleteMods, so one confirmation names every part.</summary>
    [RelayCommand]
    private void DeleteUe4ssMod(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsBuiltIn)
        {
            Ue4ssStatusMessage = $"'{item.Name}' ships with the UE4SS loader itself — untick it and Apply to disable it instead.";
            return;
        }

        if (item.NexusModId is { } id && _packagesByNexusModId.TryGetValue(id, out var package) && package.LibraryFolderNames.Count > 0)
        {
            var libraryParts = AllLibraryItems().Where(i => package.LibraryFolderNames.Contains(i.FolderName, StringComparer.OrdinalIgnoreCase)).ToList();
            if (libraryParts.Count > 0)
            {
                DeleteMods(libraryParts);
                return;
            }
        }

        var confirm = _dialogService.Confirm(
            item.RealIsEnabled
                ? $"Delete '{item.Name}'?\n\nIt is removed from the game's Mods folder (a backup is kept under Backups\\UE4SS) and from this app's staging. Re-download it to get it back."
                : $"Delete '{item.Name}'?\n\nIts staged folder is removed from disk. Re-download it to get it back.",
            "Delete UE4SS mod", ThemedConfirmSeverity.Warning);
        if (!confirm)
        {
            return;
        }

        var failure = DeleteUe4ssModCore(item.Name);
        ReloadInstalledUe4ssMods();
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        Ue4ssStatusMessage = failure is null ? $"Deleted '{item.Name}'." : $"Couldn't delete '{item.Name}': {failure}";
        _activityLog.Log(failure is null ? $"Deleted UE4SS mod '{item.Name}'." : $"Couldn't delete UE4SS mod '{item.Name}': {failure}",
            failure is null ? ActivityEntryKind.Info : ActivityEntryKind.Warning);
    }

    /// <summary>Where this mod's files actually are right now — the game's Mods folder when enabled, this app's staging when not.</summary>
    [RelayCommand]
    private void OpenUe4ssModFolder(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            var contentPath = _settingsService.Current.IcarusContentPath;
            var path = item.RealIsEnabled && !string.IsNullOrWhiteSpace(contentPath)
                ? Path.Combine(Ue4ssGamePaths.ResolveModsFolder(contentPath), item.Name)
                : _ue4ssModRepository.GetFolderPath(item.Name);
            if (!Directory.Exists(path))
            {
                Ue4ssStatusMessage = $"'{item.Name}' has no folder at {path}.";
                return;
            }

            UrlOpener.TryOpen(path);
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't open the folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Moves every dirty row to match its toggled state — enabling copies staging→game, disabling
    /// backs up then moves game→staging (IUe4ssModStateService.Apply) — then reloads so every row's
    /// RealIsEnabled (and the page's own dirty state) reflects what's actually on disk now.
    /// </summary>
    [RelayCommand]
    private void ApplyUe4ssChanges()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            Ue4ssStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath);
            var desired = Ue4ssMods.Where(m => m.IsDirty).ToDictionary(m => m.Name, m => m.IsEnabled);
            var changedCount = desired.Count;

            var failures = _ue4ssModStateService.Apply(modsFolder, desired, _backupDirectory);
            // Always reload, even on partial failure — Apply itself never rolls back a change it
            // already made to an EARLIER mod in this same batch just because a LATER one failed, so
            // the UI must re-read real on-disk state either way rather than keep showing whatever
            // was true before this call.
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = failures.Count == 0
                ? $"Applied {changedCount} change(s)."
                : $"Applied {changedCount - failures.Count} of {changedCount} change(s) — failed: "
                    + string.Join("; ", failures.Select(f => $"{f.Name} ({f.Reason})"));
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Apply failed: {ex.Message}";
            _activityLog.Log($"Applying UE4SS changes failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Manual counterpart to DownloadsViewModel.EnrichUe4ssModFromNexusAsync (which only ever runs
    /// automatically when a UE4SS mod is activated from a pending Nexus download) — mirrors
    /// LinkToNexus/LinkFolderToNexusAsync's own shape exactly, just writing through
    /// IUe4ssModMetaStore's per-folder sidecar instead of ILibraryRepository. Deliberately not
    /// offered for a framework-built-in row (see the XAML's own IsBuiltIn-gated menu item) — those
    /// ship with the loader itself and have no standalone Nexus page of their own to link to.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task LinkUe4ssModToNexus(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var dialog = new LinkNexusDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var meta = _ue4ssModMetaStore.Load(item.Name);
            meta.NexusModId = dialog.NexusModId;
            _ue4ssModMetaStore.Save(item.Name, meta);

            var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
            if (apiKey is not null)
            {
                try
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", dialog.NexusModId);
                    if (info is not null)
                    {
                        meta.NexusVersion = info.Version;
                        meta.NexusName = info.Name;
                        _ue4ssModMetaStore.Save(item.Name, meta);
                    }
                }
                catch (Exception)
                {
                    // Best-effort — the ID link above already succeeded regardless.
                }
            }

            // Rebuilds every row (Ue4ssModRowViewModel's NexusModId/KnownVersion are constructor-set,
            // not observable) so this row picks up what was just saved — same reason ApplyUe4ssChanges
            // already reloads after its own write.
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = $"Linked '{item.Name}' to Nexus mod {dialog.NexusModId}.";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't link: {ex.Message}";
        }
    }

    /// <summary>
    /// Manual, user-declared-only counterpart to LinkUe4ssModToNexus, right next to it in the row's
    /// own context menu — same IUe4ssModMetaStore sidecar write, same reload-after-write to refresh
    /// the row's constructor-set properties. There is no automatic source for a mod's own minimum
    /// UE4SS version (see Ue4ssModMeta.MinUe4ssVersion's own doc comment), so this is the only way
    /// the field is ever set: the user reads it off the mod's own Nexus page/readme and types it in.
    /// Reuses IDialogService.PromptRename (this app's one generic single-line text prompt, despite
    /// its name) rather than a new dialog type — resetValue is left null so "Clear requirement"
    /// clears the field entirely (Ue4ssVersionComparer.Compare then reports Unknown, never a
    /// warning), the same "reset means clear the override" semantics Library's own RenameItem uses.
    /// </summary>
    [RelayCommand]
    private void SetMinUe4ssVersion(Ue4ssModRowViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var result = _dialogService.PromptRename(
            item.MinUe4ssVersion ?? string.Empty,
            description: "Enter the minimum UE4SS loader version this mod needs (e.g. \"3.0.1\") — read it from the mod's own Nexus page or readme, since this app has no way to detect a mod's own requirement automatically. Compared against your installed UE4SS loader version to show a warning if it's too old.",
            resetValue: null,
            resetLabel: "Clear requirement",
            resetTooltip: "Removes the minimum UE4SS version requirement — no warning will show for this mod",
            title: "Set minimum UE4SS version",
            fieldLabel: "Minimum UE4SS version");
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            var meta = _ue4ssModMetaStore.Load(item.Name);
            meta.MinUe4ssVersion = string.IsNullOrWhiteSpace(result.NewDisplayName) ? null : result.NewDisplayName.Trim();
            _ue4ssModMetaStore.Save(item.Name, meta);

            // Same reason ApplyUe4ssChanges/LinkUe4ssModToNexus already reload after their own
            // writes — Ue4ssModRowViewModel's MinUe4ssVersion is constructor-set, not observable.
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = meta.MinUe4ssVersion is null
                ? $"Cleared the minimum UE4SS version requirement for '{item.Name}'."
                : $"'{item.Name}' now requires UE4SS v{meta.MinUe4ssVersion}+.";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't save: {ex.Message}";
        }
    }

    /// <summary>
    /// The UE4SS tab's own Import… — the SAME content sniffing every other entry point uses
    /// (ExtractedModClassifier via ImportOnePath), so an archive in game-folder layout imported
    /// here lands exactly as it would from Nexus: the Lua folder staged here, its paks as one
    /// Library mod of the same name. It used to unzip straight into staging, which dropped a
    /// bundle's paks on the floor.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportUe4ssModAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a UE4SS mod archive",
            Filter = "Mod archives (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var before = new HashSet<string>(SafeStagedUe4ssNames(), StringComparer.OrdinalIgnoreCase);
        await ImportPaths(dialog.FileNames);
        ReloadInstalledUe4ssMods();
        var added = SafeStagedUe4ssNames().Where(n => !before.Contains(n)).ToList();
        Ue4ssStatusMessage = added.Count switch
        {
            0 => StatusMessage,
            1 => $"Staged '{added[0]}' — enable it below, then click Apply.",
            _ => $"Staged {added.Count} mods ({string.Join(", ", added)}) — enable them below, then click Apply.",
        };
    }

    [RelayCommand]
    private void ImportFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select the mod's extracted folder" };
        if (dialog.ShowDialog() == true)
        {
            TryImport(dialog.FolderName, path => _repository.Import(path));
        }
    }

    /// <summary>
    /// Auto-detects what's inside instead of requiring the user to already know — an EXMODZ is
    /// itself just a zip with a different extension, and a mod author's own download could just as
    /// easily be a plain .zip/.rar/.7z containing an EXMOD-shaped mod, a bare prebuilt .pak, or a
    /// UE4SS mod folder. Same ExtractedModClassifier the Nexus pending-download Activate flow
    /// already uses for exactly this "don't yet know what's inside" situation — this is that
    /// detection's other real caller, a manual local-file import with no Nexus provenance to tag.
    /// Multiselect, routed through ImportPaths below, so picking several archives at once works the
    /// same as dragging several onto the page.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select one or more mod archives",
            Filter = "Mod archive (*.EXMODZ, *.zip, *.rar, *.7z)|*.EXMODZ;*.zip;*.rar;*.7z",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportPaths(dialog.FileNames);
    }

    /// <summary>A hint for the end of the current import batch's summary (set by the manual install).</summary>
    private string? _importNote;

    /// <summary>
    /// Mod Organizer's manual install, offered only when importing an archive failed: choose what in
    /// it to install and what each piece is (ManualInstaller / ManualInstallWindow). True when
    /// something was installed; false when the user cancelled (the caller reports the failure).
    /// </summary>
    private async Task<bool> InstallArchiveManuallyAfterFailureAsync(string path, string reason)
    {
        var scratch = ManualInstaller.UnpackForManualInstall(path);
        try
        {
            if (Downloads.ManualInstall(scratch, Path.GetFileNameWithoutExtension(path), reason, ManualInstaller.Suggest(scratch)) is not { Count: > 0 } plan)
            {
                return false;
            }

            var installed = await ManualInstaller.InstallAsync(
                plan, _repository, _ue4ssModRepository, _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath,
                source: null, nexusModId: null, catalogEntryId: null, ue4ssNamesInUse: null);
            _activityLog.Log($"Installed {installed.Count} part(s) of '{Path.GetFileName(path)}' manually.", ActivityEntryKind.Success);
            if (installed.Any(i => i.Kind == PendingDownloadActivationKind.Ue4ssMod))
            {
                _importNote = "UE4SS mods land unticked: tick them, then Apply UE4SS changes.";
            }

            return true;
        }
        finally
        {
            ManualInstaller.TryDelete(scratch);
        }
    }

    /// <summary>Lint findings from an import (ModLints via the classifier) — each one a real "this part of the mod is broken" note the user should see, kept in the Activity panel so it survives the next status line.</summary>
    private void LogImportWarnings(string path, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
        {
            _activityLog.Log($"{Path.GetFileName(path)}: {warning}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Imports one already-known path — a real folder, a bare .pak, or (falling through to the
    /// same auto-detection ImportFile's own dialog already uses) any other file, sniffed as an
    /// archive by its actual content rather than trusted by extension. Shared by drag-and-drop and
    /// every multi-select import path below, so "what kind of thing is this path" is decided in
    /// exactly one place.
    /// </summary>
    private async Task ImportOnePath(string path)
    {
        if (Directory.Exists(path))
        {
            if (Directory.EnumerateFiles(path, "*.EXMOD", SearchOption.AllDirectories).Any())
            {
                var entry = _repository.Import(path);
                _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
                return;
            }

            // No .EXMOD inside — a folder holding a bare .pak or a UE4SS mod gets the same content
            // sniffing an archive does (an audit finding: it used to fail with "No .EXMOD file
            // found"). Classified from a COPY: the classifier unpacks any nested archive it finds
            // into the folder it's given, and that must never be the user's own folder.
            var tempCopy = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Import_{Guid.NewGuid():N}");
            try
            {
                FolderBackup.CopyDirectory(path, tempCopy);
                var folderResult = await ExtractedModClassifier.ClassifyAndImport(
                    tempCopy, path, _repository, _ue4ssModRepository,
                    _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, chooseArchive: Downloads.ChooseArchive);
                var (folderEntryName, folderFolderName, folderKind, _) = folderResult;
                _activityLog.Log(
                    folderKind == PendingDownloadActivationKind.Library ? $"Imported '{folderEntryName}'." : $"Imported '{folderFolderName}' as a UE4SS mod.",
                    ActivityEntryKind.Success);
                LogImportWarnings(path, folderResult.Warnings);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempCopy, recursive: true);
                }
                catch (Exception)
                {
                    // Best-effort temp cleanup.
                }
            }

            return;
        }

        if (path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            var entry = await _prebuiltPakImporter.ImportAsync(path, _dataFolder, _settingsService.Current.UnrealPakExePath);
            _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
            LogImportWarnings(path, entry.ImportWarnings);
            return;
        }

        var tempExtractDirectory = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Import_{Guid.NewGuid():N}");
        try
        {
            AnyArchiveExtractor.ExtractToDirectory(path, tempExtractDirectory);
            ClassifiedImport archiveResult;
            try
            {
                archiveResult = await ExtractedModClassifier.ClassifyAndImport(
                    tempExtractDirectory, path, _repository, _ue4ssModRepository,
                    _prebuiltPakImporter, _dataFolder, _settingsService.Current.UnrealPakExePath, chooseArchive: Downloads.ChooseArchive);
            }
            catch (Exception ex) when (ex is not ImportCancelledException)
            {
                // Couldn't sort it out automatically: let the user say what's what (Mod Organizer's
                // manual install). Cancelling that reports the original failure as before.
                if (await InstallArchiveManuallyAfterFailureAsync(path, ex.Message))
                {
                    return;
                }

                throw;
            }

            var (entryName, folderName, kind, _) = archiveResult;
            _activityLog.Log(
                kind == PendingDownloadActivationKind.Library ? $"Imported '{entryName}'." : $"Imported '{folderName}' as a UE4SS mod.",
                ActivityEntryKind.Success);
            LogImportWarnings(path, archiveResult.Warnings);
        }
        finally
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

    /// <summary>
    /// Shared entry point for drag-and-drop (LibraryView's own code-behind Drop handler) and every
    /// multi-select "Import…" dialog — each path is imported independently via ImportOnePath, so
    /// one bad file in a batch (a corrupt archive, a folder with no .EXMOD/.pak inside) can't abort
    /// the rest. Deliberately no per-file "link to Nexus?" follow-up here, unlike ImportPak's own
    /// single-file prompt — asking that once per imported file would make importing several at once
    /// unusable; "Link to Nexus ID…" stays reachable afterward from each row's own context menu.
    /// </summary>
    public async Task ImportPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var importedCount = 0;
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                await ImportOnePath(path);
                importedCount++;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}: {ex.Message}");
            }
        }

        Reload();
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        ReloadInstalledUe4ssMods();

        StatusMessage = (failures.Count == 0
            ? (paths.Count == 1 ? "Imported 1 mod." : $"Imported {importedCount} mod(s).")
            : $"Imported {importedCount} of {paths.Count} — {string.Join("; ", failures)}")
            + (_importNote is { } note ? " " + note : "");
        _importNote = null;
        if (failures.Count > 0)
        {
            _activityLog.Log($"Import failed: {string.Join("; ", failures)}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// A prebuilt .pak carries no embedded name/author of its own (unlike an EXMOD), so right after
    /// import this offers to link it to a real Nexus mod ID immediately — rather than leaving that
    /// to the separate "Link to Nexus ID…" context-menu action on an already-imported mod, which is
    /// easy to never discover for something imported this way in the first place.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportPak()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a prebuilt .pak file",
            Filter = "Unreal pak package (*.pak)|*.pak",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var entry = await TryImportPakAsync(dialog.FileName);
        if (entry is null)
        {
            return;
        }

        var linkPrompt = _dialogService.Confirm(
            $"'{entry.Name}' doesn't have a name/author of its own yet — imported .pak files don't carry that until you tell it where they came from.\n\nIs this a Nexus mod? Link it now so IcarusStarlink can show its real name and check for updates.",
            "Link to Nexus?", ThemedConfirmSeverity.Question);
        if (!(linkPrompt))
        {
            return;
        }

        var nexusDialog = new LinkNexusDialog { Owner = Application.Current.MainWindow };
        if (nexusDialog.ShowDialog() != true)
        {
            return;
        }

        await LinkFolderToNexusAsync(entry.FolderName, entry.Name, nexusDialog.NexusModId);
    }

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
        Reload(fullResync: true);
    }

    [RelayCommand]
    private void NewMod()
    {
        var dialog = new NewModDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var entry = _repository.CreateBlankMod(dialog.ModName, dialog.ModAuthor, dialog.SelectedTemplate);
            StatusMessage = $"Created '{entry.Name}'.";
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            _activityLog.Log($"Created new mod '{entry.Name}'.", ActivityEntryKind.Success);
            OpenEditor(entry.FolderName, startWizard: ModTemplateContent.ItemPresetFor(dialog.SelectedTemplate));
        }
        catch (Exception ex)
        {
            // Same UI boundary as TryImport — folder creation can fail for the same reasons any
            // other Extracted_Mods write can (permission denied, disk full, ...).
            StatusMessage = $"Couldn't create the mod: {ex.Message}";
        }
    }

    /// <summary>
    /// The action behind the "Update available" badge. A Database-sourced mod genuinely updates in
    /// place: re-download from the catalog, then replace (delete-then-reimport under the same
    /// "Replace, not accumulate" rule Install/patch-import already use), carrying pin/favorite/
    /// notes across the swap. A Nexus-sourced mod jumps to its card on the native Nexus page
    /// instead — a real in-app Nexus download needs a file ID this app doesn't have (only the mod
    /// ID), so the honest action is the card whose own Download button starts a Mod Manager
    /// Download through the existing nxm:// pipeline (or its "Open page" action, for a non-Premium
    /// account that needs the real website).
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task GetUpdateAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (string.Equals(item.Source, "Nexus", StringComparison.OrdinalIgnoreCase))
        {
            // Nexus downloads persist in Pending Downloads (MO2-style) — if a file for this mod is
            // already sitting there UNCLAIMED, activating it is one click closer than a fresh
            // browser trip, so point there instead of the website. (Can't distinguish which version
            // that file is — PendingDownloadEntry carries no version — so this stays a pointer, not
            // an automatic activation of a possibly-older file.)
            //
            // "Unclaimed" specifically — not just "an entry for this mod ID exists at all" (a real
            // bug found live): downloads are never removed except by an explicit Discard, so ANY mod
            // ever downloaded from Nexus always has at least one PendingDownloadEntry sitting around
            // forever. The old check matched regardless of activation state, meaning once a mod had
            // been downloaded via this app even once, clicking Update on it could never again open
            // its Nexus page for a newer file — it always redirected to Pending Downloads instead,
            // even when the earlier download was already successfully activated and IS the mod
            // currently showing right here. Only a pending entry that ISN'T currently reflected as a
            // live Library item (never activated, or activated then since removed) is genuinely
            // "unclaimed" and worth pointing the user at instead of Nexus.
            var hasUnclaimedDownload = item.NexusModId is { } nexusModId && _pendingDownloadStore.Entries
                .Where(e => e.ModId == nexusModId)
                .Any(e => e.AllActivatedItems.Count == 0
                    || e.AllActivatedItems.Any(activated => activated.Kind == PendingDownloadActivationKind.Library
                        ? !_repository.GetAll().Any(le => string.Equals(le.FolderName, activated.FolderName, StringComparison.OrdinalIgnoreCase))
                        : !_ue4ssModRepository.GetAll().Any(f => string.Equals(f, activated.FolderName, StringComparison.OrdinalIgnoreCase))));
            if (hasUnclaimedDownload)
            {
                StatusMessage = "A downloaded file for this mod is already in this page's own Mods tab — Activate/Reinstall it there (or re-download from Nexus if it's older than the update).";
                return;
            }

            SearchNexusFor(item);
            StatusMessage = "Found the mod's card on the Nexus page — use its Download button to pull the update through this app.";
            return;
        }

        StatusMessage = $"Updating '{item.Name}'…";
        try
        {
            // Reuses Downloads' own cached catalog (fetching once if it hasn't yet) instead of an
            // independent re-fetch of both sources — the "Update available" badge that led here was
            // itself computed from a catalog check that already ran (CheckForUpdatesAsync, below),
            // so this isn't working from data any staler than what justified showing the button.
            var allEntries = await Downloads.GetOrFetchCatalogAsync();

            // Same ID-first, name-fallback matching CheckForUpdatesAsync itself uses.
            var catalogEntry = (item.CatalogEntryId is not null ? allEntries.FirstOrDefault(e => e.Id == item.CatalogEntryId) : null)
                ?? allEntries.FirstOrDefault(e => CatalogKey.Normalize(e.Name, e.Author) == CatalogKey.Normalize(item.Name, item.Author));
            if (catalogEntry is null)
            {
                StatusMessage = $"Couldn't find '{item.Name}' in the catalog anymore.";
                return;
            }

            var downloadUrl = catalogEntry.ExmodzUrl ?? catalogEntry.PakUrl;
            if (downloadUrl is null)
            {
                StatusMessage = $"'{catalogEntry.Name}' has no downloadable file listed.";
                return;
            }

            // A real catalog entry can point at a github.com "view this file" page instead of its
            // raw content (a common submission mistake — see GitHubBlobUrl's own doc comment) —
            // downloading that HTML page and treating it as an archive fails opaquely, so this is
            // normalized before ever reaching the actual GetByteArrayAsync call below.
            downloadUrl = GitHubBlobUrl.ToRawContentUrl(downloadUrl);

            var isExmodz = catalogEntry.ExmodzUrl is not null;
            var tempPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_{Guid.NewGuid():N}{(isExmodz ? ".EXMODZ" : ".pak")}");
            try
            {
                var bytes = await _downloadHttpClient.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempPath, bytes);

                // Captured before the delete so the swap doesn't silently drop them; cancel any
                // pending debounced notes save first, same reasoning DeleteSelected documents.
                var (folderName, isPinned, isFavorite, notes) = (item.FolderName, item.IsPinned, item.IsFavorite, item.Notes);
                var originalEntry = _repository.GetAll()
                    .FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
                var displayNameOverride = originalEntry?.DisplayNameOverride;
                var originalCatalogEntryId = originalEntry?.CatalogEntryId;
                item.CancelPendingSave();

                // Snapshot first, so the delete-then-reimport below can genuinely roll back — a
                // download that parsed as bytes can still fail import validation, and without this
                // that failure would lose the working old copy.
                _repository.BackupMod(folderName);
                _repository.Delete(folderName);

                try
                {
                    var imported = isExmodz
                        ? _repository.Import(tempPath, source: "Database", catalogEntryId: catalogEntry.Id)
                        : await _prebuiltPakImporter.ImportAsync(
                            tempPath, _dataFolder, _settingsService.Current.UnrealPakExePath,
                            source: "Database", catalogEntryId: catalogEntry.Id, name: catalogEntry.Name, author: catalogEntry.Author);

                    // The saved source pak is only dropped once the new copy is in (an audit finding:
                    // deleting it up front meant a FAILED update restored the old mod but left it with
                    // no source pak to ever re-derive from). The pak route's own Save already overwrote it.
                    if (isExmodz)
                    {
                        _prebuiltPakSourceStore.Delete(folderName);
                    }
                    if (isPinned || isFavorite || !string.IsNullOrEmpty(notes))
                    {
                        _repository.UpdateMetadata(imported.FolderName, isPinned, isFavorite, notes);
                    }

                    if (displayNameOverride is not null)
                    {
                        _repository.SetDisplayNameOverride(imported.FolderName, displayNameOverride);
                    }

                    StatusMessage = $"Updated '{imported.Name}' to v{imported.Version}.";
                    _activityLog.Log($"Updated '{imported.Name}' from the catalog.", ActivityEntryKind.Success);

                    // A real bug found live: HasUpdateAvailable compares Version against LatestVersion
                    // (set by CheckForUpdatesAsync from the CATALOG's own listed version string), but
                    // imported.Version comes from the downloaded file's OWN internal metadata — a mod
                    // author can bump one and not the other (e.g. catalog says "2.0", the .EXMODZ
                    // itself still declares "2.0.0"), so even a fully successful update could leave
                    // the two permanently unequal and the "Update available" badge stuck forever. The
                    // file we just fetched and installed is, by definition, current as of right now —
                    // syncing LatestVersion straight to what we actually just installed (rather than
                    // whatever string the catalog separately happens to use) clears the badge
                    // regardless of any such mismatch in the source data.
                    item.LatestVersion = imported.Version;

                    // The backup taken above is the mod's own previous version, so "what did the
                    // author actually change?" is answerable right now — offered rather than shown
                    // automatically, since an update the user just wanted applied shouldn't force a
                    // window open. Passes BOTH folder names, since a reimport can land on a
                    // different one than it started with (e.g. the mod's own declared fileName
                    // changed between versions): the OLD folderName is where BackupMod above
                    // actually saved the backup, but imported.FolderName is where the just-reimported
                    // CURRENT copy now lives on disk — using the old name for both would find the
                    // backup but then fail to find the current folder (already deleted under that
                    // name), and using the new name for both would fail to find the backup instead.
                    await OfferVersionComparisonAsync(imported.Name, folderName, imported.FolderName);
                }
                catch (Exception importEx)
                {
                    var restored = _repository.RestoreLatestModBackup(folderName);
                    if (restored)
                    {
                        // RestoreLatestModBackup only brings back the mod's own EXMOD/asset folder —
                        // BackupMod never captured the .immmeta.json sidecar (it's scoped to the
                        // mod's own folder, and the sidecar lives elsewhere), and Delete() above
                        // deleted that sidecar outright with nothing to recreate it. Without this, a
                        // failed update silently reset Pin/Favorite/Notes/display name/Source/catalog
                        // link to blank even though the mod's real content came back fine — a real
                        // bug found live ("the source was forgotten").
                        if (isPinned || isFavorite || !string.IsNullOrEmpty(notes))
                        {
                            _repository.UpdateMetadata(folderName, isPinned, isFavorite, notes);
                        }

                        if (displayNameOverride is not null)
                        {
                            _repository.SetDisplayNameOverride(folderName, displayNameOverride);
                        }

                        if (originalCatalogEntryId is not null)
                        {
                            _repository.SetCatalogEntry(folderName, originalCatalogEntryId);
                        }
                    }

                    StatusMessage = restored
                        ? $"Update failed ({importEx.Message}) — your previous copy was restored from its backup."
                        : $"Update failed: {importEx.Message}";
                }

                // Plain Reload(), not fullResync — the LibraryChangedMessage sent right below is
                // handled by this same VM's own registration (see the constructor), which itself
                // calls Reload(fullResync: true) on every such message, so the cached row's
                // Version/Notes/etc. still get resynced from the reimported entry without paying
                // for a second one here.
                Reload();
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }
            finally
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception)
                {
                    // Best-effort temp cleanup, same as Downloads' own download path.
                }
            }
        }
        catch (Exception ex)
        {
            // Failures before the swap starts (catalog fetch, download) — the mod on disk is
            // untouched at this point; the swap itself has its own backup-and-restore path above.
            StatusMessage = $"Update failed: {ex.Message}";
            _activityLog.Log($"Updating '{item.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Manually converts an already-imported opaque .pak entry into a real, editable EXMOD, in
    /// place — the same conversion new imports now attempt automatically (IPrebuiltPakToExmodConverter),
    /// just run on demand for one of the entries that predates that feature, or that stayed opaque
    /// because conversion wasn't possible at import time (e.g. UnrealPak.exe wasn't set up yet).
    /// Backs up the mod's folder first (FolderBackup, same as "Create mod backup") since this is a
    /// real, destructive rewrite — the source .pak is deleted once its content is fully represented
    /// as EXMOD rows/assets — and restores that backup if anything after the delete throws, so a
    /// failure never leaves the mod in a half-converted, unreadable state.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ConvertToExmodAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.IsOpaquePak)
        {
            StatusMessage = $"'{item.Name}' is already a real, editable mod — nothing to convert.";
            return;
        }

        if (!_foldersCurrentlyConvertingToExmod.Add(item.FolderName))
        {
            // A per-folder guard, not a page-wide one — converting Mod A must never silently
            // swallow a concurrent request to convert unrelated Mod B.
            return;
        }

        try
        {
            var (converted, message) = await TryConvertOneToExmodAsync(item);
            StatusMessage = message;
            if (converted)
            {
                _activityLog.Log($"Converted '{item.Name}' from a prebuilt pak into an editable mod.", ActivityEntryKind.Success);
            }
        }
        catch (Exception ex)
        {
            // Same UI boundary the whole-library sweep already has (an audit finding: without it a
            // locked .pak sent the exception to the crash-report handler and the click did nothing).
            StatusMessage = $"Couldn't convert '{item.Name}': {ex.Message}";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
        }
        finally
        {
            _foldersCurrentlyConvertingToExmod.Remove(item.FolderName);
        }
    }

    /// <summary>
    /// The actual convert-one-mod logic, shared by the single-item command above and the
    /// whole-library sweep below — everything except the per-folder reentrancy guard and the
    /// Activity log entry, which each caller handles its own way (the sweep logs one summary line
    /// instead of one per mod).
    /// </summary>
    private async Task<(bool Converted, string Message)> TryConvertOneToExmodAsync(LibraryItemViewModel item)
    {
        if (item.IsPakBundle)
        {
            return (false, $"'{item.Name}' is a bundle of {item.PakCount} paks (one download) — it stays as paks; every one of them is folded into the merge as-is.");
        }

        var folder = _repository.GetFolderPath(item.FolderName);
        var pakFilePath = Directory.GetFiles(folder, "*.pak", SearchOption.AllDirectories).FirstOrDefault();
        if (pakFilePath is null)
        {
            return (false, $"'{item.Name}' has no .pak file to convert.");
        }

        var report = new MergeReport();
        var converted = await _prebuiltPakToExmodConverter.TryConvertAsync(
            pakFilePath, _dataFolder, _settingsService.Current.UnrealPakExePath ?? "", item.Name, item.Author, report);

        if (converted is null)
        {
            return report.Warnings.Count > 0
                ? (false, $"Couldn't convert '{item.Name}': {report.Warnings[0]}")
                : (false, $"Couldn't convert '{item.Name}' to an editable mod right now.");
        }

        _repository.BackupMod(item.FolderName);
        try
        {
            // Kept, not discarded — see IPrebuiltPakSourceStore's own doc comment: a later game
            // update can re-derive a fresher diff from this exact same original content instead of
            // this conversion being frozen forever at today's game data.
            _prebuiltPakSourceStore.Save(item.FolderName, pakFilePath);
            File.Delete(pakFilePath);
            ExmodFolder.Write(folder, converted.Contents);
        }
        catch
        {
            // The delete+write above should be near-instant on a local disk, but a failure
            // partway through (disk full, permission revoked mid-operation) must not leave the
            // mod folder in a state with neither a valid .pak nor a valid .EXMOD — the backup
            // just taken makes that recoverable instead of a real, permanent loss.
            _repository.RestoreLatestModBackup(item.FolderName);
            throw;
        }

        _repository.Refresh();
        // Only marked when the conversion actually produced placeholder metadata — see
        // IPrebuiltPakToExmodConverter's own doc comment on HasAuthorDeclaredMetadata for why
        // marking a real-author-declared conversion here would risk a later Nexus link silently
        // overwriting the name the author already got right.
        if (!converted.HasAuthorDeclaredMetadata)
        {
            _repository.MarkConvertedFromPrebuiltPak(item.FolderName);
        }
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        return (true, $"Converted '{item.Name}' to a real, editable mod — a backup of the original was kept.");
    }

    /// <summary>
    /// Sweeps every currently-opaque, non-merged-pack entry and retries conversion now — for mods
    /// that stayed opaque because UnrealPak.exe/the extracted Data folder weren't ready yet at
    /// import time (every real import path already tries conversion automatically; this is just a
    /// deliberate, explicit retry once those prerequisites exist, not a silent background pass —
    /// this still deletes each mod's own source .pak, the same real, destructive rewrite the
    /// per-mod action already is). A merged pack (IsMergedPack) is never a candidate — it's a
    /// computed aggregate of other mods, not an authored one, and converting it would produce a
    /// meaningless giant diff (see MergeInstallViewModel's own reimport, which deliberately never
    /// routes through this either).
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ConvertAllOpaqueModsAsync()
    {
        if (IsConvertingAllOpaqueMods)
        {
            return;
        }

        var candidates = AllLibraryItems().Where(i => i.IsOpaquePak && !i.IsMergedPack && !i.IsPakBundle).ToList();
        if (candidates.Count == 0)
        {
            StatusMessage = "No prebuilt paks left to make editable.";
            return;
        }

        IsConvertingAllOpaqueMods = true;
        try
        {
            var convertedCount = 0;
            var stillOpaqueCount = 0;
            foreach (var item in candidates)
            {
                if (!_foldersCurrentlyConvertingToExmod.Add(item.FolderName))
                {
                    continue;
                }

                try
                {
                    var (converted, message) = await TryConvertOneToExmodAsync(item);
                    if (converted)
                    {
                        convertedCount++;
                        _activityLog.Log($"Converted '{item.Name}' from a prebuilt pak into an editable mod.", ActivityEntryKind.Success);
                    }
                    else
                    {
                        stillOpaqueCount++;
                        _activityLog.Log(message, ActivityEntryKind.Info);
                    }
                }
                catch (Exception ex)
                {
                    // One mod's own I/O failure (disk full, a locked file) shouldn't abandon the
                    // rest of the sweep — matching CheckModsAgainstCurrentDataAsync's own per-mod
                    // isolation.
                    stillOpaqueCount++;
                    _activityLog.Log($"Couldn't convert '{item.Name}': {ex.Message}", ActivityEntryKind.Warning);
                }
                finally
                {
                    _foldersCurrentlyConvertingToExmod.Remove(item.FolderName);
                }
            }

            StatusMessage = stillOpaqueCount > 0
                ? $"Made {convertedCount} of {candidates.Count} prebuilt pak(s) editable — {stillOpaqueCount} couldn't convert right now (see Activity log)."
                : $"Made all {convertedCount} prebuilt pak(s) editable.";
        }
        finally
        {
            IsConvertingAllOpaqueMods = false;
        }
    }

    /// <summary>
    /// Runs silently after every successful "Update data folder" (see the WeeklyChangeReportUpdatedMessage
    /// registration above) — not a manual action, since it's meant to keep every convertible/converted
    /// mod current with whatever the game now looks like, without anyone having to remember to ask.
    /// Two things happen, neither ever touching a hand-edited mod:
    ///
    /// 1. Any mod still opaque (never successfully converted — UnrealPak.exe or the Data folder
    ///    likely wasn't ready yet at import time) gets the exact same conversion attempt "Convert
    ///    opaque mods now" makes, using whatever .pak already sits in its own folder.
    /// 2. Any mod already converted from a prebuilt pak (ConvertedFromPrebuiltPak) that hasn't been
    ///    opened and saved in the editor since (IsLocallyEdited) gets re-derived fresh from its own
    ///    saved source pak (IPrebuiltPakSourceStore) against the just-updated game data — a
    ///    conversion's field diff is only ever as fresh as the base data available the moment it
    ///    ran, so without this a mod converted before a patch stays frozen against pre-patch data
    ///    forever. The moment a mod is hand-edited it "graduates" and is never silently touched
    ///    here again — an edit that took real thought must never be silently replaced by a fresh
    ///    mechanical re-diff.
    ///
    /// Never surfaces a blocking dialog or a status message on its own (this can fire while the
    /// user is looking at an entirely different page) — results land in the Activity log instead,
    /// the same "silent unless something happened" convention the rest of this app already uses
    /// for background passes.
    /// </summary>
    private async Task RefreshConvertedOpaqueModsAsync()
    {
        var stillOpaque = AllLibraryItems().Where(i => i.IsOpaquePak && !i.IsMergedPack && !i.IsPakBundle).ToList();
        var alreadyConverted = AllLibraryItems()
            .Where(i => i.ConvertedFromPrebuiltPak && !i.IsLocallyEdited && !i.IsOpaquePak)
            .ToList();

        var convertedCount = 0;
        var refreshedCount = 0;

        foreach (var item in stillOpaque)
        {
            if (!_foldersCurrentlyConvertingToExmod.Add(item.FolderName))
            {
                continue;
            }

            try
            {
                var (converted, _) = await TryConvertOneToExmodAsync(item);
                if (converted)
                {
                    convertedCount++;
                }
            }
            catch (Exception ex)
            {
                _activityLog.Log($"Couldn't convert '{item.Name}' after updating game data: {ex.Message}", ActivityEntryKind.Warning);
            }
            finally
            {
                _foldersCurrentlyConvertingToExmod.Remove(item.FolderName);
            }
        }

        // Same open-editor guard CheckModsAgainstCurrentDataAsync uses (an audit finding): rewriting
        // a folder under an open editor window means its next Save writes the stale in-memory
        // package back over the fresh diff — and marks it locally edited, so it's never refreshed again.
        var foldersWithOpenEditors = _openEditorCountByFolder.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in alreadyConverted)
        {
            var sourcePakPath = _prebuiltPakSourceStore.TryGetPath(item.FolderName);
            if (sourcePakPath is null || foldersWithOpenEditors.Contains(item.FolderName) || !_foldersCurrentlyConvertingToExmod.Add(item.FolderName))
            {
                continue;
            }

            try
            {
                var report = new MergeReport();
                var converted = await _prebuiltPakToExmodConverter.TryConvertAsync(
                    sourcePakPath, _dataFolder, _settingsService.Current.UnrealPakExePath ?? "", item.Name, item.Author, report);
                if (converted is null)
                {
                    continue;
                }

                var folder = _repository.GetFolderPath(item.FolderName);

                // The converter derives FileName from the SOURCE pak's file name, and the source store
                // keeps that pak as "{folderName}.pak" — so for a collision-suffixed folder
                // ("CoolMod_P_2" holding "CoolMod_P.EXMOD") the re-conversion wrote a SECOND .EXMOD
                // beside the original, and the mod vanished from the Library as "more than one
                // .EXMOD" on the next rescan (an audit finding). Keep the existing package's own name.
                converted.Contents.Package.FileName = ExmodFolder.ReadPackageOnly(folder).FileName;

                // How many sure problems the copy being replaced has: more in the fresh conversion
                // means fixes were made to it, so they're made again below.
                var folderName = item.FolderName;
                int? sureBefore;
                try
                {
                    sureBefore = await Task.Run(() => FixService.Check(folderName).SureCount);
                }
                catch (Exception)
                {
                    sureBefore = null;
                }

                _repository.BackupMod(item.FolderName);
                try
                {
                    // This entry is already ConvertedFromPrebuiltPak=true (it's re-converting one
                    // of `alreadyConverted`) — deliberately NOT reconsidering that flag even if
                    // this fresh conversion now finds a bundled EXMOD with real author metadata:
                    // a prior Nexus/Database link may have already overwritten Name/Author once,
                    // and un-marking here wouldn't undo that. Left as a known, narrow residual gap
                    // rather than guessed at.
                    ExmodFolder.Write(folder, converted.Contents);
                }
                catch
                {
                    _repository.RestoreLatestModBackup(item.FolderName);
                    throw;
                }

                _repository.Refresh();
                refreshedCount++;

                // The fixes the user chose for this mod in the fix window, made again on the fresh
                // conversion (its sure ones the converter already left out).
                // Off the UI thread: the service is shared with any check or fix pass and waits its turn.
                var reapplied = await Task.Run(() => FixService.ReapplyRemembered(folderName, sureBefore));
                if (reapplied > 0)
                {
                    _activityLog.Log($"Made {reapplied} earlier fix(es) again in '{item.Name}' after refreshing it.", ActivityEntryKind.Info);
                }

                // The fresh conversion (and those fixes) can change what the row's badge says.
                await RefreshHealthAsync(item);
            }
            catch (Exception ex)
            {
                _activityLog.Log($"Couldn't refresh '{item.Name}' against updated game data: {ex.Message}", ActivityEntryKind.Warning);
            }
            finally
            {
                _foldersCurrentlyConvertingToExmod.Remove(item.FolderName);
            }
        }

        // 3. Mods built from scale rules (the editor's Scale rules view, the Quality of life
        // template) rebuild their rule rows from the new game data — the whole point of keeping
        // rules instead of frozen copies. Rules own only their own fields, so hand edits stay; a mod
        // open in the editor is left for its own Apply/Save, same guard as above.
        var rulesRefreshedCount = 0;
        var gameTables = new GameTableIndex(_dataFolder);
        foreach (var item in AllLibraryItems().Where(i => !i.IsOpaquePak && !foldersWithOpenEditors.Contains(i.FolderName)))
        {
            try
            {
                var folder = _repository.GetFolderPath(item.FolderName);
                var package = ExmodFolder.Read(folder).Package;
                if (package.ScaleRules.Count == 0)
                {
                    continue;
                }

                var before = ExmodJson.Serialize(package, indented: false);
                ScaleRules.ApplyToPackage(package, package.ScaleRules, _dataFolder, new MergeReport(), gameTables);
                if (ExmodJson.Serialize(package, indented: false) == before)
                {
                    continue;
                }

                ExmodFolder.Write(folder, new ExmodPackageContents(package, []));
                rulesRefreshedCount++;
            }
            catch (Exception ex)
            {
                _activityLog.Log($"Couldn't rebuild '{item.Name}' from its scale rules after updating game data: {ex.Message}", ActivityEntryKind.Warning);
            }
        }

        if (rulesRefreshedCount > 0)
        {
            _repository.Refresh();
        }

        if (convertedCount > 0 || refreshedCount > 0 || rulesRefreshedCount > 0)
        {
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            var parts = new List<string>();
            if (convertedCount > 0)
            {
                parts.Add($"converted {convertedCount} newly-convertible mod(s)");
            }

            if (refreshedCount > 0)
            {
                parts.Add($"refreshed {refreshedCount} mod(s) against the updated game data");
            }

            if (rulesRefreshedCount > 0)
            {
                parts.Add($"rebuilt {rulesRefreshedCount} mod(s) from their scale rules");
            }

            _activityLog.Log($"After updating game data: {string.Join(", ", parts)}.", ActivityEntryKind.Success);
        }
    }

    /// <summary>Takes the user to this mod in the community Database (the Database page's IMM Database tab), pre-searched by name.</summary>
    [RelayCommand]
    private void FindInDatabase(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        Downloads.CatalogSearchText = item.Name;
        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("imm-database"));
    }

    /// <summary>
    /// One action over the update list the last check produced. Database-sourced mods update in
    /// place (the same delete-then-reimport GetUpdateAsync does), one after another. Nexus-sourced
    /// ones can't be fetched by this app without a file ID, so instead of opening a browser tab per
    /// mod the Nexus page is shown with its "Updates only" filter on — every outdated mod's own
    /// Download button is right there.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task UpdateAllAsync()
    {
        var outdated = AllLibraryItems().Where(i => i.HasUpdateAvailable).ToList();
        if (outdated.Count == 0)
        {
            StatusMessage = "Nothing to update — run Check for updates first, or everything is already current.";
            return;
        }

        var databaseItems = outdated.Where(i => string.Equals(i.Source, "Database", StringComparison.OrdinalIgnoreCase)).ToList();
        var nexusItems = outdated.Where(i => string.Equals(i.Source, "Nexus", StringComparison.OrdinalIgnoreCase)).ToList();

        var updated = 0;
        foreach (var item in databaseItems)
        {
            await GetUpdateAsync(item);
            if (!item.HasUpdateAvailable)
            {
                updated++;
            }
        }

        if (nexusItems.Count > 0)
        {
            var nexus = _nexusCatalogViewModel();
            nexus.SearchText = "";
            nexus.UpdatesOnly = true;
            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
        }

        // Named here, not promised on the Nexus page: its "updates only" filter narrows only the page
        // of results it's on, so an outdated mod on another page wouldn't show there.
        var nexusNames = string.Join(", ", nexusItems.Select(i => i.Name));
        if (nexusItems.Count > 0)
        {
            _activityLog.Log($"Nexus mod(s) with updates to download: {nexusNames}.", ActivityEntryKind.Info);
        }

        StatusMessage = (databaseItems.Count, nexusItems.Count) switch
        {
            (> 0, 0) => $"Updated {updated} of {databaseItems.Count} catalog mod(s).",
            (0, > 0) => $"{nexusItems.Count} Nexus mod(s) have updates: {nexusNames}. Download each from its Nexus page (right-click › Open on Nexus).",
            _ => $"Updated {updated} of {databaseItems.Count} catalog mod(s); {nexusItems.Count} Nexus mod(s) have updates: {nexusNames}. Download each from its Nexus page (right-click › Open on Nexus).",
        };
    }

    /// <summary>The spec's "Nexus search from a library row" — searches Nexus for this mod by name, for a mod that has no Nexus link yet.</summary>
    [RelayCommand]
    private void SearchNexusFor(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _nexusCatalogViewModel().SearchText = item.Name;
        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
    }

    /// <summary>
    /// "See what the author changed" — compares this mod's most recent backup (its previous
    /// version, whether that backup came from an update or a manual Create mod backup) against
    /// what's installed now. Read-only: nothing is restored, moved, or written.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CompareToPreviousVersionAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await ShowVersionComparisonAsync(item.Name, item.FolderName, item.FolderName);
    }

    /// <summary>Asks first, then shows — the post-update half of the same comparison the context menu offers on demand.</summary>
    private async Task OfferVersionComparisonAsync(string modName, string backupFolderName, string currentFolderName)
    {
        var answer = _dialogService.Confirm(
            $"'{modName}' was updated.\n\nSee what the author changed between your old version and this one?",
            "Update installed", ThemedConfirmSeverity.Information);
        if (answer)
        {
            await ShowVersionComparisonAsync(modName, backupFolderName, currentFolderName);
        }
    }

    /// <summary>
    /// Two separate folder names on purpose, not one: <paramref name="backupFolderName"/> is where
    /// TryGetLatestModBackupPath looks for the earlier copy (the name it was backed up under),
    /// <paramref name="currentFolderName"/> is where GetFolderPath looks for the current one (the
    /// name it lives under on disk right now) — a post-update reimport can land on a different
    /// folder name than it started with, so these two can genuinely differ. CompareToPreviousVersionAsync
    /// passes the same name for both, since nothing has renamed the folder in that path.
    /// </summary>
    private async Task ShowVersionComparisonAsync(string modName, string backupFolderName, string currentFolderName)
    {
        var previousVersionPath = _repository.TryGetLatestModBackupPath(backupFolderName);
        if (previousVersionPath is null)
        {
            StatusMessage = $"There's no earlier copy of '{modName}' to compare against — this app only has one once it's updated or backed up (right-click → Create mod backup).";
            return;
        }

        StatusMessage = $"Comparing '{modName}' against its previous version…";
        try
        {
            var result = await _modVersionComparer.CompareAsync(
                previousVersionPath, _repository.GetFolderPath(currentFolderName), _settingsService.Current.UnrealPakExePath);

            var window = new ModVersionCompareWindow(new ModVersionCompareViewModel(modName, result))
            {
                Owner = Application.Current.MainWindow,
            };
            window.Show();

            StatusMessage = result.IsIdentical
                ? $"'{modName}' is identical to its previous version."
                : $"Showing what changed in '{modName}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't compare versions: {ex.Message}";
        }
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

        var confirmResult = _dialogService.Confirm(
            $"This replaces '{item.Name}''s current content with its most recent backup — any edit made since then is lost.\n\nContinue?",
            "Restore backup", ThemedConfirmSeverity.Question);
        if (!(confirmResult))
        {
            return;
        }

        try
        {
            var restored = _repository.RestoreLatestModBackup(item.FolderName);
            StatusMessage = restored ? $"Restored '{item.Name}' from its latest backup." : $"No backup exists yet for '{item.Name}'.";
            if (restored)
            {
                _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
                Reload();
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

        if (Downloads.FindOriginalDownload(item.FolderName) is not { } download)
        {
            StatusMessage = $"ISL doesn't have the original download for '{item.Name}' (it was imported from a file, or the download was discarded). Import that file again, or use Restore latest backup.";
            return;
        }

        if (!_dialogService.Confirm(
                $"Reinstall '{item.Name}' from its original download ({download.FileName})?\n\nThis replaces the Library copy: fixes, edits and a rename made since are lost, and its remembered fix choices are forgotten. It's backed up first, so Restore latest backup can bring this copy back.",
                "Reinstall original", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        var name = item.Name;
        try
        {
            _repository.BackupMod(item.FolderName);
            var folderName = item.FolderName;
            await Task.Run(() => FixService.Forget(folderName));
            await Downloads.ActivatePendingDownloadCommand.ExecuteAsync(download);
            StatusMessage = Downloads.PendingDownloadStatusMessage is { Length: > 0 } result ? $"Reinstalled '{name}': {result}" : $"Reinstalled '{name}'.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reinstall failed: {ex.Message}";
            _activityLog.Log($"Reinstall of '{name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Manually connects a Library entry (however it got here — a manual Import, or an unmatched
    /// entry from a future bulk import) to a real Nexus mod ID, so it starts participating in
    /// update-checking like anything activated through the real nxm:// pipeline. Best-effort
    /// enrichment afterward (real name/author/version via the API) mirrors
    /// DownloadsViewModel.EnrichOpaquePakFromNexusAsync's own two-step shape — a rejected/missing
    /// key just means the ID link itself still succeeds without the extra display data.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task LinkToNexus(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var dialog = new LinkNexusDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await LinkFolderToNexusAsync(item.FolderName, item.Name, dialog.NexusModId);
    }

    /// <summary>Shared by LinkToNexus (the context-menu action on an already-imported mod) and ImportPak's own post-import prompt — records the ID, then best-effort-enriches real name/author/version from the API (a rejected/missing key just means the ID link itself still succeeds without the extra display data).</summary>
    private async Task LinkFolderToNexusAsync(string folderName, string displayName, int nexusModId)
    {
        try
        {
            _repository.LinkToNexus(folderName, nexusModId);

            var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
            if (apiKey is not null)
            {
                try
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", nexusModId);
                    if (info is not null)
                    {
                        _repository.SetNexusMetadata(folderName, info.Name, info.Author, info.Summary, info.Version);
                    }
                }
                catch (Exception)
                {
                    // Best-effort — the ID link above already succeeded regardless.
                }
            }

            StatusMessage = $"Linked '{displayName}' to Nexus mod #{nexusModId}.";
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't link to Nexus: {ex.Message}";
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
            Reload();
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

    /// <summary>Mods the game has (from the last Merge &amp; Install) that aren't ticked: Merge &amp; Install would take them out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTickWhatsInTheGame), nameof(TickWhatsInTheGameLabel))]
    private int _inGameNotTickedCount;

    /// <summary>Several in-game mods unticked at once: most likely the ticks were lost, not undone one by one.</summary>
    public bool CanTickWhatsInTheGame => InGameNotTickedCount > 1;

    public string TickWhatsInTheGameLabel => $"Tick the {InGameNotTickedCount} mods in the game";

    /// <summary>Ticks again what the game's merged pak was built from, in its merge order.</summary>
    [RelayCommand]
    private void TickWhatsInTheGame()
    {
        var mergeInstall = TryGetMergeInstall();
        var ticked = mergeInstall?.RestoreQueueFromGame() ?? 0;
        StatusMessage = ticked > 0
            ? $"Ticked the {ticked} mod(s) that are in the game, in the order they were merged. Nothing changes in the game until Merge & Install."
            : "Couldn't find the mods the game's merged pak was built from in your Library.";
        RefreshEnabledAndStatus();
    }

    /// <summary>The game's recorded changes from recent Refresh game data runs; set by the app (old values a mod may still hold).</summary>
    public Func<IEnumerable<FieldChange>>? GameHistory { get; init; }

    private ModFixService? _modFixService;

    /// <summary>Where each mod's remembered fix choices are kept; set by the app (null: nothing remembered).</summary>
    public string? FixDecisionsDirectory { get; init; }

    private ModFixService FixService => _modFixService ??= new ModFixService(_repository, _dataFolder, GameHistory, FixDecisionsDirectory);

    /// <summary>
    /// The row's warning badge, and right-click › Check against the game &amp; fix…: the fix window,
    /// listing what in this mod doesn't fit the current game with a fix for each. Checks the mod
    /// now when no whole-library check has yet. Not while a whole-library check or Fix sure
    /// problems is running (they write mods too), and not while the mod is open in the editor:
    /// the fix window is modal, so the editor couldn't be closed from behind it.
    /// </summary>
    [RelayCommand]
    private void OpenModFix(LibraryItemViewModel? item)
    {
        if (item is null || item.IsOpaquePak)
        {
            return;
        }

        if (ModFixBlockedReason(item) is { } blocked)
        {
            StatusMessage = blocked;
            return;
        }

        if (!Directory.Exists(_dataFolder) || !Directory.EnumerateFiles(_dataFolder, "*.json", SearchOption.AllDirectories).Any())
        {
            StatusMessage = "No game data found — run Refresh game data in Settings first.";
            return;
        }

        ModFixViewModel? viewModel = null;
        try
        {
            var folderName = item.FolderName;
            var report = FixService.Check(folderName);
            item.SetHealth(report);
            viewModel = new ModFixViewModel(folderName, item.Name, report, FixService, () => _openEditorCountByFolder.ContainsKey(folderName));
            var window = new ModFixWindow(viewModel) { Owner = Application.Current.MainWindow };
            viewModel.OpenInModEditor = () =>
            {
                var first = report.Issues.FirstOrDefault();
                window.Close();
                OpenEditor(folderName, first is null ? null : (first.CurrentFile, first.ItemName));
            };

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't check '{item.Name}': {ex.Message}";
        }

        if (viewModel is not null)
        {
            AfterModFixClosed(item, viewModel);
        }
    }

    /// <summary>Why the fix window can't open for this mod right now (null: it can).</summary>
    internal string? ModFixBlockedReason(LibraryItemViewModel item)
    {
        if (IsCheckingStaleness)
        {
            return "Mods are being checked or fixed against the game right now. Try again when that's done.";
        }

        if (_foldersCurrentlyConvertingToExmod.Contains(item.FolderName))
        {
            return $"'{item.Name}' is being refreshed against the game data right now. Try again in a moment.";
        }

        return _openEditorCountByFolder.ContainsKey(item.FolderName)
            ? $"'{item.Name}' is open in the mod editor. Close it there first, so its next Save doesn't undo the fixes."
            : null;
    }

    /// <summary>
    /// After the fix window closes, however it closed: the badge is checked again (Restore backup
    /// and Forget change what's flagged too), and only fixes actually made are logged as such
    /// ("Keep all as they are" applies nothing; it only remembers the choices).
    /// </summary>
    internal void AfterModFixClosed(LibraryItemViewModel item, ModFixViewModel viewModel)
    {
        try
        {
            item.SetHealth(FixService.Check(item.FolderName));
        }
        catch (Exception)
        {
            // The badge keeps what the window opened with.
        }

        var changedOnDisk = viewModel.AppliedCount > 0 || viewModel.ChangedWithoutApply;
        if (changedOnDisk)
        {
            item.NotifyBackupStateChanged();
            item.NotifyRepairedFromDisk();
            if (ReferenceEquals(item, SelectedItem))
            {
                item.EnsureDetailsLoaded();
            }
        }

        if (viewModel.IsApplied && viewModel.StatusMessage is { } status)
        {
            StatusMessage = status;
        }

        if (viewModel.AppliedCount > 0)
        {
            _activityLog.Log($"Fixed {viewModel.AppliedCount} thing(s) in '{item.Name}' to fit the current game (backed up first).", ActivityEntryKind.Success);
        }
    }

    /// <summary>
    /// The automatic part: every sure fix (old copies of game data, values that already match the
    /// game, certain renames) in every mod the last check flagged, each backed up first. What needs
    /// a look stays flagged for the fix window.
    /// </summary>
    [RelayCommand]
    private async Task FixSureProblemsAsync()
    {
        if (IsCheckingStaleness)
        {
            return;
        }

        var flagged = AllLibraryItems().Where(i => i.Health is { SureCount: > 0 } && !i.IsOpaquePak).ToList();
        if (flagged.Count == 0)
        {
            StalenessCheckStatusMessage = "Nothing sure to fix. Run Check mods against game data first, or click a flagged row's warning badge.";
            return;
        }

        var folders = flagged.Select(i => i.FolderName).ToList();
        // Mods whose editor is open are skipped, checked again right before each one is written
        // (an editor can be opened while the pass runs): the editor's next Save would undo the fixes.
        var skippedFolders = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        IsCheckingStaleness = true;
        try
        {
            var results = await Task.Run(() =>
            {
                var cache = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
                var history = FixService.History();
                var addedRows = FixService.LibraryAddedRows();
                var outcome = new Dictionary<string, (int Applied, ModHealthReport After)>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in folders)
                {
                    if (IsEditorOpenLive(folder))
                    {
                        skippedFolders[folder] = true;
                        continue;
                    }

                    try
                    {
                        var (applied, _) = FixService.FixSure(folder, cache, history, addedRows);
                        outcome[folder] = (applied, FixService.Check(folder, cache, history, addedRows));
                    }
                    catch (Exception)
                    {
                        // One unreadable mod doesn't stop the others.
                    }
                }

                return outcome;
            });

            var total = 0;
            var openedDuringPass = new List<string>();
            foreach (var item in flagged)
            {
                if (results.TryGetValue(item.FolderName, out var result))
                {
                    item.SetHealth(result.After);
                    if (result.Applied > 0)
                    {
                        total += result.Applied;
                        item.NotifyBackupStateChanged();
                        item.NotifyRepairedFromDisk();
                        _activityLog.Log($"Fixed {result.Applied} thing(s) in '{item.Name}' to fit the current game (backed up first).", ActivityEntryKind.Success);

                        // Opened in the editor after this mod's own check but before the pass ended:
                        // that editor still holds the mod as it was, and its Save would undo these.
                        if (_openEditorCountByFolder.ContainsKey(item.FolderName))
                        {
                            openedDuringPass.Add(item.Name);
                        }
                    }
                }
            }

            var skipped = flagged.Where(i => skippedFolders.ContainsKey(i.FolderName)).Select(i => i.Name).ToList();
            var failed = flagged.Where(i => !skippedFolders.ContainsKey(i.FolderName) && !results.ContainsKey(i.FolderName)).Select(i => i.Name).ToList();
            var left = flagged.Count(i => i.HasModIssues);
            StalenessCheckStatusMessage = $"Fixed {total} sure thing(s) in {results.Count(r => r.Value.Applied > 0)} mod(s), each backed up first (right-click a mod › Restore latest backup undoes it)."
                + (left > 0 ? $" {left} mod(s) still have things to look over: click a row's warning badge." : "")
                + (skipped.Count > 0 ? $" Skipped (open in the mod editor): {string.Join(", ", skipped)}." : "")
                + (failed.Count > 0 ? $" Couldn't read: {string.Join(", ", failed)}." : "")
                + (openedDuringPass.Count > 0 ? $" Opened in the mod editor while fixing: {string.Join(", ", openedDuringPass)} — close that editor without saving, or its Save undoes the fixes." : "");

            if (total > 0)
            {
                // Like Apply and the editor's Save: the rest of the app (merge state, details) re-reads the rewritten mods.
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }

            SelectedItem?.EnsureDetailsLoaded();
        }
        finally
        {
            IsCheckingStaleness = false;
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

    /// <summary>Deletes every mod in the Library, regardless of any current selection — its own menu item since this is a much larger blast radius than the ordinary per-selection Delete above; DeleteMods' own count-naming confirmation still gates it.</summary>
    [RelayCommand]
    private void DeleteAll() => DeleteMods([.. AllLibraryItems()]);

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
            .Where(i => i.NexusModId is not null)
            .Select(i => _packagesByNexusModId.GetValueOrDefault(i.NexusModId!.Value))
            .Where(p => p is not null)
            .Select(p => p!)
            .DistinctBy(p => p.NexusModId)
            .ToList();
        var siblingItems = AllLibraryItems()
            .Where(i => !selectedFolders.Contains(i.FolderName) && packages.Any(p => p.LibraryFolderNames.Contains(i.FolderName, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var ue4ssParts = packages.SelectMany(p => p.Ue4ssFolderNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (siblingItems.Count > 0)
        {
            items = [.. items, .. siblingItems];
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
            message = $"Delete '{package.DisplayName}' and all {package.PartCount} of its parts?\n\n{string.Join("\n", partLines)}\n\nThe folders are removed from disk; an enabled UE4SS part is taken out of the game's Mods folder (backed up first). Run Merge & Install afterwards to drop the paks from the game. This can't be undone (unless you made a backup first).";
        }
        else
        {
            message = items.Count == 1
                ? $"Delete '{items[0].Name}' from your Library?\n\nIts folder is removed from disk. Run Merge & Install afterwards to drop it from the game. This can't be undone (unless you made a backup first)."
                : $"Delete {items.Count} mods from your Library?\n\nTheir folders are removed from disk. Run Merge & Install afterwards to drop them from the game. This can't be undone (unless you made a backup first).";
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
            // Cancel first: a pending debounced notes save firing after Delete() below could write
            // this entry's metadata into a different mod that reuses the same freed folder name.
            item.CancelPendingSave();
            try
            {
                _repository.Delete(item.FolderName);
                // Purges this mod's saved original pak (if it has one — a no-op otherwise) so a
                // deleted mod doesn't leave an orphaned copy under PrebuiltPakSources forever; mirrors
                // how the repository already purges its own LibraryMeta sidecar record on Delete.
                _prebuiltPakSourceStore.Delete(item.FolderName);
                // Same reasoning, for this mod's own cached opaque-pak preview extraction (if any
                // was ever populated — see DecodeOpaquePakAssetPreviewAsync). Unlike the small,
                // deliberately-never-purged Thumbnails cache just below, this cache can be as
                // large as the mod's own .pak itself, so — like PrebuiltPakSources above — it
                // shouldn't survive as an orphaned copy forever once the mod it came from is gone.
                var pakPreviewCacheDirectory = Path.Combine(_pakPreviewCacheDirectory, item.FolderName);
                if (Directory.Exists(pakPreviewCacheDirectory))
                {
                    Directory.Delete(pakPreviewCacheDirectory, recursive: true);
                }
                deletedCount++;
                if (SelectedItem == item)
                {
                    SelectedItem = null;
                }

                _activityLog.Log($"Deleted '{name}'.", ActivityEntryKind.Info);
            }
            catch (Exception ex)
            {
                // Same UI boundary as TryImport: deleting a folder that's locked by another
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
        Reload();
        if (ue4ssParts.Count > 0)
        {
            ReloadInstalledUe4ssMods();
        }

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
    }

    /// <summary>
    /// Tries converting the pak into a real, editable EXMOD first (IPrebuiltPakImporter, which
    /// itself falls back to importing it as an opaque pak whenever conversion isn't possible right
    /// now — UnrealPak.exe not set up, the extracted game data missing, or the pak itself can't be
    /// read). Async because conversion genuinely extracts and diffs the pak, unlike TryImport's
    /// synchronous importer delegate below.
    /// </summary>
    private async Task<LibraryEntry?> TryImportPakAsync(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
    {
        try
        {
            var entry = await _prebuiltPakImporter.ImportAsync(
                pakFilePath, _dataFolder, _settingsService.Current.UnrealPakExePath, source, nexusModId, catalogEntryId);

            StatusMessage = entry.IsOpaquePak
                ? $"Imported '{entry.Name}'."
                : $"Imported '{entry.Name}' as a real, editable mod — its data changes are now visible in the mod editor.";
            LogImportWarnings(pakFilePath, entry.ImportWarnings);
            StatusMessage += ExtractedModClassifier.WarningSummary(entry.ImportWarnings);
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
            return entry;
        }
        catch (Exception ex)
        {
            // Same UI-boundary catch TryImport itself uses — a user-initiated import can fail for
            // many reasons (malformed pak, permission denied, disk full, ...).
            StatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log($"Import of '{Path.GetFileName(pakFilePath)}' failed: {ex.Message}", ActivityEntryKind.Warning);
            return null;
        }
    }

    /// <summary>
    /// Shared by ImportFolder/ImportFile — same try/catch/status/reload shape, differing only in
    /// which repository method actually reads sourcePath. Returns the imported entry (null on
    /// failure, already reported via StatusMessage). A .pak import goes through TryImportPakAsync
    /// above instead, not this — it needs to attempt conversion first, which this synchronous
    /// importer delegate shape can't express.
    /// </summary>
    private LibraryEntry? TryImport(string sourcePath, Func<string, LibraryEntry> importer)
    {
        try
        {
            var entry = importer(sourcePath);
            StatusMessage = $"Imported '{entry.Name}'.";
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
            return entry;
        }
        catch (Exception ex)
        {
            // A user-initiated import can fail for many reasons (malformed archive, permission
            // denied, disk full, ...) — this is the UI boundary where any of them should show a
            // friendly message instead of crashing the app.
            StatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log($"Import of '{Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}' failed: {ex.Message}", ActivityEntryKind.Warning);
            return null;
        }
    }

    /// <summary>
    /// fullResync is true only for the explicit Refresh() command — every other caller (the
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
        _packagesByNexusModId = BuildPackages();

        // A folder that's visibly on disk but unreadable (corrupt EXMOD, locked file) was only
        // ever explained in the log file — surfaced here so "why isn't my mod showing?" has an
        // answer in the UI itself.
        UnreadableFoldersMessage = _repository.UnreadableFolders.Count > 0
            ? $"{_repository.UnreadableFolders.Count} folder(s) couldn't be read and aren't shown: {string.Join(", ", _repository.UnreadableFolders)}. Check the mod's own files (a corrupt .EXMOD is the usual cause), or see the log."
            : null;

        var seenFolders = new HashSet<string>();
        var seenGroupKeys = new HashSet<string>();
        var targetRootItems = new List<object>();

        // A download in flight shows as a stub row at the top — no folder on disk yet to have
        // become a real LibraryEntry, so it can't come from _repository.Search above. It disappears
        // the moment the download finishes (success or failure) via the tracker's own CollectionChanged
        // triggering another Reload — a successful one is replaced by the real imported entry via
        // the LibraryChangedMessage DownloadsViewModel already sends right after activating it.
        foreach (var download in _activeDownloadsTracker.Current)
        {
            targetRootItems.Add(new DownloadStubViewModel(download.DisplayName));
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
                seenFolders.Add(item.FolderName);
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
            ? Ue4ssMods.Where(row => !row.IsLinkedToLibraryMod && MatchesSearch(row)).Select(row => (SortName: row.Name, Pinned: false, Row: (object)row)).ToList()
            : [];
        if (SortColumn is null)
        {
            targetRootItems.AddRange(libraryRows.Where(r => r.Pinned).Select(r => r.Row));
            targetRootItems.AddRange(libraryRows.Where(r => !r.Pinned).Concat(ue4ssRows).OrderBy(r => r.SortName, StringComparer.OrdinalIgnoreCase).Select(r => r.Row));
        }
        else if (SortColumn == "Name")
        {
            var all = libraryRows.Concat(ue4ssRows);
            targetRootItems.AddRange((SortDescending ? all.OrderByDescending(r => r.SortName, StringComparer.OrdinalIgnoreCase) : all.OrderBy(r => r.SortName, StringComparer.OrdinalIgnoreCase)).Select(r => r.Row));
        }
        else
        {
            targetRootItems.AddRange(libraryRows.Select(r => r.Row));
            targetRootItems.AddRange(ue4ssRows.OrderBy(r => r.SortName, StringComparer.OrdinalIgnoreCase).Select(r => r.Row));
        }

        Ue4ssCount = ue4ssRows.Count;
        SyncRootItems(targetRootItems);
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
    }

    /// <summary>
    /// Updates RootItems to match `target` via targeted Remove/Insert/Move rather than
    /// Clear()+re-Add(): WPF's TreeView regenerates every container from scratch on the Reset
    /// notification Clear() raises, even for items whose object identity didn't change — which
    /// was collapsing every expanded family (and losing the selection highlight) on every
    /// debounced search reload despite GetOrCreateGroup/GetOrCreateItem already reusing the same
    /// instances. LibraryGroupViewModel.SetItems applies the same fix one level down, for a
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

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task CheckForUpdates() => CheckForUpdatesAsync(isAutomatic: false);

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task CheckModsAgainstCurrentData() => CheckModsAgainstCurrentDataAsync();

    /// <summary>
    /// Fetches this account's whole endorsement history ONCE (GetEndorsementsAsync — there's no
    /// per-mod endpoint that would let this batch any other way) and stamps every Nexus-linked
    /// Library row with its real status — a mod absent from the returned list has never been
    /// touched (Undecided), matching Nexus's own implicit default. A separate, explicit action from
    /// CheckForUpdatesAsync (a different question — version-checking vs. "have I endorsed this"),
    /// same as CheckModsAgainstCurrentDataAsync already is.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CheckEndorsements()
    {
        if (IsCheckingEndorsements)
        {
            return;
        }

        var nexusLinkedItems = AllLibraryItems().Where(i => i.HasNexusLink).ToList();
        if (nexusLinkedItems.Count == 0)
        {
            EndorsementCheckStatusMessage = "No Library mods are linked to a Nexus mod ID yet.";
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            EndorsementCheckStatusMessage = "Sign in with your Nexus API key in Settings first.";
            return;
        }

        IsCheckingEndorsements = true;
        try
        {
            var endorsements = await _nexusApiClient.GetEndorsementsAsync(apiKey);
            var statusByModId = endorsements
                .Where(e => string.Equals(e.DomainName, "icarus", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => e.ModId, e => e.Status);

            var notEndorsedCount = 0;
            foreach (var item in nexusLinkedItems)
            {
                item.EndorsementStatus = statusByModId.GetValueOrDefault(item.NexusModId!.Value, NexusEndorsementStatus.Undecided);
                if (item.EndorsementStatus != NexusEndorsementStatus.Endorsed)
                {
                    notEndorsedCount++;
                }
            }

            EndorsementCheckStatusMessage = notEndorsedCount == 0
                ? $"All {nexusLinkedItems.Count} Nexus-linked mod(s) are endorsed."
                : $"{notEndorsedCount} of {nexusLinkedItems.Count} Nexus-linked mod(s) aren't endorsed yet.";
        }
        catch (Exception ex)
        {
            EndorsementCheckStatusMessage = $"Couldn't check endorsements: {ex.Message}";
        }
        finally
        {
            IsCheckingEndorsements = false;
        }
    }

    /// <summary>One mod's own outcome from a CheckModsAgainstCurrentDataAsync pass — plain data, safe to build off the UI thread and apply afterward.</summary>
    private sealed record ModStalenessResult(
        IReadOnlyList<ExmodStalenessChecker.StaleItem> RemainingStaleItems,
        string? SuggestionHint,
        IReadOnlyList<string> AutoFixActivityMessages,
        bool BackupTaken,
        bool PackageChanged = false,
        ModHealthReport? Health = null,
        bool HealthCheckFailed = false);

    /// <summary>
    /// A separate question and a separate action from CheckForUpdatesAsync above (that's Nexus/
    /// catalog version-string checking) — this asks "does this mod's own data still match what the
    /// currently-extracted game data actually defines", independent of any catalog or version
    /// string. Explicit action (a toolbar button), not automatic on every Library load, since a
    /// whole-library base-vs-modded diff is real work a routine navigation shouldn't pay for.
    ///
    /// Beyond detection, this also attempts a real fix for anything StaleItemFixSuggester is
    /// confident enough about: it backs the mod up first (the same BackupMod this page's own
    /// context menu already offers — one click to undo via Restore latest backup), renames the
    /// item to the suggested real row, writes the mod back out, and marks it locally edited —
    /// never silently; every auto-fix is logged to the Activity panel by name. Anything less than
    /// fully confident is left alone and only named as a hint in the row's own tooltip.
    /// </summary>
    private async Task CheckModsAgainstCurrentDataAsync()
    {
        // Real file mutation happens inside this pass (backup + rewrite an auto-fixed mod's own
        // .EXMOD) — unlike CheckForUpdatesAsync's own read-only network calls, letting two passes
        // run at once risks two threads writing the same mod's .EXMOD concurrently. Checked here,
        // not via [RelayCommand]'s CanExecute, since IsCheckingStaleness itself is what's guarded.
        if (IsCheckingStaleness)
        {
            return;
        }

        // Opaque .pak entries have no .EXMOD to diff — same exclusion EnsureDetailsLoaded's own
        // ChangesContent formatting already applies.
        var candidates = AllLibraryItems().Where(i => !i.IsOpaquePak).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        // Without this, a Data folder that's missing, empty, or was never extracted makes every
        // mod's own base-file lookup fail silently (ExmodBaseDiffer.DiffAgainstBase is called with
        // no MergeReport here, so its own "no matching base file" warnings go nowhere) — every mod
        // comes back with zero stale items, and the status message below would read "No possibly
        // stale items remain," a false all-clear. Same check, same wording, ExmodEditorViewModel's
        // own game-data search already uses for the identical underlying condition.
        if (!Directory.Exists(_dataFolder) || !Directory.EnumerateFiles(_dataFolder, "*.json", SearchOption.AllDirectories).Any())
        {
            StatusMessage = "No game data found — run Refresh game data in Settings first.";
            return;
        }

        IsCheckingStaleness = true;
        try
        {
            var folderNames = candidates.Select(i => i.FolderName).ToList();
            // Snapshotted here, on the UI thread, before the Task.Run below reads it from a
            // background thread — _openEditorCountByFolder is only ever mutated on the UI thread
            // (OpenEditor/its window's Closed handler), so a live, unsynchronized read of it from
            // that background thread would itself be a new race. See its own doc comment for why
            // this exists at all.
            var foldersWithOpenEditors = _openEditorCountByFolder.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Off the UI thread: this diffs every non-opaque mod's own items against the real
            // extracted game data (and, for anything confidently fixable, backs up + rewrites the
            // mod's own .EXMOD), which for a large library is real, avoidable-to-block-on work —
            // same reasoning RebuildService's own merge computation was moved to Task.Run for.
            // Results are collected into a plain dictionary and applied to the rows/ActivityLog back
            // on this (UI) thread afterward, since neither an ObservableProperty setter nor
            // IActivityLog.Log's own bound ObservableCollection is safe to touch from a background
            // thread.
            var resultsByFolder = await Task.Run(() =>
            {
                // Shared for the whole pass, not per mod — many mods touch the same real file (e.g.
                // Traits-D_Itemable.json), and re-parsing it once per mod that touches it is real,
                // avoidable cost. Also doubles as the fix suggester's own candidate-row pool per file.
                var baseTableCache = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
                var classifier = new DefaultSemanticClassifier();
                var history = FixService.History();
                _passAddedRows = FixService.LibraryAddedRows();
                var results = new Dictionary<string, ModStalenessResult>();

                foreach (var folderName in folderNames)
                {
                    try
                    {
                        // Looks only: renames are offered (never for a mod's own new rows) and made from the
                        // fix window or Fix sure problems. A silent rename here once turned Homestead's
                        // own Water_Well into the game's Waterwheel.
                        results[folderName] = CheckOneModAgainstCurrentData(
                            folderName, baseTableCache, classifier, allowAutoFix: false, history);
                    }
                    catch (Exception)
                    {
                        // Best-effort per mod, matching CheckForUpdatesAsync's own per-source
                        // isolation — a locked or malformed .EXMOD just means that one mod isn't
                        // checked this pass, not a reason to fail the whole library check.
                    }
                }

                return results;
            });

            var flaggedCount = 0;
            var couldNotCheck = new List<string>();
            var autoFixedCount = 0;
            foreach (var item in candidates)
            {
                if (!resultsByFolder.TryGetValue(item.FolderName, out var result))
                {
                    couldNotCheck.Add(item.Name);
                    continue;
                }

                item.SetStaleItems(result.RemainingStaleItems, result.SuggestionHint);
                if (result.HealthCheckFailed)
                {
                    // Not an all-clear: the badge keeps what it had and the mod is named below.
                    couldNotCheck.Add(item.Name);
                }
                else
                {
                    item.SetHealth(result.Health);
                }

                if (item.Health is { HasProblems: true })
                {
                    flaggedCount++;
                }

                foreach (var message in result.AutoFixActivityMessages)
                {
                    _activityLog.Log(message, ActivityEntryKind.Success);
                    autoFixedCount++;
                }

                if (result.BackupTaken)
                {
                    item.NotifyBackupStateChanged();
                }

                if (result.PackageChanged)
                {
                    // The auto-fix loop above wrote straight to disk and to the repository's own
                    // cache — nothing else re-syncs this bound row, so without this the ✎ badge
                    // wouldn't appear and a currently-open Changes tab would keep showing the
                    // pre-repair item name until an unrelated full resync happened to run.
                    item.NotifyRepairedFromDisk();
                    if (ReferenceEquals(item, SelectedItem))
                    {
                        item.EnsureDetailsLoaded();
                    }
                }
            }

            var messageParts = new List<string>();
            if (autoFixedCount > 0)
            {
                messageParts.Add($"Auto-fixed {autoFixedCount} item(s) — each mod was backed up first, see Restore latest backup to undo.");
            }

            messageParts.Add(flaggedCount > 0
                ? $"{flaggedCount} mod(s) don't fully fit the current game — click a flagged row's warning badge to see and fix them, or Check ▾ › Fix sure problems in every mod."
                : couldNotCheck.Count > 0 ? "Every mod that could be checked fits the current game." : "Every mod fits the current game.");
            if (couldNotCheck.Count > 0)
            {
                messageParts.Add($"Couldn't check {couldNotCheck.Count} mod(s): {string.Join(", ", couldNotCheck)}.");
            }

            StalenessCheckStatusMessage = string.Join(" ", messageParts);
        }
        finally
        {
            IsCheckingStaleness = false;
        }
    }

    /// <summary>
    /// Runs entirely off the UI thread (called from CheckModsAgainstCurrentDataAsync's own
    /// Task.Run) — every repository call here is plain file I/O, safe on a background thread; only
    /// its ModStalenessResult return value ever touches a bound property, back on the UI thread.
    /// allowAutoFix false (an open editor window for this same folder, see
    /// _openEditorCountByFolder's own doc comment) still detects and reports stale items as usual,
    /// it just never backs up or writes anything for this mod this pass.
    /// </summary>
    private ModStalenessResult CheckOneModAgainstCurrentData(
        string folderName, Dictionary<string, JsonObject?> baseTableCache, DefaultSemanticClassifier classifier, bool allowAutoFix,
        GameValueHistory? history = null)
    {
        var folderPath = _repository.GetFolderPath(folderName);
        var package = ExmodFolder.ReadPackageOnly(folderPath);
        var ownAssetPaths = _repository.ListAssetPaths(folderName);

        var staleItems = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, classifier, baseTableCache, ownAssetPaths);
        if (staleItems.Count == 0)
        {
            var (health, healthFailed) = CheckHealth(folderName, baseTableCache, history);
            return new ModStalenessResult([], null, [], BackupTaken: false, Health: health, HealthCheckFailed: healthFailed);
        }

        var remaining = new List<ExmodStalenessChecker.StaleItem>();
        var activityMessages = new List<string>();
        string? suggestionHint = null;
        var backupTaken = false;
        var packageChanged = false;

        foreach (var staleItem in staleItems)
        {
            var row = package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, staleItem.CurrentFile, StringComparison.OrdinalIgnoreCase));
            // LastOrDefault, not First: a mod can legitimately list the same item name more than
            // once (see the field notes on real EXMOD mods) — ExmodBaseDiffer.ToKeyedObject keys by
            // Name and overwrites on each duplicate, so the LAST entry is the one TableDiffer.Diff
            // actually scored (StaleItem.FieldCount). Using the first entry here would judge the fix
            // suggestion's field overlap against fields that were never actually part of the diff.
            IEnumerable<string> fieldNames = row?.FileItems.LastOrDefault(i => i.Name == staleItem.ItemName)?.Fields.Keys ?? Enumerable.Empty<string>();
            var baseTable = baseTableCache.GetValueOrDefault(staleItem.CurrentFile);
            var suggestion = baseTable is null ? null : StaleItemFixSuggester.Suggest(staleItem.ItemName, fieldNames, baseTable);

            if (suggestion is { CanAutoApply: true } && allowAutoFix)
            {
                if (!backupTaken)
                {
                    _repository.BackupMod(folderName);
                    backupTaken = true;
                }

                if (ExmodStaleItemRepair.RenameItem(package, staleItem.CurrentFile, staleItem.ItemName, suggestion.SuggestedItemName))
                {
                    activityMessages.Add(
                        $"Auto-fixed '{staleItem.ItemName}' → '{suggestion.SuggestedItemName}' in '{package.Name}' (backed up first).");
                    packageChanged = true;
                    continue;
                }
            }

            remaining.Add(staleItem);
            suggestionHint ??= suggestion?.SuggestedItemName;
        }

        if (packageChanged)
        {
            ExmodFolder.Write(folderPath, new ExmodPackageContents(package, []));
            _repository.MarkLocallyEdited(folderName);
        }

        var (report, failed) = CheckHealth(folderName, baseTableCache, history);
        return new ModStalenessResult(remaining, suggestionHint, activityMessages, backupTaken, packageChanged, report, failed);
    }

    /// <summary>The rows Library mods add, gathered once per whole-library check (see ModFixService.LibraryAddedRows).</summary>
    private IReadOnlySet<string>? _passAddedRows;

    /// <summary>What in this mod doesn't fit the current game (old copies, mixed lists, renamed rows); Failed when it couldn't be checked (the row keeps its badge).</summary>
    private (ModHealthReport? Report, bool Failed) CheckHealth(string folderName, Dictionary<string, JsonObject?> baseTableCache, GameValueHistory? history)
    {
        try
        {
            return (FixService.Check(folderName, baseTableCache, history, _passAddedRows), false);
        }
        catch (Exception)
        {
            return (null, true);
        }
    }

    /// <summary>Checks one row's mod again off the UI thread and updates its badge; a failed check keeps the badge it had.</summary>
    private async Task RefreshHealthAsync(LibraryItemViewModel item)
    {
        var folderName = item.FolderName;
        try
        {
            item.SetHealth(await Task.Run(() => FixService.Check(folderName)));
        }
        catch (Exception)
        {
            // Only the badge: the next check against game data tries again.
        }
    }

    /// <summary>
    /// Nexus-sourced mods get a real version lookup via the Nexus API (same GetModInfoAsync the
    /// Activate enrichment flow already uses), compared against each mod's own currently-known
    /// Version. Database-sourced mods cross-reference the live Daedalus+Jimk72 catalog by
    /// (Name, Author) — same CatalogKey normalization Export Patch already uses — and compare its
    /// Version field. Neither result is persisted; both are recomputed every call, matching
    /// Downloads' own Nexus watchlist precedent ("a real but coarse signal... not persisted").
    /// isAutomatic (the once-per-launch call from the constructor) suppresses status messages
    /// entirely — no "sign in first" nag just from opening the app, only from an explicit click.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        var nexusItems = AllLibraryItems()
            .Where(i => string.Equals(i.Source, "Nexus", StringComparison.OrdinalIgnoreCase) && i.NexusModId is not null)
            .ToList();
        var databaseItems = AllLibraryItems()
            .Where(i => string.Equals(i.Source, "Database", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ue4ssItems = Ue4ssMods.Where(m => m.HasNexusLink).ToList();

        if (nexusItems.Count == 0 && databaseItems.Count == 0 && ue4ssItems.Count == 0)
        {
            return;
        }

        IsCheckingForUpdates = true;
        try
        {
            var updatedCount = 0;
            var messages = new List<string>();

            if (nexusItems.Count > 0 || ue4ssItems.Count > 0)
            {
                var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
                if (apiKey is null)
                {
                    if (!isAutomatic)
                    {
                        messages.Add("Sign in with your Nexus API key in Settings to check Nexus-sourced mods.");
                    }
                }
                else
                {
                    // Same batched GetModInfoAsync pattern for both collections — a UE4SS mod's own
                    // "current version" is whatever KnownVersion recorded at link time, since
                    // there's no live-installed version string to read the way a Library EXMOD/pak
                    // entry has, but the check itself (fetch, compare, count) is identical.
                    // Mods that know their exact Nexus file are checked against that file's own
                    // update chain first; only the rest fall back to the mod's headline version.
                    var (perFileUpdates, resolvedItems, resolvedRows) = await ApplyNexusFileUpdatesAsync(nexusItems, ue4ssItems, apiKey);
                    updatedCount += perFileUpdates;
                    nexusItems = [.. nexusItems.Where(i => !resolvedItems.Contains(i))];
                    ue4ssItems = [.. ue4ssItems.Where(r => !resolvedRows.Contains(r.Name))];
                    updatedCount += await ApplyNexusVersionsAsync(nexusItems, apiKey, i => i.NexusModId!.Value, (i, v) => i.LatestVersion = v, i => i.HasUpdateAvailable);
                    // Resolved against the LIVE row by name at apply time: a reload of the UE4SS list while
                    // the Nexus lookups were in flight replaces every row, and stamping the version onto
                    // the captured (now orphaned) instance left no badge until the next manual check.
                    updatedCount += await ApplyNexusVersionsAsync(ue4ssItems, apiKey, r => r.NexusModId!.Value,
                        (r, v) => (Ue4ssMods.FirstOrDefault(m => m.Name == r.Name) ?? r).LatestVersion = v,
                        r => (Ue4ssMods.FirstOrDefault(m => m.Name == r.Name) ?? r).HasUpdateAvailable);
                }
            }

            if (databaseItems.Count > 0)
            {
                try
                {
                    // Reuses Downloads' own cached catalog instead of an independent re-fetch of
                    // both sources — see GetUpdateAsync's own comment above for why this is safe;
                    // GetOrFetchCatalogAsync fetches once if the cache is still empty, otherwise
                    // returns immediately.
                    var allEntries = await Downloads.GetOrFetchCatalogAsync();

                    // ID-first, name-fallback — same rename-safe matching Downloads'
                    // ApplyCatalogFilters now uses, for the same reason (a renamed/oddly-named mod
                    // still checks correctly when its stable CatalogEntryId was recorded at
                    // Download & extract time).
                    var catalogVersionById = new Dictionary<string, string>();
                    var catalogVersionByKey = new Dictionary<(string Name, string Author), string>();
                    foreach (var catalogEntry in allEntries)
                    {
                        catalogVersionById[catalogEntry.Id] = catalogEntry.Version;
                        catalogVersionByKey[CatalogKey.Normalize(catalogEntry.Name, catalogEntry.Author)] = catalogEntry.Version;
                    }

                    foreach (var item in databaseItems)
                    {
                        var catalogVersion = item.CatalogEntryId is not null && catalogVersionById.TryGetValue(item.CatalogEntryId, out var byId)
                            ? byId
                            : catalogVersionByKey.GetValueOrDefault(CatalogKey.Normalize(item.Name, item.Author));
                        if (catalogVersion is null)
                        {
                            continue;
                        }

                        item.LatestVersion = catalogVersion;
                        if (item.HasUpdateAvailable)
                        {
                            updatedCount++;
                        }
                    }
                }
                catch (Exception)
                {
                    // Best-effort — a catalog fetch failure just means Database-sourced mods don't
                    // get checked this time, not a reason to fail the whole update check.
                }
            }

            if (!isAutomatic)
            {
                messages.Add(updatedCount > 0 ? $"{updatedCount} mod(s) have an update available." : "Everything checked is up to date.");
                UpdateCheckStatusMessage = string.Join(" ", messages);
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    /// <summary>
    /// Per-file update check: one files request per Nexus mod, then each tracked copy follows its
    /// own file's update chain (NexusFileLineage) — so with several variants of one mod installed or
    /// known, each is judged on its own file. Returns how many have an update and which items/rows
    /// were resolved this way (the rest fall back to the mod-level check).
    /// </summary>
    private async Task<(int Updates, HashSet<LibraryItemViewModel> Items, HashSet<string> Ue4ssNames)> ApplyNexusFileUpdatesAsync(
        IReadOnlyList<LibraryItemViewModel> items, IReadOnlyList<Ue4ssModRowViewModel> rows, string apiKey)
    {
        var resolvedItems = new HashSet<LibraryItemViewModel>();
        var resolvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trackedItems = items.Where(i => i.NexusFileId is not null).ToList();
        var trackedRows = rows.Where(r => r.NexusFileId is not null && r.NexusModId is not null).ToList();
        var modIds = trackedItems.Select(i => i.NexusModId!.Value).Concat(trackedRows.Select(r => r.NexusModId!.Value)).Distinct().ToList();
        if (modIds.Count == 0)
        {
            return (0, resolvedItems, resolvedNames);
        }

        var sets = await Task.WhenAll(modIds.Select(async modId =>
        {
            try
            {
                return (ModId: modId, Set: (NexusModFileSet?)await _nexusApiClient.GetModFileSetAsync(apiKey, "icarus", modId));
            }
            catch (Exception)
            {
                return (ModId: modId, Set: (NexusModFileSet?)null); // best-effort, like the mod-level check
            }
        }));
        var setByMod = sets.Where(s => s.Set is not null).ToDictionary(s => s.ModId, s => s.Set!);

        var updates = 0;
        foreach (var item in trackedItems)
        {
            if (setByMod.TryGetValue(item.NexusModId!.Value, out var set) && NexusFileLineage.Newest(set, item.NexusFileId!.Value) is { } newest)
            {
                item.LatestVersion = newest.Version;
                item.LatestNexusFileId = newest.FileId;
                resolvedItems.Add(item);
                updates += item.HasUpdateAvailable ? 1 : 0;
            }
        }

        foreach (var captured in trackedRows)
        {
            // The live row by name, for the same reason as the mod-level check below.
            var row = Ue4ssMods.FirstOrDefault(m => m.Name == captured.Name) ?? captured;
            if (setByMod.TryGetValue(captured.NexusModId!.Value, out var set) && NexusFileLineage.Newest(set, captured.NexusFileId!.Value) is { } newest)
            {
                row.LatestVersion = newest.Version;
                row.LatestNexusFileId = newest.FileId;
                resolvedNames.Add(captured.Name);
                updates += row.HasUpdateAvailable ? 1 : 0;
            }
        }

        return (updates, resolvedItems, resolvedNames);
    }

    /// <summary>
    /// Batched per-item Nexus version lookup + apply, shared by CheckForUpdatesAsync's Library-mod
    /// and UE4SS-mod checks above — both used to be near-identical copies differing only in which
    /// property/type they touched. Returns how many of the checked items now have an update
    /// available, for the caller's own running total.
    /// </summary>
    private async Task<int> ApplyNexusVersionsAsync<T>(
        IReadOnlyList<T> items, string apiKey, Func<T, int> getNexusModId, Action<T, string> setLatestVersion, Func<T, bool> hasUpdateAvailable)
    {
        var results = await Task.WhenAll(items.Select(async item =>
        {
            try
            {
                var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", getNexusModId(item));
                return (Item: item, Version: info?.Version);
            }
            catch (Exception)
            {
                // Best-effort per-mod — one bad lookup (rate limit, a since-removed mod) shouldn't
                // stop the rest of the batch from checking.
                return (Item: item, Version: (string?)null);
            }
        }));

        var updatedCount = 0;
        foreach (var (item, version) in results)
        {
            if (string.IsNullOrEmpty(version))
            {
                continue;
            }

            setLatestVersion(item, version);
            if (hasUpdateAvailable(item))
            {
                updatedCount++;
            }
        }

        return updatedCount;
    }
}
