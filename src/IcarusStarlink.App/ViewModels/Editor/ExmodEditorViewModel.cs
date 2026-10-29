using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 7.1's core EXMOD editor — deliberately the first transient (per-open-instance) ViewModel
/// in this app; every other ViewModel is a DI singleton. Classic IMM's own editor really is a
/// separate .exe that supports two independent sessions open at once, which this window/ViewModel
/// pairing matches (see the plan's "7.1 — Core editor" section) — LibraryViewModel constructs a
/// fresh instance per Edit… / New mod… click via the Func&lt;string, ExmodEditorViewModel&gt;
/// factory registered in App.xaml.cs, rather than this being resolved as a singleton.
/// </summary>
public sealed partial class ExmodEditorViewModel : ObservableObject
{
    private readonly ILibraryRepository _repository;
    private readonly string _folderName;
    private readonly string _dataFolder;
    private readonly GameDataIndexCache _gameDataIndexCache;
    private readonly ExmodPackage _package;

    /// <summary>
    /// Undo/Redo snapshots (see ExmodUndoHistory for what counts as one change). Field edits
    /// snapshot too (SnapshotBeforeFieldEdit): without them, Undo of a structural action restored
    /// a package from before it and silently wiped every value typed since.
    /// </summary>
    private readonly ExmodUndoHistory _history = new();

    /// <summary>The field typed into since the last snapshot: more typing there needs no new one.</summary>
    private (string CurrentFile, string ItemName, string FieldName)? _lastEditedField;

    /// <summary>The package as it is on disk (when opened, or last saved), for HasUnsavedChanges.</summary>
    private string _savedJson;

    /// <summary>
    /// Recomputed whenever Rows changes shape (construction, or a raw-JSON Apply) — not on every
    /// keystroke or plain item-selection change. Amber-highlight comparisons
    /// (EditorFieldViewModel.IsChanged) work entirely off the base value snapshotted into each
    /// field row when this was last refreshed, so re-diffing against the real base files on every
    /// edit isn't needed.
    /// </summary>
    private Dictionary<(string CurrentFile, string ItemName, string FieldName), JsonNode?> _baseValuesByKey = [];

    /// <summary>
    /// What this mod's own fields held when this editor was opened (or last saved, whichever is
    /// more recent) — a *separate* reference point from _baseValuesByKey's real-game-default
    /// values: "what did I just change in this sitting" vs. "what does this mod change from
    /// vanilla". Deliberately never refreshed by AddItem/AddField/RemoveField/a raw-JSON Apply —
    /// those are exactly the in-session edits this is meant to reveal — only by construction and a
    /// successful Save (at which point the mod's own "original" genuinely becomes whatever was
    /// just written).
    /// </summary>
    private Dictionary<(string CurrentFile, string ItemName, string FieldName), JsonNode?> _originalValuesByKey = [];

    public string WindowTitle => $"Mod editor — {_package.Name}";

    /// <summary>When this window last read or wrote the mod's .EXMOD (see Save).</summary>
    private DateTime _savedStamp;

    private bool _overwriteOutsideChanges;

    /// <summary>Saved at least once in this window: "Turn on & go to Merge & Install" is offered.</summary>
    [ObservableProperty]
    private bool _hasSaved;

    [RelayCommand]
    private void TurnOnAndGoToMerge() => WeakReferenceMessenger.Default.Send(new TurnOnModMessage(_folderName));

    private readonly OwnModWriter _writer;

    private DateTime ExmodStamp() => _writer.StampOf(_folderName, _package.FileName);

    private readonly IReadOnlyList<string> _replacedGameFiles;

    /// <summary>
    /// Shown instead of blank panes when the mod has no data rows yet: a new mod, or a blueprint-only
    /// mod (Ferani's Steadfast Structures replaces BP_Building_Base and has nothing else) — a real
    /// report read the empty editor as broken.
    /// </summary>
    public string? NoDataRowsMessage => _package.Rows.Any(r => r.FileItems.Count > 0) ? null
        : (ExmodChangesFormatter.DescribeReplacedGameFiles(_replacedGameFiles) is { } replaced
            ? $"This mod has no data-table changes. {replaced}\n\nYou can still add data changes to it: New item from a game item…, More ▾ › Copy a game row into this mod…, or Scale rules."
            : "This mod has no data-table changes yet. Add some with New item from a game item…, More ▾ › Copy a game row into this mod…, or Scale rules.");

    public ObservableCollection<EditorItemViewModel> Items { get; } = [];

    /// <summary>The item list's text when it shows nothing: the search found nothing, or there are no items. Null while it has rows.</summary>
    public string? ItemsEmptyText => Items.Count > 0 ? null
        : !string.IsNullOrWhiteSpace(FilterText) ? "No items match the search."
        : "No items yet.";

