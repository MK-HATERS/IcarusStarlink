using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A row a drop-down can point at: its row name, and how the list shows it ("Crafting Bench  (Crafting_Bench)").</summary>
public sealed record LinkChoice(string Name, string Display)
{
    public override string ToString() => Display;
}

/// <summary>One value on the Editor page: the game's, and the one being typed.</summary>
public sealed partial class GameItemFieldViewModel : ObservableObject
{
    private readonly string _startText;
    private readonly string _gameText;

    private readonly bool _wholeNumber;

    /// <param name="decimalField">Whether the game's table has fractions in this field anywhere (GameTableIndex.IsDecimalField); null: unknown, judge by this value.</param>
    /// <param name="linkChoices">For a drop-down: the rows it can point at.</param>
    public GameItemFieldViewModel(EditableField field, bool? decimalField = null, IReadOnlyList<LinkChoice>? linkChoices = null)
    {
        Field = field;
        LinkChoices = linkChoices;
        _gameText = EditorValueText.Display(field.GameValue);
        _startText = EditorValueText.Display(field.CurrentValue ?? field.GameValue);
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
        field.Path.Steps.Count > 0 && field.Path.Steps[^1] is string key && GameStats.IsStatKey(key);

    /// <summary>The field's path in IsDecimalField's terms: list positions match any element.</summary>
    internal static IReadOnlyList<string> DecimalSteps(EditPath path) =>
        [.. path.Steps.Select(s => s is string key ? key : GameTableIndex.AnyElement)];

    /// <summary>Set by the card for a value the user added (a stat): "back to the game's" takes it out again.</summary>
    internal Action<GameItemFieldViewModel>? RemoveAdded { get; set; }

    public EditableField Field { get; }

    public string Label => Field.Label;

    public string Description => Field.Description;

    public bool IsText => Field.Kind == EditableKind.Text;

    public bool IsNumber => Field.Kind == EditableKind.Number;

    /// <summary>On or off: a tick box instead of a text box.</summary>
    public bool IsToggle => Field.Kind == EditableKind.Toggle;

    /// <summary>A row of another table: a drop-down instead of a text box.</summary>
    public bool IsLink => Field.Kind == EditableKind.Link;

    /// <summary>Typed into a box (a number or text).</summary>
    public bool ShowsTextBox => !IsToggle && !IsLink;

    /// <summary>The rows a drop-down can point at (null for other kinds).</summary>
    public IReadOnlyList<LinkChoice>? LinkChoices { get; }

    /// <summary>The drop-down's pick. While a search typed into it matches no row it reads null: that isn't a pick, the value stays.</summary>
    public string? LinkValue
    {
        get => ValueText.Trim();
        set
        {
            if (value is not null)
            {
                ValueText = value;
            }
        }
    }

    /// <summary>The tick box: "true" / "false" underneath, like the game's JSON.</summary>
    public bool IsOn
    {
        get => string.Equals(ValueText.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        set => ValueText = value ? "true" : "false";
    }

    /// <summary>Takes whole numbers only (a stat, a stack size): a bulk multiply rounds for it.</summary>
    internal bool IsWholeNumber => _wholeNumber;

    public string GameText => Field.GameValue is null ? "game: not set" : $"game: {ShownAs(_gameText)}{(Field.Inherited ? " (table default)" : "")}";

    /// <summary>A link by its in-game name, an on/off value in words; anything else as it is.</summary>
    private string ShownAs(string text) => Field.Kind switch
    {
        EditableKind.Toggle => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ? "on" : "off",
        EditableKind.Link => LinkChoices?.FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase))?.Display ?? text,
        _ => text,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffersFromGame), nameof(IsPending), nameof(Error), nameof(ValueHint), nameof(IsOn), nameof(LinkValue))]
    private string _valueText;

    /// <summary>False while the page's "Only changed values" hides it (set when that's switched, never while typing, so a field doesn't vanish under the cursor).</summary>
    [ObservableProperty]
    private bool _isShown = true;

    /// <summary>A time in seconds read the way people think of it ("= 3 h" for 10800); null for any other field.</summary>
    public string? ValueHint =>
        Label.Contains("(seconds)", StringComparison.OrdinalIgnoreCase) && EditorValueText.TryParseNumber(ValueText, out var seconds) && seconds >= 60
            ? "= " + Duration(seconds)
            : null;

    /// <summary>10800 → "3 h"; 900 → "15 min"; 5400 → "1 h 30 min"; 90061 → "1 d 1 h".</summary>
    internal static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        var parts = new List<string>();
        if (span.Days > 0)
        {
            parts.Add($"{span.Days} d");
        }

        if (span.Hours > 0)
        {
            parts.Add($"{span.Hours} h");
        }

        if (span.Minutes > 0 && span.Days == 0)
        {
            parts.Add($"{span.Minutes} min");
        }

        if (parts.Count == 0 || (span.Seconds > 0 && span.TotalHours < 1))
        {
            parts.Add($"{span.Seconds} s");
        }

        return string.Join(" ", parts);
    }

    /// <summary>Orange border: what the game will see differs from the game's own value.</summary>
    public bool DiffersFromGame => IsDropped || ValueText.Trim() != _gameText.Trim();

    /// <summary>Typed here and not saved yet.</summary>
    public bool IsPending => IsDropped || (IsTyped && Error is null);

    /// <summary>Differs from what was shown, valid or not (a half-typed number is kept when switching items too).</summary>
    public bool IsTyped => IsDropped || ValueText.Trim() != _startText.Trim(); // game text can end in a space ("…resistance. "): not a change

    /// <summary>An entry of a list (a recipe ingredient) that can be taken out with its ×.</summary>
    public bool CanDrop { get; init; }

    /// <summary>Taken out of its list (a recipe ingredient): saved as the list without it; ↺ puts it back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffersFromGame), nameof(IsPending), nameof(Error), nameof(ShowDrop))]
    private bool _isDropped;

    /// <summary>The × beside an ingredient: there while it's in the list.</summary>
    public bool ShowDrop => CanDrop && !IsDropped;

    /// <summary>For an entry added here: the row it names and that row's table ("Stone", "D_ItemsStatic").</summary>
    internal (string Row, string Table)? AddedEntry { get; init; }

    /// <summary>The × beside an ingredient: takes it out of the recipe.</summary>
    [RelayCommand]
    private void Drop() => IsDropped = true;

    /// <summary>A value the game doesn't have (a stat the mod adds), emptied by ↺: it comes out of the mod again.</summary>
    private bool IsRemoved => Field.GameValue is null && ValueText.Trim().Length == 0;

    public string? Error =>
        IsDropped ? null
        : IsLink ? (LinkChoices is { Count: > 0 } choices && !choices.Any(c => string.Equals(c.Name, ValueText.Trim(), StringComparison.OrdinalIgnoreCase)) ? "Pick one from the list." : null)
        : Field.Kind != EditableKind.Number || IsRemoved ? null
        : !EditorValueText.TryParseNumber(ValueText, out var n) ? "Needs a number, like 50 or 0.5."
        : _wholeNumber && n != Math.Floor(n) ? (IsStat(Field) ? "Needs a whole number: stats have no fractions in the game, like 15." : "Needs a whole number here, like 50.")
        : null;

    /// <summary>
    /// ↺: back to the game's value. For a value the game doesn't have, that means taking it out:
    /// one added here just goes; one the target mod adds is emptied, and saving removes it.
    /// </summary>
    [RelayCommand]
    private void UseGameValue()
    {
        if (IsDropped)
        {
            IsDropped = false; // back in the list
            return;
        }

        if (Field.GameValue is null && RemoveAdded is { } remove)
        {
            remove(this);
            return;
        }

        ValueText = _gameText;
    }

    /// <summary>
    /// The × in "Your changes": back to what was shown before typing (the target mod's value, or
    /// the game's). A stat added here and not saved yet just goes again.
    /// </summary>
    [RelayCommand]
    private void UndoChange()
    {
        if (IsDropped)
        {
            IsDropped = false;
            return;
        }

        if (RemoveAdded is { } remove)
        {
            remove(this);
            return;
        }

        ValueText = _startText;
    }

    /// <summary>The value to write: null means "the game's again" (for a value the game doesn't have: none at all).</summary>
    public JsonNode? NewValue()
    {
        if ((ValueText.Trim() == _gameText.Trim() && Field.GameValue is not null) || IsRemoved)
        {
            return null;
        }

        if (Field.Kind == EditableKind.Text)
        {
            return JsonValue.Create(ValueText);
        }

        if (IsToggle)
        {
            return JsonValue.Create(IsOn);
        }

        if (IsLink)
        {
            // As the game spells the row (the list's name, whatever case was typed).
            var name = ValueText.Trim();
            return JsonValue.Create(LinkChoices?.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Name ?? name);
        }

        EditorValueText.TryParseNumber(ValueText, out var number); // Save checks Error first
        return number == Math.Floor(number) && Math.Abs(number) < int.MaxValue ? JsonValue.Create((long)number) : JsonValue.Create(number);
    }
}

