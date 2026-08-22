using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Which of the editor's three views (spec: "Item fields, File JSON, or Full EXMOD JSON views") is showing in the right-hand pane.</summary>
public enum ExmodEditorViewMode
{
    ItemFields,
    FileJson,
    FullExmodJson,
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
    private static readonly JsonSerializerOptions DisplayOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILibraryRepository _repository;
    private readonly string _folderName;
    private readonly string _dataFolder;
    private readonly ExmodPackage _package;

    /// <summary>
    /// Snapshots taken before each discrete structural action (Add/Remove field, Add item,
    /// Duplicate, mass edit, a raw-JSON Apply) — deliberately NOT per-keystroke: EditorFieldViewModel
    /// writes a per-field value straight into the live package on every keystroke with no commit
    /// event to hook, and snapshotting that granularly would make Undo revert one character at a
    /// time instead of one meaningful action. Each entry is a full serialized copy (via the same
    /// ExmodJson round-trip Full EXMOD JSON already uses), not a shallow reference — JsonNode/
    /// ExmodPackage are mutable reference types, so anything less would let a later edit silently
    /// corrupt an already-pushed snapshot. Capped so an extended editing session can't grow this
    /// unboundedly.
    /// </summary>
    private readonly List<string> _undoSnapshots = [];
    private const int MaxUndoDepth = 20;

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

    public string WindowTitle => $"Edit — {_package.Name}";

    public ObservableCollection<EditorItemViewModel> Items { get; } = [];

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

    /// <summary>Filters Items by a substring of its Display text (file or item name) — Ctrl+F focuses the box this is bound to.</summary>
    [ObservableProperty]
    private string _filterText = "";

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

    public ExmodEditorViewModel(string folderName, ILibraryRepository repository, string dataFolder)
    {
        _repository = repository;
        _folderName = folderName;
        _dataFolder = dataFolder;
        _package = ExmodFolder.Read(repository.GetFolderPath(folderName)).Package;

        RefreshBaseDiff();
        SnapshotOriginalValues();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault();
    }