    /// <summary>The Fields view's prompt while nothing is picked (a mod with no rows says so itself, via NoDataRowsMessage).</summary>
    public bool ShowPickItemPrompt => SelectedItem is null && Items.Count > 0 && NoDataRowsMessage is null;

    public ObservableCollection<EditorFieldViewModel> Fields { get; } = [];

    [ObservableProperty]
    private EditorItemViewModel? _selectedItem;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _newFieldName = "";

    [ObservableProperty]
    private string _newFieldValue = "";

    [ObservableProperty]
    private string _newItemCurrentFile = "";

    [ObservableProperty]
    private string _newItemName = "";

    [ObservableProperty]
    private ExmodEditorViewMode _viewMode = ExmodEditorViewMode.ItemFields;

    // Shared scroll position per view — not per window. When the same view (e.g. Item fields) is
    // open in both the main editor window and a popped-out pane at once, ScrollSyncHelper keeps
    // whichever one the user scrolls pushing its offset in here, and the other window pulls it
    // back out — so scrolling either one moves both, instead of a popped-out pane being a frozen
    // snapshot of wherever it happened to open scrolled to.
    [ObservableProperty]
    private double _itemFieldsScrollOffset;

    /// <summary>Filters Items by a substring of its Display text (file or item name) — Ctrl+F focuses the box this is bound to.</summary>
    [ObservableProperty]
    private string _filterText = "";

    private readonly DebounceTimer _filterDebounceTimer;

    /// <summary>
    /// Populated by ExmodEditorWindow's code-behind SelectionChanged handler — ListBox.SelectedItems
    /// isn't a bindable DependencyProperty in stock WPF, so this is the one piece of this editor
    /// that has to be pushed in from the View rather than bound directly, matching the same
    /// established gap LibraryView's own TreeView selection handling already works around.
    /// </summary>
    private IReadOnlyList<EditorItemViewModel> _selectedItemsForMassEdit = [];

    [ObservableProperty]
    private int _selectedItemCount;

    /// <summary>2+ items selected at once — mass edit becomes available. A single selection (the common case) keeps using the normal Fields view instead.</summary>
    public bool IsMassEditActive => SelectedItemCount > 1;

    [ObservableProperty]
    private string _massEditFieldName = "";

    [ObservableProperty]
    private string _massEditFieldValue = "";

    [ObservableProperty]
    private string? _massEditStatusMessage;

