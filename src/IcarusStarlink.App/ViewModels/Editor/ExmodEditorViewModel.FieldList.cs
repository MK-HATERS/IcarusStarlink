using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The selected row's field list: the base-game diff behind its highlights, building and showing it, Add field, Remove field and Add item.</summary>
public sealed partial class ExmodEditorViewModel
{
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
        // had a row with no matching base file) when a later refresh — after a File JSON
        // Apply changed what Rows actually contains — no longer has anything to warn
        // about; a bare "only set when there are warnings" would leave the old message showing
        // even though it no longer applies to the package's current content.
        StatusMessage = report.Warnings.Count > 0 ? report.Warnings[0] : null;
    }

    /// <summary>Shared by OnSelectedItemChanged and a successful Save (which needs the just-saved item's own "was:" indicators to clear immediately, not just on next reselect).</summary>
    private void PopulateFieldsForSelection()
    {
        _allFields.Clear();
        try
        {
            if (SelectedItem is null || FindItem(SelectedItem.CurrentFile, SelectedItem.ItemName) is not { } item)
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
        finally
        {
            ShowFields();
            RefreshAddableFields();
        }
    }

    /// <summary>Every field of the selected row; Fields is the part on show (see ShowFields).</summary>
    private readonly List<EditorFieldViewModel> _allFields = [];

    /// <summary>"Only fields that differ from the game": hides the ones a copied game row carries unchanged.</summary>
    [ObservableProperty]
    private bool _showChangedFieldsOnly;

    partial void OnShowChangedFieldsOnlyChanged(bool value) => ShowFields();

    /// <summary>
    /// Puts the selected row's fields on show, in sections (names, numbers, on/off, links, stats,
    /// lists), each section in the row's own order. With "Only changed fields" on, a field that
    /// matches the game is left out unless it was just added. Not re-run while typing: a field
    /// typed back to the game's value stays in view until the next item or toggle.
    /// </summary>
    private void ShowFields()
    {
        Fields.Clear();
        foreach (var field in _allFields.Select((f, i) => (Field: f, Index: i)).OrderBy(x => x.Field.GroupOrder).ThenBy(x => x.Index).Select(x => x.Field))
        {
            if (!ShowChangedFieldsOnly || field.IsChanged || field.KeepInView)
            {
                Fields.Add(field);
            }
        }

        OnPropertyChanged(nameof(FieldsSummary));
        OnPropertyChanged(nameof(FieldsEmptyText));
    }

    /// <summary>"12 fields, 3 differ from the game" for the selected row; null with nothing picked.</summary>
    public string? FieldsSummary
    {
        get
        {
            if (SelectedItem is null || NoDataRowsMessage is not null)
            {
                return null;
            }

            var changed = _allFields.Count(f => f.IsChanged);
            var text = $"{_allFields.Count} field(s), {changed} differ from the game";
            return Fields.Count < _allFields.Count ? $"{text} (showing {Fields.Count})" : text;
        }
    }

    /// <summary>Why the Fields list is empty for a picked row; null while it shows fields.</summary>
    public string? FieldsEmptyText =>
        SelectedItem is null || Fields.Count > 0 || NoDataRowsMessage is not null ? null
        : _allFields.Count > 0 ? $"No field differs from the game yet. Untick \"Only changed fields\" to see all {_allFields.Count}."
        : "This row has no fields yet. Add one below.";

    /// <summary>Adds a field row; its edits keep the list's change badge for the row up to date.</summary>
    private EditorFieldViewModel AddFieldRow(EditorFieldViewModel field, EditorItemViewModel owner)
    {
        field.BeforeWrite = () => SnapshotBeforeFieldEdit(owner.CurrentFile, owner.ItemName, field.FieldName);
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorFieldViewModel.ValueText))
            {
                Recount(owner);
            }
            else if (e.PropertyName == nameof(EditorFieldViewModel.IsChanged))
            {
                OnPropertyChanged(nameof(FieldsSummary));
            }
        };
        _allFields.Add(field);
        return field;
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
            ChoicesFor(SelectedItem.CurrentFile, NewFieldName, parsedValue ?? baseValue)), SelectedItem).KeepInView = true;
        ShowFields();
        RefreshAddableFields();
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
        _allFields.Remove(field);
        Fields.Remove(field);
        OnPropertyChanged(nameof(FieldsSummary));
        OnPropertyChanged(nameof(FieldsEmptyText));
        RefreshAddableFields();
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
}
