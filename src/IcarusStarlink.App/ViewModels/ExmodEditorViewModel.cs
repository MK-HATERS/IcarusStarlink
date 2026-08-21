using System.Collections.ObjectModel;
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
    private readonly ExmodPackage _package;

    /// <summary>
    /// Computed once at load — not re-run per keystroke or per selection change. Amber-highlight
    /// comparisons (EditorFieldViewModel.IsChanged) work entirely off the base value snapshotted
    /// into each field row at construction, so re-diffing against the real base files on every
    /// edit isn't needed.
    /// </summary>
    private readonly Dictionary<(string CurrentFile, string ItemName, string FieldName), JsonNode?> _baseValuesByKey;

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

    public ExmodEditorViewModel(string folderName, ILibraryRepository repository, string dataFolder)
    {
        _repository = repository;
        _folderName = folderName;
        _package = ExmodFolder.Read(repository.GetFolderPath(folderName)).Package;

        var report = new MergeReport();
        var baseDiff = ExmodBaseDiffer.DiffAgainstBase(_package, dataFolder, new DefaultSemanticClassifier(), report);
        _baseValuesByKey = baseDiff.ToDictionary(c => (c.CurrentFile, c.ItemName, c.FieldName), c => c.OriginalValue);

        if (report.Warnings.Count > 0)
        {
            StatusMessage = report.Warnings[0];
        }

        ReloadItems();
        SelectedItem = Items.FirstOrDefault();
    }

    partial void OnSelectedItemChanged(EditorItemViewModel? value)
    {
        Fields.Clear();
        if (value is null)
        {
            return;
        }

        var item = FindItem(value.CurrentFile, value.ItemName);
        if (item is null)
        {
            return;
        }

        foreach (var fieldName in item.Fields.Keys.ToList())
        {
            var baseValue = _baseValuesByKey.TryGetValue((value.CurrentFile, value.ItemName, fieldName), out var v)
                ? v
                : item.Fields[fieldName];
            Fields.Add(new EditorFieldViewModel(fieldName, item.Fields, baseValue));
        }
    }

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