/// <summary>One game row the item uses, as a card on the Editor page.</summary>
public sealed partial class GameItemSectionViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <param name="linkChoices">The rows each table's drop-downs offer (the page keeps them per table).</param>
    /// <param name="items">The game's items, for a recipe card's "Add ingredient".</param>
    public GameItemSectionViewModel(EditableSection section, IReadOnlyList<StatChoice> stats, Action changed, GameTableIndex? game = null,
        Func<string, IReadOnlyList<LinkChoice>>? linkChoices = null, Func<IReadOnlyList<ItemChoice>>? items = null)
    {
        Section = section;
        Picker = new StatPickerViewModel(stats, AddStat);
        _changed = changed;
        if (CanEditIngredients && items is not null)
        {
            IngredientPicker = new ItemPickerViewModel(items, AddIngredient);
        }

        foreach (var field in section.Fields)
        {
            var decimalField = field.Kind == EditableKind.Number ? game?.IsDecimalField(section.CurrentFile, field.Path.Top, GameItemFieldViewModel.DecimalSteps(field.Path)) : null;
            Add(new GameItemFieldViewModel(field, decimalField, field.LinkTable is { } table ? linkChoices?.Invoke(table) : null)
            {
                CanDrop = CanEditIngredients && IsIngredient(field.Path),
            });
        }
    }

    private const string Ingredients = "Inputs";

    /// <summary>A recipe card with an ingredient list: ingredients can be added and taken out.</summary>
    public bool CanEditIngredients =>
        Section.Key.StartsWith(GameItemEditor.RecipeSection + ":", StringComparison.Ordinal) && Section.Lists?.ContainsKey(Ingredients) == true;

    /// <summary>"Add ingredient" (recipe cards only).</summary>
    public ItemPickerViewModel? IngredientPicker { get; }

    public bool HasIngredientPicker => IngredientPicker is not null;

    private static bool IsIngredient(EditPath path) => path.Top == Ingredients && path.Steps.Count == 2 && path.Steps[0] is int;

    private string? AddIngredient(ItemChoice item)
    {
        var current = Section.Lists![Ingredients];
        bool Names(GameItemFieldViewModel f, string row) =>
            f.AddedEntry?.Row is { } added ? string.Equals(added, row, StringComparison.OrdinalIgnoreCase)
            : IsIngredient(f.Field.Path) && !f.IsDropped && string.Equals(current.ElementAtOrDefault((int)f.Field.Path.Steps[0])?["Element"]?["RowName"]?.GetValue<string>(), row, StringComparison.OrdinalIgnoreCase);
        if (Fields.Any(f => Names(f, item.Name)))
        {
            return "Already an ingredient: change its number instead.";
        }

        // The same table the recipe's other ingredients name (D_ItemsStatic in the game's recipes).
        var table = current.OfType<JsonObject>().Select(e => e["Element"]?["DataTableName"]?.GetValue<string>()).FirstOrDefault(t => t is not null) ?? "D_ItemsStatic";
        var index = current.Count + Fields.Count(f => f.AddedEntry is not null);
        Add(new GameItemFieldViewModel(new EditableField(new EditPath(Ingredients, [index, "Count"]), $"Needs {item.Title}",
            "Added by you: how many it takes. ↺ takes it out again.", EditableKind.Number, null, null, false), decimalField: false)
        {
            ValueText = "1",
            AddedEntry = (item.Name, table),
            RemoveAdded = field =>
            {
                Fields.Remove(field);
                _changed();
            },
        });
        AllHidden = false;
        _changed();
        return null;
    }

    /// <summary>
    /// What Save writes for this card: each changed value, except that a list with entries taken out
    /// or added is written whole (the entries kept, their numbers as typed, the new ones after).
    /// </summary>
    internal IEnumerable<GameItemChange> Changes()
    {
        var rewritten = Fields.Where(f => f.IsDropped || f.AddedEntry is not null).Select(f => f.Field.Path.Top).ToHashSet(StringComparer.Ordinal);
        foreach (var field in Fields.Where(f => f.IsPending && !rewritten.Contains(f.Field.Path.Top)))
        {
            yield return new GameItemChange(Section.Key, field.Field.Path, field.NewValue());
        }

        foreach (var top in rewritten)
        {
            yield return new GameItemChange(Section.Key, new EditPath(top, []), RewrittenList(top));
        }
    }

    private JsonArray RewrittenList(string top)
    {
        var current = Section.Lists?.GetValueOrDefault(top) ?? [];
        var list = new JsonArray();
        for (var i = 0; i < current.Count; i++)
        {
            var entryFields = Fields.Where(f => f.Field.Path.Top == top && f.AddedEntry is null && f.Field.Path.Steps.Count == 2 && f.Field.Path.Steps[0] is int at && at == i).ToList();
            if (entryFields.Any(f => f.IsDropped) || current[i]?.DeepClone() is not JsonObject entry)
            {
                continue;
            }

            foreach (var field in entryFields.Where(f => f.IsPending))
            {
                entry[(string)field.Field.Path.Steps[1]] = field.NewValue() ?? field.Field.GameValue?.DeepClone();
            }

            list.Add(entry);
        }

        foreach (var added in Fields.Where(f => f.Field.Path.Top == top && f.AddedEntry is not null))
        {
            var (row, table) = added.AddedEntry!.Value;
            list.Add(new JsonObject
            {
                ["Element"] = new JsonObject { ["RowName"] = row, ["DataTableName"] = table },
                [(string)added.Field.Path.Steps[1]] = added.NewValue(),
            });
        }

        return list;
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

    /// <summary>Something was typed on this card (valid or not): switching items keeps it.</summary>
    public bool HasTyped => Fields.Any(f => f.IsTyped);

    /// <summary>"Only changed values" hides every value of this card: it says so instead of showing an empty card.</summary>
    [ObservableProperty]
    private bool _allHidden;

    /// <summary>Hides the values that are still the game's (on), or shows them all (off).</summary>
    public void ShowOnlyChanged(bool on)
    {
        foreach (var field in Fields)
        {
            field.IsShown = !on || field.DiffersFromGame;
        }

        AllHidden = on && Fields.Count > 0 && Fields.All(f => !f.IsShown);
    }

    /// <summary>"Add a stat" for this card (cards with a stats list only).</summary>
    public StatPickerViewModel Picker { get; }

    private string? AddStat(StatChoice stat)
    {
        var key = GameStats.StatKeyFor(stat.Name);
        var group = Section.StatGroups[0];
        if (Fields.Any(f => f.Field.Path.Top == group && f.Field.Path.Steps.Count == 1 && (string)f.Field.Path.Steps[0] == key))
        {
            return "That stat is already here.";
        }

        // Tested in game: some stats do nothing where they'd go (a storage's spoil rate).
        if (KnownGameBehaviour.StatWithNoEffect(Section.Table, group, stat.Name, placedItem: false) is { } why)
        {
            return why;
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
        AllHidden = false;
        _changed();
        return null;
    }

    private void Add(GameItemFieldViewModel field)
    {
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameItemFieldViewModel.ValueText) or nameof(GameItemFieldViewModel.IsDropped))
            {
                _changed();
            }
        };
        Fields.Add(field);
    }
}
