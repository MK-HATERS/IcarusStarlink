using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A field the game's row has (or its table's default) that the selected mod row doesn't set yet.</summary>
public sealed record AddableField(string FieldName, string Label, string GameText)
{
    /// <summary>"Stack size  (MaxStack)"; just the name when the plain words are the name.</summary>
    public string Display => string.Equals(Label, FieldName, StringComparison.OrdinalIgnoreCase) ? FieldName : $"{Label}  ({FieldName})";

    /// <summary>What a screen reader says for the field in the list (the record's own text otherwise).</summary>
    public override string ToString() => Display;
}

/// <summary>
/// The Fields view's "Add a field": search the fields the game's row has by plain name and add one
/// with the game's value, ready to change — instead of typing a field name and its JSON by hand
/// (still there, folded away, for a field the game doesn't have).
/// </summary>
public sealed partial class ExmodEditorViewModel
{
    private List<AddableField> _addableFields = [];

    /// <summary>The addable fields that match the search box.</summary>
    public ObservableCollection<AddableField> MatchingAddableFields { get; } = [];

    [ObservableProperty]
    private string _addFieldSearch = "";

    [ObservableProperty]
    private AddableField? _selectedAddableField;

    public int AddableFieldCount => _addableFields.Count;

    public bool HasAddableFields => _addableFields.Count > 0;

    /// <summary>The card's heading: what it offers for this row.</summary>
    public string AddFieldTitle => _addableFields.Count > 0
        ? $"Add a field ({_addableFields.Count} more the game has for this row)"
        : "Add a field";

    partial void OnAddFieldSearchChanged(string value) => FilterAddableFields();

    /// <summary>Re-reads what the selected row could still add (after a pick, an add or a remove).</summary>
    private void RefreshAddableFields()
    {
        _addableFields = [];
        if (SelectedItem is { } selected && HasGameTable(selected.CurrentFile) && FindItem(selected.CurrentFile, selected.ItemName) is { } item)
        {
            var gameRow = GameTables.Row(selected.CurrentFile, selected.ItemName);
            var defaults = GameTables.Defaults(selected.CurrentFile);
            var names = (gameRow?.Select(kv => kv.Key) ?? []).Concat(defaults?.Select(kv => kv.Key) ?? [])
                .Where(k => k != "Name" && !item.Fields.Keys.Contains(k, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (GameValueOf(gameRow, defaults, name) is not { } value)
                {
                    continue;
                }

                var plain = EditorValueText.Plain(value);
                _addableFields.Add(new AddableField(name, GameItemEditor.FieldText(name, ScaleRules.TryReadNumber(value, out _)).Label,
                    "game: " + (plain.Length > 70 ? plain[..67] + "…" : plain)));
            }

            _addableFields.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label));
        }

        OnPropertyChanged(nameof(AddableFieldCount));
        OnPropertyChanged(nameof(HasAddableFields));
        OnPropertyChanged(nameof(AddFieldTitle));
        FilterAddableFields();
    }

    private static JsonNode? GameValueOf(JsonObject? gameRow, JsonObject? defaults, string field) =>
        (gameRow?.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value
         ?? defaults?.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value);

    private void FilterAddableFields()
    {
        var search = AddFieldSearch.Trim();
        MatchingAddableFields.Clear();
        foreach (var field in _addableFields.Where(f => search.Length == 0
            || f.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
            || f.FieldName.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            MatchingAddableFields.Add(field);
        }

        if (SelectedAddableField is not null && !MatchingAddableFields.Contains(SelectedAddableField))
        {
            SelectedAddableField = null;
        }
    }

    /// <summary>Adds the picked field to the selected row with the game's value (undoable). Unchanged, it changes nothing in game.</summary>
    [RelayCommand]
    private void AddGameField(AddableField? field)
    {
        field ??= SelectedAddableField;
        if (SelectedItem is not { } selected || FindItem(selected.CurrentFile, selected.ItemName) is not { } item)
        {
            StatusMessage = "Select an item first.";
            return;
        }

        if (field is null)
        {
            StatusMessage = "Pick a field in the list first.";
            return;
        }

        if (GameValueOf(GameTables.Row(selected.CurrentFile, selected.ItemName), GameTables.Defaults(selected.CurrentFile), field.FieldName)?.DeepClone() is not { } value
            || item.Fields.Keys.Contains(field.FieldName, StringComparer.OrdinalIgnoreCase))
        {
            RefreshAddableFields();
            return;
        }

        PushUndoSnapshot();
        item.Fields[field.FieldName] = value;
        RefreshBaseDiff();
        var baseValue = BaseValueFor(selected.CurrentFile, selected.ItemName, field.FieldName, item);
        AddFieldRow(new EditorFieldViewModel(field.FieldName, item.Fields, baseValue, value, StatChoicesForFields(),
            ChoicesFor(selected.CurrentFile, field.FieldName, value)), selected).KeepInView = true;
        ShowFields();
        AddFieldSearch = "";
        RefreshAddableFields();
        Recount(selected);
        StatusMessage = $"Added {field.Label} with the game's value. Change it here; left as it is, it changes nothing in game.";
    }
}