    public ExmodEditorViewModel(string folderName, ILibraryRepository repository, string dataFolder, GameDataIndexCache gameDataIndexCache)
    {
        _repository = repository;
        _writer = new OwnModWriter(repository);
        _folderName = folderName;
        _dataFolder = dataFolder;
        _gameDataIndexCache = gameDataIndexCache;
        // Only the .EXMOD: Read loaded every model and texture file of the mod into memory too (on
        // the UI thread), and the editor never uses them.
        _package = ExmodFolder.ReadPackageOnly(repository.GetFolderPath(folderName));
        _savedJson = ExmodJson.Serialize(_package);
        _replacedGameFiles = ExmodChangesFormatter.ReplacedGameFiles(repository.ListAssetPaths(folderName));
        _savedStamp = ExmodStamp();
        _filterDebounceTimer = new DebounceTimer(TimeSpan.FromMilliseconds(250), ApplyFilter);
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ItemsEmptyText));
            OnPropertyChanged(nameof(ShowPickItemPrompt));
        };

        RefreshBaseDiff();
        SnapshotOriginalValues();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault();
        LoadScaleRules();
        RefreshRuleTags();

        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((ExmodEditorViewModel)recipient).OnGameDataRefreshed());

        // The window's own Save / Don't save / Cancel can't stop the app closing (WPF ignores
        // Cancel in an owned window during shutdown), so the main window's close prompt asks here
        // first. Unregistered when the window closes (Detach): a closed editor has nothing to lose.
        WeakReferenceMessenger.Default.Register<UnsavedEditsRequestMessage>(this, (recipient, message) =>
        {
            if (((ExmodEditorViewModel)recipient).HasUnsavedChanges)
            {
                message.Reply("an open mod editor's changes");
            }
        });

        // A mod that has rules but no rows yet (fresh from the Quality of life template): build its
        // rows from this PC's game data right away and open on the rules, so the user starts from
        // a working mod. Nothing is saved until they Save.
        if (_package.ScaleRules.Count > 0 && !_package.Rows.Any(r => r.FileItems.Count > 0))
        {
            ViewMode = ExmodEditorViewMode.ScaleRules;
            if (ApplyScaleRulesCore())
            {
                _history.Clear();
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                ScaleStatusMessage = $"{ScaleStatusMessage} Turn rules on or off and change the numbers, then Apply rules again.";
            }
        }
    }

    /// <summary>The extracted game data the Scale rules read.</summary>
    internal string DataFolderForScale => _dataFolder;

    /// <summary>The same tables, parsed once for this editor (previews would otherwise re-read a multi-MB table per click).</summary>
    internal GameTableIndex GameTablesForScale => GameTables;

    partial void OnSelectedItemChanged(EditorItemViewModel? value)
    {
        OnPropertyChanged(nameof(ShowPickItemPrompt));
        PopulateFieldsForSelection();
    }

    // Debounced like Library's and Downloads' own search boxes, which this was the last one not to
    // match: ReloadItems rebuilds every row's ViewModel, and a mod can carry thousands of items
    // (one real file alone has 87), so filtering on every keystroke rebuilt the whole list per
    // character typed.
    partial void OnFilterTextChanged(string value) => _filterDebounceTimer.Restart();

    partial void OnSelectedItemCountChanged(int value) => OnPropertyChanged(nameof(IsMassEditActive));

    /// <summary>Called from ExmodEditorWindow's code-behind whenever the Items ListBox's (Extended-mode) selection changes.</summary>
    public void SetSelectedItemsForMassEdit(IReadOnlyList<EditorItemViewModel> items)
    {
        _selectedItemsForMassEdit = items;
        SelectedItemCount = items.Count;
    }

    /// <summary>F3 — moves the selection to the next item in the (possibly filtered) Items list, wrapping to the first after the last.</summary>
    [RelayCommand]
    private void SelectNextItem()
    {
        if (Items.Count == 0)
        {
            return;
        }

        var currentIndex = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        SelectedItem = Items[(currentIndex + 1) % Items.Count];
    }

    /// <summary>Reveals the selected item's real, unmodified base game data file in an in-app read-only viewer — a user picking this is almost always about to go back and forth with the editor itself, so keeping it in-app beats shelling out to whatever the OS has associated with .json.</summary>
    [RelayCommand]
    private void OpenOriginalFile()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "Select an item first.";
            return;
        }

        var realPath = Path.Combine(_dataFolder, SelectedItem.RealPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(realPath))
        {
            StatusMessage = $"No matching base file at '{realPath}'.";
            return;
        }

        try
        {
            new TextFileViewerWindow(SelectedItem.RealPath, realPath) { Owner = OwnerWindow() }.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same reasoning as SearchGameDataWindow's own identical catch — File.Exists above can
            // return true for a file whose CONTENT still can't be read (EFS encryption, a narrow
            // ACL, a TOCTOU race), which throws UnauthorizedAccessException from TextFileViewerWindow's
            // own File.ReadAllText, not IOException. Catching only IOException would let that reach
            // the app's global unhandled-exception handler, which deliberately terminates the app —
            // turning a benign "can't preview this one file" into a crash.
            StatusMessage = $"Couldn't open the file: {ex.Message}";
        }
    }

    /// <summary>
    /// Classic IMM's "Search Original JSON" — find an item by name AND everywhere its name/value is
    /// referenced inside other items' JSON across the whole extracted game data (which recipes
    /// consume 'Wood'; which items grant a stat). Non-modal, like the editor itself — a research
    /// companion meant to sit beside the fields being edited.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task SearchGameDataAsync()
    {
        IReadOnlyList<GameDataSearchEntry> gameSearchIndex;
        StatusMessage = "Indexing the game data for search…";
        try
        {
            gameSearchIndex = await _gameDataIndexCache.GetSearchIndexAsync(_dataFolder);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read the game data folder: {ex.Message}";
            return;
        }

        StatusMessage = null;

        if (gameSearchIndex.Count == 0)
        {
            StatusMessage = "No game data found — run Refresh game data in Settings first.";
            return;
        }

        var window = new SearchGameDataWindow(gameSearchIndex, _dataFolder) { Owner = OwnerWindow() };
        window.Show();
    }

    /// <summary>Reveals this mod's own real folder in Explorer — the same "open mods folder" convenience classic IMM's editor has, using the same folder Save writes to.</summary>
    [RelayCommand]
    private void OpenModsFolder()
    {
        if (UrlOpener.TryOpen(_repository.GetFolderPath(_folderName)) is { } ex)
        {
            StatusMessage = $"Couldn't open the folder: {ex.Message}";
        }
    }

    partial void OnViewModeChanged(ExmodEditorViewMode value)
    {
        if (value == ExmodEditorViewMode.NotInMod)
        {
            OpenNotInMod();
        }
        else if (value == ExmodEditorViewMode.ScaleRules)
        {
            FindScaleSuggestions();
        }
        else if (value == ExmodEditorViewMode.ItemFields)
        {
            // A rule edited in the Scale view changes which fields it sets.
            RefreshRuleTags();
        }
    }

    [RelayCommand]
    private void SetViewMode(string mode) => ViewMode = Enum.Parse<ExmodEditorViewMode>(mode);

    /// <summary>Every pane currently popped out of this editor, one per ExmodEditorViewMode — this is what makes PopOutCurrentView idempotent instead of piling up duplicate windows for the same view on repeated clicks.</summary>
    private readonly List<ExmodPaneWindow> _openPaneWindows = [];

    /// <summary>
    /// Detaches whichever view is currently showing into its own floating window — a real gap the
    /// pre-Phase-10 review flagged and the plan deliberately left cut ("arrangement, not
    /// capability"): all three views already share one live ExmodEditorViewModel/ExmodPackage, so a
    /// popped-out pane just needs its own fixed-mode window bound to that same instance, not a
    /// second ViewModel or any new sync plumbing. Finds this VM's own hosting Window by scanning
    /// Application.Windows (a ViewModel has no stored back-reference to it) so the pop-out is owned
    /// by the editor window it came from, not the main app window.
    /// </summary>
    [RelayCommand]
    private void PopOutCurrentView()
    {
        if (ViewMode is ExmodEditorViewMode.ScaleRules or ExmodEditorViewMode.NotInMod)
        {
            StatusMessage = "This view stays in this window. Pop out Fields to watch the rows it writes.";
            return;
        }

        var existing = _openPaneWindows.FirstOrDefault(w => w.FixedViewMode == ViewMode);
        if (existing is not null)
        {
            // Already open for this exact view — surface it instead of stacking another copy on
            // top. A minimized pane would otherwise Activate() invisibly, with nothing appearing
            // to happen.
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        // Owned by the editor window, never by another pane: closing that pane would take this one with it.
        var pane = new ExmodPaneWindow(this, ViewMode) { Owner = EditorWindow() ?? OwnerWindow() };
        _openPaneWindows.Add(pane);
        pane.Closed += (_, _) => _openPaneWindows.Remove(pane);
        pane.Show();
    }

    /// <summary>Copies every field but FileName from parsed onto the live _package in place, then refreshes every view derived from it. Used by Undo/Redo ("replace the whole package's content with a snapshot").</summary>
    /// <param name="reloadRules">False keeps the Scale rules view as it is — Undo of some other edit mustn't throw away rule changes not applied yet.</param>
    private void ApplyParsedPackage(ExmodPackage parsed, bool reloadRules = true)
    {
        _package.Name = parsed.Name;
        _package.Author = parsed.Author;
        _package.Version = parsed.Version;
        _package.Description = parsed.Description;
        _package.ImageUrl = parsed.ImageUrl;
        _package.ReadmeUrl = parsed.ReadmeUrl;
        _package.Level2 = parsed.Level2;
        _package.Week = parsed.Week;
        _package.VariantGroup = parsed.VariantGroup;
        _package.Variant = parsed.Variant;
        _package.VariantSort = parsed.VariantSort;
        _package.Rows = parsed.Rows;
        _package.ScaleRules = parsed.ScaleRules;
        if (reloadRules)
        {
            LoadScaleRules();
        }

        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>The filter box (debounced): rebuilds the list and keeps the selected item if it still shows.</summary>
    internal void ApplyFilter()
    {
        var previous = SelectedItem;
        ReloadItems();
        SelectedItem = previous is null ? null : Items.FirstOrDefault(i => Same(i.CurrentFile, previous.CurrentFile) && Same(i.ItemName, previous.ItemName));
    }

    /// <summary>
    /// File and row names ignore case, like the game (and ItemCloner, InsertFile, Merge): comparing
    /// them exactly let "items/d_itemsstatic.json" in beside "Items-D_ItemsStatic.json", and
    /// "envirosuit" beside "Envirosuit", as a second copy only one of which the merge uses.
    /// </summary>
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private ExmodFileItem? FindItem(string currentFile, string itemName) =>
        _package.Rows.FirstOrDefault(r => Same(r.CurrentFile, currentFile))?.FileItems.FirstOrDefault(i => Same(i.Name, itemName));

    private void ReloadItems()
    {
        OnPropertyChanged(nameof(NoDataRowsMessage));
        Items.Clear();
        foreach (var row in _package.Rows.OrderBy(r => r.CurrentFile, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var item in row.FileItems.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = MakeItem(row.CurrentFile, item.Name, item);
                if (string.IsNullOrWhiteSpace(FilterText) || candidate.SearchText.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                {
                    Items.Add(candidate);
                }
            }
        }
    }

    private void SyncItemOrder()
    {
        var ordered = Items
            .OrderBy(i => i.CurrentFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Items.Clear();
        foreach (var item in ordered)
        {
            Items.Add(item);
        }
    }
}
