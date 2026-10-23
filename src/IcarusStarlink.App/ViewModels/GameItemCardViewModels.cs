using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One value on the Editor page: the game's, and the one being typed.</summary>
public sealed partial class GameItemFieldViewModel : ObservableObject
{
    private readonly string _startText;
    private readonly string _gameText;

    private readonly bool _wholeNumber;

    /// <param name="decimalField">Whether the game's table has fractions in this field anywhere (GameTableIndex.IsDecimalField); null: unknown, judge by this value.</param>
    public GameItemFieldViewModel(EditableField field, bool? decimalField = null)
    {
        Field = field;
        _gameText = Display(field.GameValue);
        _startText = Display(field.CurrentValue ?? field.GameValue);
        _valueText = _startText;
        // A stack size or slot count is a whole number in the game; 2.5 would be read as 2 or
        // rejected. Stats always are (an int each). Otherwise it's the FIELD that decides: a
        // Weight of 100 here is still a real number when other items weigh 0.5.
        _wholeNumber = IsStat(field)
            || (decimalField is { } isDecimal
                ? !isDecimal
                : (field.GameValue ?? field.CurrentValue) is JsonValue v && ScaleRules.TryReadNumber(v, out var n) && n == Math.Floor(n) && !v.ToJsonString().Contains('.'));
    }

    private static bool IsStat(EditableField field) =>
        field.Path.Steps.Count > 0 && field.Path.Steps[^1] is string key && GameItemEditor.IsStatKey(key);

    /// <summary>The field's path in IsDecimalField's terms: list positions match any element.</summary>
    internal static IReadOnlyList<string> DecimalSteps(EditPath path) =>
        [.. path.Steps.Select(s => s is string key ? key : GameTableIndex.AnyElement)];

    /// <summary>Set by the card for a value the user added (a stat): "back to the game's" takes it out again.</summary>
    internal Action<GameItemFieldViewModel>? RemoveAdded { get; set; }

    public EditableField Field { get; }

    public string Label => Field.Label;

    public string Description => Field.Description;

    public bool IsText => Field.Kind == EditableKind.Text;

    public string GameText => Field.GameValue is null ? "game: not set" : $"game: {_gameText}{(Field.Inherited ? " (table default)" : "")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffersFromGame), nameof(IsPending), nameof(Error))]
    private string _valueText;

    /// <summary>Orange border: what the game will see differs from the game's own value.</summary>
    public bool DiffersFromGame => ValueText.Trim() != _gameText;

    /// <summary>Typed here and not saved yet.</summary>
    public bool IsPending => ValueText.Trim() != _startText && Error is null;

    /// <summary>A value the game doesn't have (a stat the mod adds), emptied by ↺: it comes out of the mod again.</summary>
    private bool IsRemoved => Field.GameValue is null && ValueText.Trim().Length == 0;

    public string? Error =>
        Field.Kind != EditableKind.Number || IsRemoved ? null
        : !double.TryParse(ValueText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) ? "Needs a number, like 50 or 0.5."
        : _wholeNumber && n != Math.Floor(n) ? (IsStat(Field) ? "Needs a whole number: stats have no fractions in the game, like 15." : "Needs a whole number here, like 50.")
        : null;

    /// <summary>
    /// ↺: back to the game's value. For a value the game doesn't have, that means taking it out:
    /// one added here just goes; one the target mod adds is emptied, and saving removes it.
    /// </summary>
    [RelayCommand]
    private void UseGameValue()
    {
        if (Field.GameValue is null && RemoveAdded is { } remove)
        {
            remove(this);
            return;
        }

        ValueText = _gameText;
    }

    /// <summary>The value to write: null means "the game's again" (for a value the game doesn't have: none at all).</summary>
    public JsonNode? NewValue()
    {
        if ((ValueText.Trim() == _gameText && Field.GameValue is not null) || IsRemoved)
        {
            return null;
        }

        if (Field.Kind == EditableKind.Text)
        {
            return JsonValue.Create(ValueText);
        }

        var number = double.Parse(ValueText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return number == Math.Floor(number) && Math.Abs(number) < int.MaxValue ? JsonValue.Create((long)number) : JsonValue.Create(number);
    }

    internal static string Display(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => NsLocText.TextOf(s) ?? s,
        _ when ScaleRules.TryReadNumber(value, out var n) => n.ToString("0.######", CultureInfo.InvariantCulture),
        _ => value.ToJsonString(),
    };
}

/// <summary>One game row the item uses, as a card on the Editor page.</summary>
public sealed partial class GameItemSectionViewModel : ObservableObject
{
    private readonly Action _changed;

    public GameItemSectionViewModel(EditableSection section, IReadOnlyList<StatChoice> stats, Action changed, GameTableIndex? game = null)
    {
        Section = section;
        Picker = new StatPickerViewModel(stats, AddStat);
        _changed = changed;
        foreach (var field in section.Fields)
        {
            var decimalField = field.Kind == EditableKind.Number ? game?.IsDecimalField(section.CurrentFile, field.Path.Top, GameItemFieldViewModel.DecimalSteps(field.Path)) : null;
            Add(new GameItemFieldViewModel(field, decimalField));
        }
    }

    public EditableSection Section { get; }

    public string Title => Section.Title;

    public string Source => $"{Section.Table} › {Section.RowName}";

    public bool IsShared => Section.SharedBy > 1;

    public string SharedText => Section.IsOwnCopy ? "this item's own copy" : IsShared ? $"shared by {Section.SharedBy:N0} items" : "this item only";

    public string AllItemsText => $"Change it for all {Section.SharedBy:N0}";

    public ObservableCollection<GameItemFieldViewModel> Fields { get; } = [];

    /// <summary>For a shared row: give this item its own copy (true) or change it for every item using it (false).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeForAll))]
    private bool _ownCopy = true;

    public bool ChangeForAll
    {
        get => !OwnCopy;
        set => OwnCopy = !value;
    }

    partial void OnOwnCopyChanged(bool value) => _changed();

    public bool CanAddStat => Section.StatGroups.Count > 0;

    /// <summary>"Add a stat" for this card (cards with a stats list only).</summary>
    public StatPickerViewModel Picker { get; }

    private string? AddStat(StatChoice stat)
    {
        var key = GameItemEditor.StatKeyFor(stat.Name);
        var group = Section.StatGroups[0];
        if (Fields.Any(f => f.Field.Path.Top == group && f.Field.Path.Steps.Count == 1 && (string)f.Field.Path.Steps[0] == key))
        {
            return "That stat is already here.";
        }

        Add(new GameItemFieldViewModel(new EditableField(new EditPath(group, [key]), stat.Label, $"Added by you: a {stat.Kind}. Higher = more, lower = less; 0 = no bonus. ↺ takes it out again.", EditableKind.Number, null, null, false))
        {
            ValueText = "0",
            RemoveAdded = field =>
            {
                Fields.Remove(field);
                _changed();
            },
        });
        _changed();
        return null;
    }

    private void Add(GameItemFieldViewModel field)
    {
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GameItemFieldViewModel.ValueText))
            {
                _changed();
            }
        };
        Fields.Add(field);
    }
}
