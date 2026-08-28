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
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
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
    private readonly INexusApiClient _nexusApiClient;
    private readonly ICredentialStore _credentialStore;
    private readonly Func<string, ExmodEditorViewModel> _editorFactory;
    private readonly IActivityLog _activityLog;
    private readonly IActiveDownloadsTracker _activeDownloadsTracker;
    private readonly HttpClient _downloadHttpClient;
    private readonly IPendingDownloadStore _pendingDownloadStore;
    private readonly IModVersionComparer _modVersionComparer;

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

    /// <summary>The last row clicked without Shift held — a Shift-click ranges from here, the same anchor concept Explorer/ListBox multi-select use. Cleared (never explicitly re-set to null; a stale anchor pointing at a since-deleted row is simply skipped by FlattenModItems' own lookup) whenever a fresh plain/Ctrl click moves it.</summary>
    private LibraryItemViewModel? _bulkSelectionAnchor;

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
    private bool _isCheckingEndorsements;

    [ObservableProperty]
    private string? _endorsementCheckStatusMessage;

    [ObservableProperty]
    private string? _stalenessCheckStatusMessage;

    /// <summary>Ctrl/Shift-click multi-select, for "Add to merge queue" — the only way mods reach Merge & Install's queue now that its own Library pane is gone. A plain list (not a HashSet): insertion order isn't meaningful here, but ObservableCollection gives free CollectionChanged notification for HasBulkSelection/BulkSelectedCount.</summary>
    public ObservableCollection<LibraryItemViewModel> BulkSelectedItems { get; } = [];

    public bool HasBulkSelection => BulkSelectedItems.Count > 0;

    public int BulkSelectedCount => BulkSelectedItems.Count;

    /// <summary>Whether every mod currently shown is bulk-selected — drives the header row's own "select all" checkbox. False (not true) when the list is empty, matching Explorer's own convention of an unchecked header checkbox on an empty list.</summary>
    public bool IsAllSelectedForBulk
    {
        get
        {
            var flattened = FlattenModItems();
            return flattened.Count > 0 && BulkSelectedItems.Count == flattened.Count;
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
        ISettingsService settingsService, IUnrealPakService unrealPakService, INexusApiClient nexusApiClient,
        ICredentialStore credentialStore,
        Func<string, ExmodEditorViewModel> editorFactory, IActivityLog activityLog, HttpClient downloadHttpClient,
        IPendingDownloadStore pendingDownloadStore, IModVersionComparer modVersionComparer,
        DownloadsViewModel downloadsViewModel, Func<NexusCatalogViewModel> nexusCatalogViewModel,
        IActiveDownloadsTracker activeDownloadsTracker, Func<MergeInstallViewModel> mergeInstallViewModel, string backupDirectory,
        string dataFolder)
    {
        _modVersionComparer = modVersionComparer;
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

        BulkSelectedItems.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasBulkSelection));
            OnPropertyChanged(nameof(BulkSelectedCount));
            OnPropertyChanged(nameof(IsAllSelectedForBulk));
        };

        Reload();
        ReloadInstalledUe4ssMods();

        // Constructors can't be async — same "fire it and let the method itself handle every
        // failure" shape DownloadsViewModel's own launch-time Nexus check already uses. Silent:
        // no "sign in first" nag if unconfigured, that only shows on an explicit button click.
        _ = CheckForUpdatesAsync(isAutomatic: true);
    }

    partial void OnSearchTextChanged(string value) => _searchDebounceTimer.Restart();

    partial void OnSelectedItemChanged(LibraryItemViewModel? value) => value?.EnsureDetailsLoaded();

    /// <summary>Ctrl-click: toggles this row's own membership, called from LibraryView's code-behind on a plain (non-Ctrl) click first to clear whatever was previously selected — so a fresh click always starts a new bulk selection rather than adding to a stale one.</summary>
    public void ToggleBulkSelection(LibraryItemViewModel item)
    {
        if (item.IsSelectedForBulk)
        {
            item.IsSelectedForBulk = false;
            BulkSelectedItems.Remove(item);
        }
        else
        {
            item.IsSelectedForBulk = true;
            BulkSelectedItems.Add(item);
        }

        _bulkSelectionAnchor = item;
    }

    public void ClearBulkSelection()
    {
        foreach (var item in BulkSelectedItems)
        {
            item.IsSelectedForBulk = false;
        }

        BulkSelectedItems.Clear();
    }

    /// <summary>Plain click (no modifier): clears any active bulk selection but still moves the anchor to this row, so a later Shift-click ranges from here — matching Explorer's own "any plain click resets the range start" behavior.</summary>
    public void SetBulkSelectionAnchor(LibraryItemViewModel item)
    {
        ClearBulkSelection();
        _bulkSelectionAnchor = item;
    }

    /// <summary>Right-click: a row that's already part of a multi-selection stays part of it (so right-clicking any one of several Ctrl-selected rows adds all of them); right-clicking a row outside the current selection replaces it with just that row — the same convention Explorer uses.</summary>
    public void EnsureBulkSelected(LibraryItemViewModel item)
    {
        if (!item.IsSelectedForBulk)
        {
            ClearBulkSelection();
            item.IsSelectedForBulk = true;
            BulkSelectedItems.Add(item);
        }

        _bulkSelectionAnchor = item;
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
    private List<LibraryItemViewModel> FlattenModItems() =>
        [.. RootItems.SelectMany(entry => entry switch
        {
            LibraryItemViewModel item => (IEnumerable<LibraryItemViewModel>)[item],
            LibraryGroupViewModel group => group.Items,
            _ => [],
        })];

    /// <summary>Shared by Reload's own column-sort switch — SortDescending flips OrderBy/OrderByDescending, generic over TKey so both string columns (StringComparer.OrdinalIgnoreCase) and ImportedAtUtc's own DateTimeOffset share one implementation.</summary>
    private List<LibraryGroup> SortGroups<TKey>(
        IReadOnlyList<LibraryGroup> groups, Func<LibraryGroup, TKey> keySelector, IComparer<TKey>? comparer = null) =>
        [.. (SortDescending ? groups.OrderByDescending(keySelector, comparer) : groups.OrderBy(keySelector, comparer))];

    /// <summary>Shift-click: selects every mod between the last plain/Ctrl-clicked anchor and this one (inclusive), replacing whatever was selected before — matching Explorer's own Shift-click convention. Falls back to selecting just this row alone if there's no anchor yet (e.g. the very first click in a fresh session was a Shift-click).</summary>
    public void SelectBulkRange(LibraryItemViewModel target)
    {
        var flattened = FlattenModItems();
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
            flattened[i].IsSelectedForBulk = true;
            BulkSelectedItems.Add(flattened[i]);
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
        foreach (var item in FlattenModItems())
        {
            item.IsSelectedForBulk = true;
            BulkSelectedItems.Add(item);
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
            Ue4ssStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath);
            var states = _ue4ssModStateService.GetAll(modsFolder);

            // This reload also runs on every LibraryChangedMessage (an unrelated import/delete/link
            // elsewhere in the app, not just this page's own Refresh/Apply) — rebuilding every row
            // from scratch would otherwise silently drop an unsaved pending enable/disable toggle
            // and any already-fetched "Update available" badge each time, neither of which is
            // persisted anywhere else. Snapshot by name first, then reapply onto the fresh rows.
            var previousByName = Ue4ssMods.ToDictionary(m => m.Name);

            Ue4ssMods.Clear();
            foreach (var state in states)
            {
                var meta = _ue4ssModMetaStore.Load(state.Name);
                // IsFrameworkOwned, not membership in ListUserAddedMods — that list only enumerates
                // what's currently IN the game's real Mods folder, so a disabled/staged mod (built-in
                // or not) would silently misclassify as "user-added" by simple absence from it.
                var isBuiltIn = _ue4ssLoaderInstallService.IsFrameworkOwned(_settingsService.Current.IcarusContentPath!, state.Name);
                var row = new Ue4ssModRowViewModel(state.Name, state.IsEnabled, isBuiltIn, meta.NexusModId, meta.NexusVersion, RecomputeHasPendingUe4ssChanges);
                if (previousByName.TryGetValue(state.Name, out var previous))
                {
                    if (previous.IsDirty)
                    {
                        row.IsEnabled = previous.IsEnabled;
                    }

                    row.LatestVersion = previous.LatestVersion;
                }

                Ue4ssMods.Add(row);
            }
            RecomputeHasPendingUe4ssChanges();

            Ue4ssStatusMessage = states.Count == 0 ? "No UE4SS mods found." : null;
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't read the game's UE4SS Mods folder: {ex.Message}";
        }
    }

    private void RecomputeHasPendingUe4ssChanges() => HasPendingUe4ssChanges = Ue4ssMods.Any(m => m.IsDirty);

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

            _ue4ssModStateService.Apply(modsFolder, desired, _backupDirectory);
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = $"Applied {changedCount} change(s).";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Apply failed: {ex.Message}";
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
    [RelayCommand]
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

    [RelayCommand]
    private void ImportUe4ssMod()
    {
        var dialog = new OpenFileDialog { Title = "Select a UE4SS mod zip", Filter = "Zip archive (*.zip)|*.zip" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var folderName = _ue4ssModRepository.Import(dialog.FileName);
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = $"Staged '{folderName}' — enable it below, then click Apply.";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Import failed: {ex.Message}";
        }
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
    [RelayCommand]
    private void ImportFile()
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

        ImportPaths(dialog.FileNames);
    }

    /// <summary>
    /// Imports one already-known path — a real folder, a bare .pak, or (falling through to the
    /// same auto-detection ImportFile's own dialog already uses) any other file, sniffed as an
    /// archive by its actual content rather than trusted by extension. Shared by drag-and-drop and
    /// every multi-select import path below, so "what kind of thing is this path" is decided in
    /// exactly one place.
    /// </summary>
    private void ImportOnePath(string path)
    {
        if (Directory.Exists(path))
        {
            var entry = _repository.Import(path);
            _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
            return;
        }

        if (path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            var entry = _repository.ImportPak(path);
            _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
            return;
        }

        var tempExtractDirectory = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_Import_{Guid.NewGuid():N}");
        try
        {
            AnyArchiveExtractor.ExtractToDirectory(path, tempExtractDirectory);
            var (entryName, folderName, kind, _) = ExtractedModClassifier.ClassifyAndImport(
                tempExtractDirectory, path, _repository, _ue4ssModRepository);
            _activityLog.Log(
                kind == PendingDownloadActivationKind.Library ? $"Imported '{entryName}'." : $"Imported '{folderName}' as a UE4SS mod.",
                ActivityEntryKind.Success);
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
    public void ImportPaths(IReadOnlyList<string> paths)
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
                ImportOnePath(path);
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

        StatusMessage = failures.Count == 0
            ? (paths.Count == 1 ? "Imported 1 mod." : $"Imported {importedCount} mod(s).")
            : $"Imported {importedCount} of {paths.Count} — {string.Join("; ", failures)}";
    }

    /// <summary>
    /// A prebuilt .pak carries no embedded name/author of its own (unlike an EXMOD), so right after
    /// import this offers to link it to a real Nexus mod ID immediately — rather than leaving that
    /// to the separate "Link to Nexus ID…" context-menu action on an already-imported mod, which is
    /// easy to never discover for something imported this way in the first place.
    /// </summary>
    [RelayCommand]
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

        var entry = TryImport(dialog.FileName, path => _repository.ImportPak(path));
        if (entry is null)
        {
            return;
        }

        var linkPrompt = MessageBox.Show(
            $"'{entry.Name}' was imported as an opaque .pak — it has no name/author of its own until you tell it where it came from.\n\nIs this a Nexus mod? Link it now so IcarusStarlink can show its real name and check for updates.",
            "Link to Nexus?", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (linkPrompt != MessageBoxResult.Yes)
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
    private void EditSelected()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (SelectedItem.IsOpaquePak)
        {
            StatusMessage = "An opaque .pak import has no .EXMOD to edit.";
            return;
        }

        try
        {
            OpenEditor(SelectedItem.FolderName);
        }
        catch (Exception ex)
        {
            // Same UI boundary as NewMod's own identical OpenEditor call — the mod's .EXMOD can
            // have gone missing, become ambiguous, or be locked by another process since Library
            // last scanned it.
            StatusMessage = $"Couldn't open the editor: {ex.Message}";
        }
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
            var entry = _repository.CreateBlankMod(dialog.ModName, dialog.ModAuthor);
            StatusMessage = $"Created '{entry.Name}'.";
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            _activityLog.Log($"Created new mod '{entry.Name}'.", ActivityEntryKind.Success);
            OpenEditor(entry.FolderName);
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
    [RelayCommand]
    private async Task GetUpdateAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (string.Equals(item.Source, "Nexus", StringComparison.OrdinalIgnoreCase))
        {
            // Nexus downloads persist in Pending Downloads (MO2-style) — if a file for this mod is
            // already sitting there, activating it is one click closer than a fresh browser trip,
            // so point there instead of the website. (Can't distinguish which version that file is
            // — PendingDownloadEntry carries no version — so this stays a pointer, not an
            // automatic activation of a possibly-older file.)
            if (item.NexusModId is { } nexusModId && _pendingDownloadStore.Entries.Any(e => e.ModId == nexusModId))
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
                        : _repository.ImportPak(tempPath, source: "Database", catalogEntryId: catalogEntry.Id);
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

                    // The backup taken above is the mod's own previous version, so "what did the
                    // author actually change?" is answerable right now — offered rather than shown
                    // automatically, since an update the user just wanted applied shouldn't force a
                    // window open.
                    await OfferVersionComparisonAsync(imported.Name, imported.FolderName);
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

    /// <summary>Takes the user to this mod in the community Database, pre-searched by name — the spec's "open DB from a library row". The IMM Database tab now lives on this same page, so this is just a tab switch, not a cross-page navigation.</summary>
    [RelayCommand]
    private void FindInDatabase(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        Downloads.CatalogSearchText = item.Name;
        SelectedTabIndex = 2;
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
    [RelayCommand]
    private async Task CompareToPreviousVersionAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await ShowVersionComparisonAsync(item.Name, item.FolderName);
    }

    /// <summary>Asks first, then shows — the post-update half of the same comparison the context menu offers on demand.</summary>
    private async Task OfferVersionComparisonAsync(string modName, string folderName)
    {
        var answer = MessageBox.Show(
            $"'{modName}' was updated.\n\nSee what the author changed between your old version and this one?",
            "Update installed", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
        {
            await ShowVersionComparisonAsync(modName, folderName);
        }
    }

    private async Task ShowVersionComparisonAsync(string modName, string folderName)
    {
        var previousVersionPath = _repository.TryGetLatestModBackupPath(folderName);
        if (previousVersionPath is null)
        {
            StatusMessage = $"There's no earlier copy of '{modName}' to compare against — this app only has one once it's updated or backed up (right-click → Create mod backup).";
            return;
        }

        StatusMessage = $"Comparing '{modName}' against its previous version…";
        try
        {
            var result = await _modVersionComparer.CompareAsync(
                previousVersionPath, _repository.GetFolderPath(folderName), _settingsService.Current.UnrealPakExePath);

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

        var confirmResult = MessageBox.Show(
            $"This replaces '{item.Name}''s current content with its most recent backup — any edit made since then is lost.\n\nContinue?",
            "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var restored = _repository.RestoreLatestModBackup(item.FolderName);
            StatusMessage = restored ? $"Restored '{item.Name}' from its latest backup." : $"No backup exists yet for '{item.Name}'.";
            if (restored)
            {
                Reload();
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
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
    [RelayCommand]
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

        var dialog = new RenameModDialog(item.Name) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _repository.SetDisplayNameOverride(item.FolderName, dialog.NewDisplayName);
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't rename: {ex.Message}";
        }
    }

    /// <summary>Phase 10: an inline per-row Edit action (row hover icons) — takes the row's own item directly rather than requiring it to be selected first, unlike EditSelectedCommand.</summary>
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
            StatusMessage = $"Couldn't open the editor: {ex.Message}";
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
    /// it instead of the editor's own default (its first item) — used by OpenStaleItem so the
    /// editor's existing amber base-diff highlighting is what the user sees first, not an unrelated
    /// row.
    /// </param>
    private void OpenEditor(string folderName, (string CurrentFile, string ItemName)? preSelect = null)
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
        window.Show();
    }

    /// <summary>The row's own warning badge — clicking it opens the mod in the editor with the first flagged item pre-selected (see OpenEditor's preSelect param), so the editor's already-existing amber base-diff highlighting does the rest.</summary>
    [RelayCommand]
    private void OpenStaleItem(LibraryItemViewModel? item)
    {
        if (item is null || item.IsOpaquePak)
        {
            return;
        }

        try
        {
            OpenEditor(item.FolderName, item.FirstStaleItem);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open the editor: {ex.Message}";
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
    private void DeleteAll() => DeleteMods([.. _itemsByFolderName.Values]);

    private void DeleteMods(IReadOnlyList<LibraryItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Deleting removes each mod's real folder from disk and can't be undone from here — and a
        // right-click menu is far easier to hit by accident than a dedicated button, so this always
        // asks first, matching how every other irreversible action in this app behaves. Names the
        // real count rather than a generic "these mods" so a fat-fingered bulk selection is obvious
        // before it's too late to back out.
        var confirm = MessageBox.Show(
            items.Count == 1
                ? $"Delete '{items[0].Name}' from your Library?\n\nIts folder is removed from disk. This can't be undone (unless you made a backup first)."
                : $"Delete {items.Count} mods from your Library?\n\nTheir folders are removed from disk. This can't be undone (unless you made a backup first).",
            "Delete mod(s)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var deletedCount = 0;
        var failures = new List<string>();
        foreach (var item in items)
        {
            var name = item.Name;
            // Cancel first: a pending debounced notes save firing after Delete() below could write
            // this entry's metadata into a different mod that reuses the same freed folder name.
            item.CancelPendingSave();
            try
            {
                _repository.Delete(item.FolderName);
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
        StatusMessage = failures.Count switch
        {
            0 when deletedCount == 1 => $"Deleted '{items[0].Name}'.",
            0 => $"Deleted {deletedCount} mod(s).",
            _ => $"Deleted {deletedCount} of {items.Count} — {string.Join("; ", failures)}",
        };
        Reload();
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
    }

    /// <summary>
    /// Shared by ImportFolder/ImportFile/ImportPak — same try/catch/status/reload shape, differing
    /// only in which repository method actually reads sourcePath. Returns the imported entry (null
    /// on failure, already reported via StatusMessage) so a caller like ImportPak can act on it
    /// further — e.g. offering to link it to Nexus — without duplicating this same try/catch body.
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

        foreach (var group in groups)
        {
            var items = group.Entries.Select(entry => GetOrCreateItem(entry, fullResync)).ToList();
            foreach (var item in items)
            {
                seenFolders.Add(item.FolderName);
            }

            if (group.IsFamily)
            {
                seenGroupKeys.Add(group.GroupKey);
                targetRootItems.Add(GetOrCreateGroup(group.GroupKey, group.DisplayName, items));
            }
            else
            {
                targetRootItems.Add(items[0]);
            }
        }

        SyncRootItems(targetRootItems);
        OnPropertyChanged(nameof(IsAllSelectedForBulk));

        // Drop cached instances for mods/families no longer present/matching, so these don't grow
        // forever. FlushPendingSave() first, not CancelPendingSave() — the mod's folder isn't going
        // anywhere here (unlike DeleteSelected), so a pending Notes edit should be saved a little
        // early, not silently discarded; without this, an orphaned timer could fire later and write
        // the edit to disk after a fresh instance for the same mod (built from the now-stale
        // repository read) has already replaced this one, leaving the visible Notes field stale
        // relative to what's actually on disk.
        foreach (var staleFolderName in _itemsByFolderName.Keys.Except(seenFolders).ToList())
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
            entry, _repository, _unrealPakService, _settingsService, _nexusApiClient, _credentialStore, status => StatusMessage = status, () => Reload());
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

    [RelayCommand]
    private Task CheckForUpdates() => CheckForUpdatesAsync(isAutomatic: false);

    [RelayCommand]
    private Task CheckModsAgainstCurrentData() => CheckModsAgainstCurrentDataAsync();

    /// <summary>
    /// Fetches this account's whole endorsement history ONCE (GetEndorsementsAsync — there's no
    /// per-mod endpoint that would let this batch any other way) and stamps every Nexus-linked
    /// Library row with its real status — a mod absent from the returned list has never been
    /// touched (Undecided), matching Nexus's own implicit default. A separate, explicit action from
    /// CheckForUpdatesAsync (a different question — version-checking vs. "have I endorsed this"),
    /// same as CheckModsAgainstCurrentDataAsync already is.
    /// </summary>
    [RelayCommand]
    private async Task CheckEndorsements()
    {
        var nexusLinkedItems = _itemsByFolderName.Values.Where(i => i.HasNexusLink).ToList();
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
        bool PackageChanged = false);

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
        var candidates = _itemsByFolderName.Values.Where(i => !i.IsOpaquePak).ToList();
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
            StatusMessage = "No game data found — run Update data folder in Settings first.";
            return;
        }

        IsCheckingStaleness = true;
        try
        {
            var folderNames = candidates.Select(i => i.FolderName).ToList();

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
                var results = new Dictionary<string, ModStalenessResult>();

                foreach (var folderName in folderNames)
                {
                    try
                    {
                        results[folderName] = CheckOneModAgainstCurrentData(folderName, baseTableCache, classifier);
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
            var autoFixedCount = 0;
            foreach (var item in candidates)
            {
                if (!resultsByFolder.TryGetValue(item.FolderName, out var result))
                {
                    continue;
                }

                item.SetStaleItems(result.RemainingStaleItems, result.SuggestionHint);
                if (result.RemainingStaleItems.Count > 0)
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
                ? $"{flaggedCount} mod(s) still have possibly stale items — click a flagged row's warning badge to review."
                : "No possibly stale items remain.");

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
    /// </summary>
    private ModStalenessResult CheckOneModAgainstCurrentData(
        string folderName, Dictionary<string, JsonObject?> baseTableCache, DefaultSemanticClassifier classifier)
    {
        var folderPath = _repository.GetFolderPath(folderName);
        var package = ExmodFolder.ReadPackageOnly(folderPath);
        var ownAssetPaths = _repository.ListAssetPaths(folderName);

        var staleItems = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, classifier, baseTableCache, ownAssetPaths);
        if (staleItems.Count == 0)
        {
            return new ModStalenessResult([], null, [], BackupTaken: false);
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

            if (suggestion is { CanAutoApply: true })
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

        return new ModStalenessResult(remaining, suggestionHint, activityMessages, backupTaken, packageChanged);
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
        var nexusItems = _itemsByFolderName.Values
            .Where(i => string.Equals(i.Source, "Nexus", StringComparison.OrdinalIgnoreCase) && i.NexusModId is not null)
            .ToList();
        var databaseItems = _itemsByFolderName.Values
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
                    updatedCount += await ApplyNexusVersionsAsync(nexusItems, apiKey, i => i.NexusModId!.Value, (i, v) => i.LatestVersion = v, i => i.HasUpdateAvailable);
                    updatedCount += await ApplyNexusVersionsAsync(ue4ssItems, apiKey, r => r.NexusModId!.Value, (r, v) => r.LatestVersion = v, r => r.HasUpdateAvailable);
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
