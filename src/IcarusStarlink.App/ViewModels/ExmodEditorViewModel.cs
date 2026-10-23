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

/// <summary>One real DataTable row in the extracted game data — the pickable unit for "Add item from game data". CurrentFile is EXMOD's own dash-flattened form; RealPath keeps the slashes for display and for reading the file back.</summary>
public sealed record GameDataItemRef(string CurrentFile, string RealPath, string ItemName)
{
    public string Display => $"{RealPath} — {ItemName}";
}

/// <summary>One row plus its own serialized JSON — the searchable unit for the Search game data window's reference search. Kept separate from GameDataItemRef so the (much lighter) add-item picker never pays for the serialized text it doesn't use.</summary>
public sealed record GameDataSearchEntry(string RealPath, string ItemName, string RowJson);

/// <summary>Which of the editor's three views (spec: "Item fields, File JSON, or Full EXMOD JSON views") is showing in the right-hand pane.</summary>
public enum ExmodEditorViewMode
{
    ItemFields,
    FileJson,
    FullExmodJson,

    /// <summary>"Scale a field across a table" rules (ExmodEditorViewModel.Scale.cs).</summary>
    ScaleRules,
}

/// <summary>
/// Phase 7.1's core EXMOD editor — deliberately the first transient (per-open-instance) ViewModel
/// in this app; every other ViewModel is a DI singleton. Classic IMM's own editor really is a
/// separate .exe that supports two independent sessions open at once, which this window/ViewModel
/// pairing matches (see the plan's "7.1 — Core editor" section) — LibraryViewModel constructs a
/// fresh instance per Edit… / New mod… click via the Func&lt;string, ExmodEditorViewModel&gt;
/// factory registered in App.xaml.cs, rather than this being resolved as a singleton.
/// Phase 7.2 added the File JSON / Full EXMOD JSON raw-text views alongside Item fields.
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

    [ObservableProperty]
    private string _fileJsonText = "";

    [ObservableProperty]
    private string _fullExmodJsonText = "";

    [ObservableProperty]
    private string? _rawJsonStatusMessage;

    // Shared scroll position per view — not per window. When the same view (e.g. Item fields) is
    // open in both the main editor window and a popped-out pane at once, ScrollSyncHelper keeps
    // whichever one the user scrolls pushing its offset in here, and the other window pulls it
    // back out — so scrolling either one moves both, instead of a popped-out pane being a frozen
    // snapshot of wherever it happened to open scrolled to.
    [ObservableProperty]
    private double _itemFieldsScrollOffset;

    [ObservableProperty]
    private double _fileJsonScrollOffset;

    [ObservableProperty]
    private double _fullExmodJsonScrollOffset;

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

    /// <summary>2+ items selected at once — mass edit becomes available. A single selection (the common case) keeps using the normal Item fields/File JSON/Full EXMOD JSON views instead.</summary>
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
        _package = ExmodFolder.Read(repository.GetFolderPath(folderName)).Package;
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

        // Keeps the File JSON view "synced" to whatever's selected in the shared Items list, per
        // the spec's "share selection/edits with the Item fields view" — switching the selected
        // item while already in File JSON mode should show *that* item's own file, not whatever
        // was showing before.
        if (ViewMode == ExmodEditorViewMode.FileJson)
        {
            RefreshFileJsonText();
        }
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
        RawJsonStatusMessage = null;
        if (value == ExmodEditorViewMode.FileJson)
        {
            RefreshFileJsonText();
        }
        else if (value == ExmodEditorViewMode.FullExmodJson)
        {
            RefreshFullExmodJsonText();
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
        if (ViewMode == ExmodEditorViewMode.ScaleRules)
        {
            StatusMessage = "Scale rules stay in this window — pop out Fields or a JSON view to watch the rows they write.";
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

    /// <summary>Copies every field but FileName (the caller's own responsibility to guard, per ApplyFullExmodJson's own check above) from parsed onto the live _package in place, then refreshes every view derived from it. Shared by ApplyFullExmodJson and Undo — both are, structurally, "replace the whole package's content with something else."</summary>
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

    /// <summary>Snapshots the package before a change. A new change (not Redo itself) ends any redo chain.</summary>
    private void PushUndoSnapshot() => PushUndoSnapshot(clearRedo: true);

    private void PushUndoSnapshot(bool clearRedo)
    {
        if (_restoringSnapshot && clearRedo)
        {
            return;
        }

        _lastEditedField = null;
        var hadRedo = _history.CanRedo;
        _history.Push(CurrentSnapshot(), clearRedo);
        if (hadRedo != _history.CanRedo)
        {
            OnPropertyChanged(nameof(CanRedo));
        }

        OnPropertyChanged(nameof(CanUndo));
    }

    private string CurrentSnapshot() => ExmodJson.ToJsonObject(_package).ToJsonString();

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    /// <summary>
    /// Called by a field row just before it writes a new value: the first write to a field after
    /// anything else (another field, a structural action, an Undo) snapshots first, so Undo takes
    /// back that field's typing as one change and never the typing along with an earlier action.
    /// </summary>
    private void SnapshotBeforeFieldEdit(string currentFile, string itemName, string fieldName)
    {
        var key = (currentFile, itemName, fieldName);
        if (_lastEditedField == key)
        {
            return;
        }

        PushUndoSnapshot();
        _lastEditedField = key;
    }

    /// <summary>Reverts the last change (see ExmodUndoHistory for exactly what counts); what it took away goes on the redo stack.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (_history.Undo(CurrentSnapshot()) is not { } snapshotJson)
        {
            StatusMessage = "Nothing to undo.";
            return;
        }

        RestoreSnapshot(snapshotJson);
        StatusMessage = "Undid last action.";
    }

    /// <summary>Puts back what the last Undo took away (until a new change is made).</summary>
    [RelayCommand]
    private void Redo()
    {
        if (_history.Redo(CurrentSnapshot()) is not { } snapshotJson)
        {
            StatusMessage = "Nothing to redo.";
            return;
        }

        RestoreSnapshot(snapshotJson);
        StatusMessage = "Redid last action.";
    }

    private void RestoreSnapshot(string snapshotJson)
    {
        _lastEditedField = null;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));

        var snapshot = ExmodJson.Parse(snapshotJson);
        var previousFile = SelectedItem?.CurrentFile;
        var previousName = SelectedItem?.ItemName;
        _restoringSnapshot = true;
        try
        {
            ApplyParsedPackage(snapshot, reloadRules: !_scaleRulesDirty);
            SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, previousFile) && Same(i.ItemName, previousName)) ?? Items.FirstOrDefault();
        }
        finally
        {
            _restoringSnapshot = false;
        }

        _lastEditedField = null;
    }

    /// <summary>Set while Undo/Redo rebuild the view: anything that writes then is the restore, not a new change to snapshot.</summary>
    private bool _restoringSnapshot;

    /// <summary>"Name" is the row-identity key in EXMOD JSON (see ReservedFieldNames in PakIO) — an item carrying a field by that name can't be serialized, and every structural command snapshots by serializing, so it has to be refused at entry rather than crash the next click.</summary>
    private static bool IsReservedFieldName(string fieldName) => string.Equals(fieldName.Trim(), "Name", StringComparison.Ordinal);

    /// <summary>
    /// The base tables RefreshBaseDiff reads, parsed once for this editor (until Refresh game data):
    /// every Add field, Duplicate, mass edit, Undo and Redo re-read and re-parsed each multi-MB
    /// table the mod touches, on the UI thread.
    /// </summary>
    private readonly Dictionary<string, JsonObject?> _baseTableCache = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshBaseDiff()
    {
        var report = new MergeReport();
        var baseDiff = ExmodBaseDiffer.DiffAgainstBase(_package, _dataFolder, new DefaultSemanticClassifier(), report, _baseTableCache);
        _baseValuesByKey = baseDiff.ToDictionary(c => (c.CurrentFile, c.ItemName, c.FieldName), c => c.OriginalValue);

        // Explicitly clears a stale warning from an earlier refresh (e.g. a prior package version
        // had a row with no matching base file) when a later refresh — after a File JSON/Full
        // EXMOD JSON Apply changed what Rows actually contains — no longer has anything to warn
        // about; a bare "only set when there are warnings" would leave the old message showing
        // even though it no longer applies to the package's current content.
        StatusMessage = report.Warnings.Count > 0 ? report.Warnings[0] : null;
    }

    /// <summary>Shared by OnSelectedItemChanged and a successful Save (which needs the just-saved item's own "was:" indicators to clear immediately, not just on next reselect).</summary>
    private void PopulateFieldsForSelection()
    {
        Fields.Clear();
        if (SelectedItem is null)
        {
            return;
        }

        var item = FindItem(SelectedItem.CurrentFile, SelectedItem.ItemName);
        if (item is null)
        {
            return;
        }

        var selected = SelectedItem;
        foreach (var fieldName in item.Fields.Keys.ToList())
        {
            var baseValue = BaseValueFor(selected.CurrentFile, selected.ItemName, fieldName, item);
            var originalValue = LookupOrFallback(_originalValuesByKey, selected.CurrentFile, selected.ItemName, fieldName, item);
            AddFieldRow(new EditorFieldViewModel(fieldName, item.Fields, baseValue, originalValue, StatChoicesForFields(),
                ChoicesFor(selected.CurrentFile, fieldName, item.Fields.GetValueOrDefault(fieldName) ?? baseValue)), selected);
        }
    }

    /// <summary>Adds a field row; its edits keep the list's change badge for the row up to date.</summary>
    private void AddFieldRow(EditorFieldViewModel field, EditorItemViewModel owner)
    {
        field.BeforeWrite = () => SnapshotBeforeFieldEdit(owner.CurrentFile, owner.ItemName, field.FieldName);
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorFieldViewModel.ValueText))
            {
                Recount(owner);
            }
        };
        Fields.Add(field);
    }

    /// <summary>A field genuinely not found in the given lookup (never diffed against base, or didn't exist at the last snapshot) falls back to "same as current" — no highlight, matching the established convention for both _baseValuesByKey and _originalValuesByKey.</summary>
    private static JsonNode? LookupOrFallback(
        Dictionary<(string CurrentFile, string ItemName, string FieldName), JsonNode?> lookup,
        string currentFile, string itemName, string fieldName, ExmodFileItem item) =>
        lookup.TryGetValue((currentFile, itemName, fieldName), out var v) ? v : item.Fields[fieldName];

    /// <summary>See _originalValuesByKey's own doc comment for when this runs and why.</summary>
    private void SnapshotOriginalValues()
    {
        var snapshot = new Dictionary<(string, string, string), JsonNode?>();
        foreach (var row in _package.Rows)
        {
            foreach (var item in row.FileItems)
            {
                foreach (var (fieldName, value) in item.Fields.ToList())
                {
                    snapshot[(row.CurrentFile, item.Name, fieldName)] = value;
                }
            }
        }

        _originalValuesByKey = snapshot;
    }

    [RelayCommand]
    private void AddField()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "Select an item first.";
            return;
        }

        NewFieldName = NewFieldName.Trim();
        if (string.IsNullOrWhiteSpace(NewFieldName))
        {
            StatusMessage = "Field name is required.";
            return;
        }

        if (IsReservedFieldName(NewFieldName))
        {
            StatusMessage = "'Name' is the item's own identity, not a field — rename the item instead.";
            return;
        }

        var item = FindItem(SelectedItem.CurrentFile, SelectedItem.ItemName)!;
        if (item.Fields.ContainsKey(NewFieldName))
        {
            StatusMessage = $"'{NewFieldName}' already exists on this item.";
            return;
        }

        JsonNode? parsedValue;
        try
        {
            parsedValue = string.IsNullOrWhiteSpace(NewFieldValue) ? null : JsonNode.Parse(NewFieldValue);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Invalid JSON value: {ex.Message}";
            return;
        }

        PushUndoSnapshot();
        item.Fields[NewFieldName] = parsedValue;
        // The amber "differs from base" highlight is keyed on a diff recomputed whenever Rows change
        // shape — a freshly added field had no entry and so never highlighted (an audit finding).
        RefreshBaseDiff();
        var baseValue = BaseValueFor(SelectedItem.CurrentFile, SelectedItem.ItemName, NewFieldName, item);
        var originalValue = LookupOrFallback(_originalValuesByKey, SelectedItem.CurrentFile, SelectedItem.ItemName, NewFieldName, item);
        AddFieldRow(new EditorFieldViewModel(NewFieldName, item.Fields, baseValue, originalValue, StatChoicesForFields(),
            ChoicesFor(SelectedItem.CurrentFile, NewFieldName, parsedValue ?? baseValue)), SelectedItem);
        Recount(SelectedItem);

        NewFieldName = "";
        NewFieldValue = "";
        StatusMessage = null;
    }

    [RelayCommand]
    private void RemoveField(EditorFieldViewModel? field)
    {
        if (SelectedItem is null || field is null)
        {
            return;
        }

        var item = FindItem(SelectedItem.CurrentFile, SelectedItem.ItemName)!;
        PushUndoSnapshot();
        item.Fields.Remove(field.FieldName);
        Fields.Remove(field);
        Recount(SelectedItem);
    }

    [RelayCommand]
    private void AddItem()
    {
        if (string.IsNullOrWhiteSpace(NewItemCurrentFile) || string.IsNullOrWhiteSpace(NewItemName))
        {
            StatusMessage = "Type both the file and the row name.";
            return;
        }

        // Matches the real EXMOD CurrentFile convention confirmed throughout Phase 6:
        // "Traits/D_Fuel.json" typed by a user -> "Traits-D_Fuel.json" as stored.
        var currentFile = NewItemCurrentFile.Trim().Replace('/', '-').Replace('\\', '-');
        NewItemName = NewItemName.Trim();

        // Validated BEFORE the undo snapshot (an audit finding): a refused add used to leave a no-op
        // snapshot on the stack, so the user's next Undo "undid" nothing.
        var row = _package.Rows.FirstOrDefault(r => Same(r.CurrentFile, currentFile));
        if (row is not null && row.FileItems.Any(i => Same(i.Name, NewItemName)))
        {
            StatusMessage = $"'{NewItemName}' already exists in '{currentFile}'.";
            return;
        }

        PushUndoSnapshot();
        if (row is null)
        {
            row = new ExmodFileRow { CurrentFile = currentFile };
            _package.Rows.Add(row);
        }

        row.FileItems.Add(new ExmodFileItem { Name = NewItemName });

        // The file as the mod already spells it ("Items-D_ItemsStatic.json" for a typed "items/d_itemsstatic.json").
        var newItem = MakeItem(row.CurrentFile, NewItemName);
        Items.Add(newItem);
        SyncItemOrder();
        SelectedItem = newItem;

        NewItemCurrentFile = "";
        NewItemName = "";
        StatusMessage = null;
    }

    /// <summary>True when the mod differs from what's on disk (typed values, structural edits, or Scale rules not applied yet).</summary>
    public bool HasUnsavedChanges => _scaleRulesDirty || ExmodJson.Serialize(_package) != _savedJson;

    /// <summary>Asks what to do with unsaved changes on close: true = Save, false = Don't save, null = Cancel. Tests swap in an answer.</summary>
    internal Func<string, bool?> AskSaveOnClose
    {
        get => _askSaveOnClose ??= message => ThemedConfirmDialog.ShowWithCancel(
            OwnerWindow(), message, "Unsaved changes", ThemedConfirmSeverity.Question, "Save", "Don't save");
        set => _askSaveOnClose = value;
    }

    private Func<string, bool?>? _askSaveOnClose;

    /// <summary>
    /// Called as the window closes: true when it may close. Unsaved changes ask Save / Don't save /
    /// Cancel; a Save that doesn't go through (a rule to fix, the mod changed elsewhere, its folder
    /// gone) keeps the window open with the reason showing.
    /// </summary>
    public bool ConfirmClose()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        switch (AskSaveOnClose($"You have changes to '{_package.Name}' that aren't saved. Save them before closing?"))
        {
            case null:
                return false;
            case false:
                return true;
            default:
                Save();
                return !HasUnsavedChanges;
        }
    }

    /// <summary>Called when the editor window has closed: stops answering app-wide messages.</summary>
    public void Detach() => WeakReferenceMessenger.Default.UnregisterAll(this);

    /// <summary>False once the mod was deleted (or moved) in Library; the repository throws for a folder it no longer knows.</summary>
    private bool ModFolderExists()
    {
        try
        {
            return Directory.Exists(_repository.GetFolderPath(_folderName));
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        // Deleted (or moved) in Library while open here: writing would make an orphan folder that
        // Library doesn't list, so the work would look saved but be nowhere to be found.
        if (!ModFolderExists())
        {
            StatusMessage = "Not saved: this mod's folder is gone (deleted or moved in Library?). To keep your work, copy it from the Full EXMOD JSON view into a new mod.";
            return;
        }

        // The Editor page (or anything else) may have saved into this mod since it was opened here:
        // overwriting that silently would lose it. The first Save says so; a second one overwrites.
        void RefuseChangedElsewhere()
        {
            _overwriteOutsideChanges = true;
            StatusMessage = "Not saved: this mod was changed somewhere else (the Editor page?) since you opened it here. Save again to overwrite those changes, or close this window and reopen the mod to keep them.";
        }

        if (ExmodStamp() != _savedStamp && !_overwriteOutsideChanges)
        {
            RefuseChangedElsewhere();
            return;
        }

        if (!ApplyPendingScaleRules())
        {
            StatusMessage = $"Not saved — a scale rule needs fixing: {ScaleStatusMessage}";
            return;
        }

        try
        {
            // Writes a STRIPPED copy — never mutates the live _package/its item.Fields in place, so
            // nothing visibly vanishes from the open editor (Items list, Fields pane, amber
            // highlights, Undo stack all keep operating on the real, unstripped session state). The
            // ExmodJson round-trip is the same deep-copy pattern PushUndoSnapshot already uses, so
            // every ExmodPackage-level property (Name/Author/Version/etc.) carries over correctly
            // without being hand-copied field by field here.
            var strippedRows = ExmodBaseDiffer.StripFieldsIdenticalToBase(_package, _dataFolder);
            var strippedFieldCount = _package.Rows.Sum(r => r.FileItems.Sum(i => i.Fields.Count))
                - strippedRows.Sum(r => r.FileItems.Sum(i => i.Fields.Count));

            var writePackage = ExmodJson.Parse(ExmodJson.Serialize(_package));
            writePackage.Rows = strippedRows;
            if (!_writer.TrySave(_folderName, writePackage, _overwriteOutsideChanges ? null : _savedStamp, out var stamp))
            {
                RefuseChangedElsewhere();
                return;
            }

            _savedStamp = stamp;
            _savedJson = ExmodJson.Serialize(_package);
            _overwriteOutsideChanges = false;
            HasSaved = true;

            // What just got written is now this mod's own "original" going forward — refreshing
            // the already-displayed Fields immediately (not waiting for the next reselect) so
            // every "was: X" indicator for the just-saved item clears right away.
            SnapshotOriginalValues();
            PopulateFieldsForSelection();
            StatusMessage = strippedFieldCount > 0
                ? $"Saved — removed {strippedFieldCount} field(s) that matched base game data."
                : "Saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            return;
        }

        // Saved either way; a link to a row that doesn't exist is worth knowing about now, not in game.
        if (Directory.Exists(_dataFolder) && RunReferenceCheck(showWhenClean: false) is > 0 and var problems)
        {
            StatusMessage += $" {problems} reference problem(s), listed above.";
        }
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