    partial void OnSelectedItemChanged(EditorItemViewModel? value)
    {
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

    partial void OnFilterTextChanged(string value) => ReloadItems();

    partial void OnSelectedItemCountChanged(int value) => OnPropertyChanged(nameof(IsMassEditActive));

    /// <summary>Called from ExmodEditorWindow's code-behind whenever the Items ListBox's (Extended-mode) selection changes.</summary>
    public void SetSelectedItemsForMassEdit(IReadOnlyList<EditorItemViewModel> items)
    {
        _selectedItemsForMassEdit = items;
        SelectedItemCount = items.Count;
    }

    [RelayCommand]
    private void ApplyMassEdit()
    {
        if (_selectedItemsForMassEdit.Count < 2)
        {
            MassEditStatusMessage = "Select 2 or more items first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(MassEditFieldName))
        {
            MassEditStatusMessage = "Field name is required.";
            return;
        }

        JsonNode? parsedValue;
        try
        {
            parsedValue = string.IsNullOrWhiteSpace(MassEditFieldValue) ? null : JsonNode.Parse(MassEditFieldValue);
        }
        catch (Exception ex)
        {
            MassEditStatusMessage = $"Invalid JSON value: {ex.Message}";
            return;
        }

        PushUndoSnapshot();
        var count = 0;
        foreach (var editorItem in _selectedItemsForMassEdit)
        {
            var item = FindItem(editorItem.CurrentFile, editorItem.ItemName);
            if (item is null)
            {
                continue;
            }

            // Each item's Fields dictionary needs its own JsonNode instance — a JsonNode can only
            // ever belong to one parent, the same constraint ExmodBaseDiffer.ToKeyedObject already
            // works around by deep-cloning.
            item.Fields[MassEditFieldName] = parsedValue?.DeepClone();
            count++;
        }

        RefreshBaseDiff();
        PopulateFieldsForSelection();
        MassEditStatusMessage = $"Set '{MassEditFieldName}' on {count} item(s).";
    }

    /// <summary>Ctrl+D — clones the selected item's own fields into a new item under the same file, named "&lt;Name&gt;_Copy" (or "_Copy2"/"_Copy3"/... if that's already taken).</summary>
    [RelayCommand]
    private void DuplicateItem()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "Select an item first.";
            return;
        }

        var sourceItem = FindItem(SelectedItem.CurrentFile, SelectedItem.ItemName);
        var row = _package.Rows.FirstOrDefault(r => r.CurrentFile == SelectedItem.CurrentFile);
        if (sourceItem is null || row is null)
        {
            return;
        }

        var newName = $"{sourceItem.Name}_Copy";
        var suffix = 2;
        while (row.FileItems.Any(i => i.Name == newName))
        {
            newName = $"{sourceItem.Name}_Copy{suffix}";
            suffix++;
        }

        var clonedFields = new Dictionary<string, JsonNode?>();
        foreach (var (fieldName, value) in sourceItem.Fields)
        {
            clonedFields[fieldName] = value?.DeepClone();
        }

        PushUndoSnapshot();
        row.FileItems.Add(new ExmodFileItem { Name = newName, Fields = clonedFields });

        var newEditorItem = new EditorItemViewModel(row.CurrentFile, newName);
        Items.Add(newEditorItem);
        SyncItemOrder();
        SelectedItem = newEditorItem;
        StatusMessage = $"Duplicated '{sourceItem.Name}' as '{newName}'.";
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

    /// <summary>Reveals the selected item's real, unmodified base game data file in whatever the OS has associated with .json — read-only reference, not something this app opens an in-app viewer for.</summary>
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
            Process.Start(new ProcessStartInfo(realPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open the file: {ex.Message}";
        }
    }

    /// <summary>
    /// "Insert file at location" — classic IMM's own name for picking one of the game's own real
    /// data files from a browsable list, rather than free-typing its path the way Add item's own
    /// NewItemCurrentFile box requires. Adds it with zero items yet; Add item (or Add field once an
    /// item exists) is still what actually puts content into it.
    /// </summary>
    [RelayCommand]
    private void InsertFileAtLocation()
    {
        var dialog = new PickFileDialog(_dataFolder) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.SelectedCurrentFile is not { } currentFile)
        {
            return;
        }

        if (_package.Rows.Any(r => string.Equals(r.CurrentFile, currentFile, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"'{currentFile}' is already part of this mod.";
            return;
        }

        PushUndoSnapshot();
        var previousFile = SelectedItem?.CurrentFile;
        var previousName = SelectedItem?.ItemName;
        _package.Rows.Add(new ExmodFileRow { CurrentFile = currentFile });
        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => i.CurrentFile == previousFile && i.ItemName == previousName) ?? Items.FirstOrDefault();
        StatusMessage = $"Added '{currentFile}' with no items yet — use Add item to add one.";
    }

    /// <summary>
    /// "Merge existing mod into this one" — copies another Library mod's own items into this one.
    /// An item this mod already has (same CurrentFile + item name) is left alone, never overwritten
    /// — this is a one-time fold-in action with no queue/conflict-picker semantics behind it, so
    /// "don't touch what's already there" is the one safe default. Binary assets aren't part of
    /// this (per the dialog's own description, "copies every item" — the picked mod's own real
    /// .uasset/.uexp files aren't something this in-memory JSON editor touches at all; a mod that
    /// genuinely needs the other one's assets too still needs those copied by hand into its folder).
    /// </summary>
    [RelayCommand]
    private void MergeExistingMod()
    {
        var candidates = _repository.GetAll()
            .Where(e => !e.IsOpaquePak && !string.Equals(e.FolderName, _folderName, StringComparison.OrdinalIgnoreCase));

        var dialog = new PickModDialog(candidates) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.SelectedEntry is not { } selected)
        {
            return;
        }

        ExmodPackage sourcePackage;
        try
        {
            sourcePackage = ExmodFolder.Read(_repository.GetFolderPath(selected.FolderName)).Package;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read '{selected.Name}': {ex.Message}";
            return;
        }

        PushUndoSnapshot();
        var previousFile = SelectedItem?.CurrentFile;
        var previousName = SelectedItem?.ItemName;
        var addedCount = 0;
        foreach (var sourceRow in sourcePackage.Rows)
        {
            var targetRow = _package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, sourceRow.CurrentFile, StringComparison.OrdinalIgnoreCase));
            if (targetRow is null)
            {
                targetRow = new ExmodFileRow { CurrentFile = sourceRow.CurrentFile };
                _package.Rows.Add(targetRow);
            }

