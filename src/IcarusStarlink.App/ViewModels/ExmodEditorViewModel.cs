using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
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
    /// Recomputed whenever Rows changes shape (construction, or a raw-JSON Apply) — not on every
    /// keystroke or plain item-selection change. Amber-highlight comparisons
    /// (EditorFieldViewModel.IsChanged) work entirely off the base value snapshotted into each
    /// field row when this was last refreshed, so re-diffing against the real base files on every
    /// edit isn't needed.
    /// </summary>
    private Dictionary<(string CurrentFile, string ItemName, string FieldName), JsonNode?> _baseValuesByKey = [];

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

    public ExmodEditorViewModel(string folderName, ILibraryRepository repository, string dataFolder)
    {
        _repository = repository;
        _folderName = folderName;
        _dataFolder = dataFolder;
        _package = ExmodFolder.Read(repository.GetFolderPath(folderName)).Package;

        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault();
    }

    partial void OnSelectedItemChanged(EditorItemViewModel? value)
    {
        Fields.Clear();
        if (value is not null)
        {
            var item = FindItem(value.CurrentFile, value.ItemName);
            if (item is not null)
            {
                foreach (var fieldName in item.Fields.Keys.ToList())
                {
                    var baseValue = _baseValuesByKey.TryGetValue((value.CurrentFile, value.ItemName, fieldName), out var v)
                        ? v
                        : item.Fields[fieldName];
                    Fields.Add(new EditorFieldViewModel(fieldName, item.Fields, baseValue));
                }
            }
        }

        // Keeps the File JSON view "synced" to whatever's selected in the shared Items list, per
        // the spec's "share selection/edits with the Item fields view" — switching the selected
        // item while already in File JSON mode should show *that* item's own file, not whatever
        // was showing before.
        if (ViewMode == ExmodEditorViewMode.FileJson)
        {
            RefreshFileJsonText();
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
        RawJsonStatusMessage = "Applied.";
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

        item.Fields[NewFieldName] = parsedValue;
        var baseValue = _baseValuesByKey.TryGetValue((SelectedItem.CurrentFile, SelectedItem.ItemName, NewFieldName), out var v)
            ? v
            : parsedValue;
        Fields.Add(new EditorFieldViewModel(NewFieldName, item.Fields, baseValue));

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
                Items.Add(new EditorItemViewModel(row.CurrentFile, item.Name));
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