            foreach (var sourceItem in sourceRow.FileItems)
            {
                if (targetRow.FileItems.Any(i => i.Name == sourceItem.Name))
                {
                    continue;
                }

                // Each item's own Fields dictionary needs its own JsonNode instances — a JsonNode
                // can only ever belong to one parent, same reasoning DuplicateItem already documents.
                var clonedFields = new Dictionary<string, JsonNode?>();
                foreach (var (fieldName, value) in sourceItem.Fields)
                {
                    clonedFields[fieldName] = value?.DeepClone();
                }

                targetRow.FileItems.Add(new ExmodFileItem { Name = sourceItem.Name, Fields = clonedFields });
                addedCount++;
            }
        }

        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => i.CurrentFile == previousFile && i.ItemName == previousName) ?? Items.FirstOrDefault();
        StatusMessage = addedCount == 0
            ? $"Nothing new to merge — every item in '{selected.Name}' is already here."
            : $"Merged {addedCount} item(s) from '{selected.Name}'.";
    }

    /// <summary>Reveals this mod's own real folder in Explorer — the same "open mods folder" convenience classic IMM's editor has, using the same folder Save writes to.</summary>
    [RelayCommand]
    private void OpenModsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_repository.GetFolderPath(_folderName)) { UseShellExecute = true });
        }
        catch (Exception ex)
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

    [RelayCommand]
    private void ApplyFileJson()
    {
        if (SelectedItem is null)
        {
            RawJsonStatusMessage = "Select an item first.";
            return;
        }

        JsonObject parsedObject;
        try
        {
            parsedObject = JsonNode.Parse(FileJsonText) as JsonObject
                ?? throw new FormatException("Root is not a JSON object.");
        }
        catch (Exception ex)
        {
            RawJsonStatusMessage = $"Invalid JSON: {ex.Message}";
            return;
        }

        ExmodFileRow newRow;
        try
        {
            newRow = ExmodJson.ParseRow(parsedObject);
        }
        catch (Exception ex)
        {
            RawJsonStatusMessage = $"Invalid file JSON: {ex.Message}";
            return;
        }

        PushUndoSnapshot();
        var existingIndex = _package.Rows.FindIndex(r => r.CurrentFile == SelectedItem.CurrentFile);
        if (existingIndex >= 0)
        {
            _package.Rows[existingIndex] = newRow;
        }
        else
        {
            _package.Rows.Add(newRow);
        }

        RefreshBaseDiff();
        var previousFile = SelectedItem.CurrentFile;
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => i.CurrentFile == previousFile) ?? Items.FirstOrDefault();
        RawJsonStatusMessage = "Applied.";
    }

    [RelayCommand]
    private void ApplyFullExmodJson()
    {
        ExmodPackage parsed;
        try
        {
            parsed = ExmodJson.Parse(FullExmodJsonText);
        }
        catch (Exception ex)
        {
            RawJsonStatusMessage = $"Invalid EXMOD JSON: {ex.Message}";
            return;
        }

        // Guards the same ambiguous-.EXMOD-files state 7.1's design already keeps FileName out of
        // the Item fields view for — Save() writes to "<FileName>.EXMOD" without deleting any
        // differently-named file already on disk, so silently accepting a changed fileName here
        // would leave two .EXMOD files in the mod's folder.
        if (parsed.FileName != _package.FileName)
        {
            RawJsonStatusMessage = $"Can't change \"fileName\" here (would leave two .EXMOD files on disk) — it must stay \"{_package.FileName}\".";
            return;
        }

        PushUndoSnapshot();
        ApplyParsedPackage(parsed);
        RawJsonStatusMessage = "Applied.";
    }

    /// <summary>Copies every field but FileName (the caller's own responsibility to guard, per ApplyFullExmodJson's own check above) from parsed onto the live _package in place, then refreshes every view derived from it. Shared by ApplyFullExmodJson and Undo — both are, structurally, "replace the whole package's content with something else."</summary>
    private void ApplyParsedPackage(ExmodPackage parsed)
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

        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault();
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void PushUndoSnapshot()
    {
        _undoSnapshots.Add(ExmodJson.ToJsonObject(_package).ToJsonString());
        if (_undoSnapshots.Count > MaxUndoDepth)
        {
            _undoSnapshots.RemoveAt(0);
        }

        OnPropertyChanged(nameof(CanUndo));
    }

    public bool CanUndo => _undoSnapshots.Count > 0;

    /// <summary>Reverts the last discrete structural action (see _undoSnapshots' own doc comment for exactly what counts) — does not itself push a "redo" point, matching classic IMM's own plain single-direction Undo button.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (_undoSnapshots.Count == 0)
        {
            StatusMessage = "Nothing to undo.";
            return;
        }

        var snapshotJson = _undoSnapshots[^1];
        _undoSnapshots.RemoveAt(_undoSnapshots.Count - 1);
        OnPropertyChanged(nameof(CanUndo));

        var snapshot = ExmodJson.Parse(snapshotJson);
        var previousFile = SelectedItem?.CurrentFile;
        var previousName = SelectedItem?.ItemName;
        ApplyParsedPackage(snapshot);
        SelectedItem = Items.FirstOrDefault(i => i.CurrentFile == previousFile && i.ItemName == previousName) ?? Items.FirstOrDefault();
        StatusMessage = "Undid last action.";
    }

    private void RefreshBaseDiff()
    {
        var report = new MergeReport();
        var baseDiff = ExmodBaseDiffer.DiffAgainstBase(_package, _dataFolder, new DefaultSemanticClassifier(), report);
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

        foreach (var fieldName in item.Fields.Keys.ToList())
        {
            var baseValue = LookupOrFallback(_baseValuesByKey, SelectedItem.CurrentFile, SelectedItem.ItemName, fieldName, item);
            var originalValue = LookupOrFallback(_originalValuesByKey, SelectedItem.CurrentFile, SelectedItem.ItemName, fieldName, item);
            Fields.Add(new EditorFieldViewModel(fieldName, item.Fields, baseValue, originalValue));
        }
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
                foreach (var (fieldName, value) in item.Fields)
                {
                    snapshot[(row.CurrentFile, item.Name, fieldName)] = value;
                }
            }
        }

        _originalValuesByKey = snapshot;
    }

    private void RefreshFileJsonText()
    {
        if (SelectedItem is null)
        {
            FileJsonText = "";
            return;
        }

        var row = _package.Rows.FirstOrDefault(r => r.CurrentFile == SelectedItem.CurrentFile);
        FileJsonText = row is null ? "" : ExmodJson.RowToJsonObject(row).ToJsonString(DisplayOptions);
    }

    private void RefreshFullExmodJsonText() => FullExmodJsonText = ExmodJson.ToJsonObject(_package).ToJsonString(DisplayOptions);

    [RelayCommand]
    private void AddField()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "Select an item first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewFieldName))
        {
            StatusMessage = "Field name is required.";
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
        var baseValue = LookupOrFallback(_baseValuesByKey, SelectedItem.CurrentFile, SelectedItem.ItemName, NewFieldName, item);
        var originalValue = LookupOrFallback(_originalValuesByKey, SelectedItem.CurrentFile, SelectedItem.ItemName, NewFieldName, item);
        Fields.Add(new EditorFieldViewModel(NewFieldName, item.Fields, baseValue, originalValue));

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
    }

    [RelayCommand]
    private void AddItem()
    {
        if (string.IsNullOrWhiteSpace(NewItemCurrentFile) || string.IsNullOrWhiteSpace(NewItemName))
        {
            StatusMessage = "Both file and item name are required.";
            return;
        }

        // Matches the real EXMOD CurrentFile convention confirmed throughout Phase 6:
        // "Traits/D_Fuel.json" typed by a user -> "Traits-D_Fuel.json" as stored.
        var currentFile = NewItemCurrentFile.Trim().Replace('/', '-').Replace('\\', '-');

        PushUndoSnapshot();
        var row = _package.Rows.FirstOrDefault(r => r.CurrentFile == currentFile);
        if (row is null)
        {
            row = new ExmodFileRow { CurrentFile = currentFile };
            _package.Rows.Add(row);
        }

        if (row.FileItems.Any(i => i.Name == NewItemName))
        {
            StatusMessage = $"'{NewItemName}' already exists in '{currentFile}'.";
            return;
        }

        row.FileItems.Add(new ExmodFileItem { Name = NewItemName });

        var newItem = new EditorItemViewModel(currentFile, NewItemName);
        Items.Add(newItem);
        SyncItemOrder();
        SelectedItem = newItem;

        NewItemCurrentFile = "";
        NewItemName = "";
        StatusMessage = null;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            ExmodFolder.Write(_repository.GetFolderPath(_folderName), new ExmodPackageContents(_package, []));
            _repository.MarkLocallyEdited(_folderName);

            // What just got written is now this mod's own "original" going forward — refreshing
            // the already-displayed Fields immediately (not waiting for the next reselect) so
            // every "was: X" indicator for the just-saved item clears right away.
            SnapshotOriginalValues();
            PopulateFieldsForSelection();

            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            StatusMessage = "Saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    private ExmodFileItem? FindItem(string currentFile, string itemName) =>
        _package.Rows.FirstOrDefault(r => r.CurrentFile == currentFile)?.FileItems.FirstOrDefault(i => i.Name == itemName);

    private void ReloadItems()
    {
        Items.Clear();
        foreach (var row in _package.Rows.OrderBy(r => r.CurrentFile, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var item in row.FileItems.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var candidate = new EditorItemViewModel(row.CurrentFile, item.Name);
                if (string.IsNullOrWhiteSpace(FilterText) || candidate.Display.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
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
