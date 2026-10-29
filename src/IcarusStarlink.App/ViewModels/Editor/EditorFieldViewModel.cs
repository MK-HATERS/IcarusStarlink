using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>How the Fields view shows a field: plainly when it can, as JSON when it can't.</summary>
public enum EditorFieldKind
{
    /// <summary>A number: one box.</summary>
    Number,

    /// <summary>Game text (NSLOCTEXT): what players see, edited as plain text.</summary>
    Text,

    /// <summary>Any other text (a model or sound path): one line, no JSON quotes.</summary>
    String,

    /// <summary>A stats list ("ArmourStats"): one row per stat.</summary>
    Stats,

    /// <summary>Anything else (lists, nested objects): the JSON itself.</summary>
    Raw,

    /// <summary>A true/false switch: a checkbox.</summary>
    Bool,

    /// <summary>A link to a row of another table ("Consumable" → D_Consumable): a dropdown of the rows that exist.</summary>
    Link,

    /// <summary>Text the game only ever sets to a few values ("SnapBehaviour"): a dropdown of those.</summary>
    Choice,
}

/// <summary>
/// What a field may hold, read from the game's own tables: a link field's target table (its
/// Defaults' DataTableName) and the rows there, or the handful of values the game uses for a
/// text field. Lets the editor offer only real values, so a mod can't link to a row that doesn't
/// exist (a canteen you can't drink from).
/// </summary>
public sealed record FieldChoices(string? LinkTable, IReadOnlyList<string> Options);

/// <summary>
/// One editable field row in the EXMOD editor's right-hand pane. Writes straight back into the
/// owning item's own Fields dictionary on every successfully-parsed edit — the in-memory
/// ExmodPackage the editor holds is always live/current, so ExmodEditorViewModel.Save doesn't
/// need a separate "commit the currently-displayed pane" step when the user switches items
/// without saving first. ValueText (the field's JSON) stays the one source of truth; the plain
/// views (number, text, one row per stat) read and write through it.
/// </summary>
public sealed partial class EditorFieldViewModel : ObservableObject
{
    private readonly Dictionary<string, JsonNode?> _fields;
    private readonly JsonNode? _baseValue;
    private readonly JsonNode? _originalValue;
    private bool _syncingStats;

    public string FieldName { get; }
    public string BaseValueText { get; }

    /// <summary>What this field held when the editor was opened (or last saved) — "was: X", shown only when it differs from the live value. A separate reference point from base/vanilla-game.</summary>
    public string OriginalValueText { get; }

    public EditorFieldKind Kind { get; }

    /// <summary>The field in plain words ("Stack size" for MaxStack).</summary>
    public string Label { get; }

    /// <summary>What lower and higher values do, for numbers.</summary>
    public string Hint { get; }

    public bool IsNumberKind => Kind == EditorFieldKind.Number;
    public bool IsTextKind => Kind == EditorFieldKind.Text;
    public bool IsStringKind => Kind == EditorFieldKind.String;
    public bool IsStatsKind => Kind == EditorFieldKind.Stats;
    public bool IsRawKind => Kind == EditorFieldKind.Raw;
    public bool IsBoolKind => Kind == EditorFieldKind.Bool;
    public bool IsListKind => Kind is EditorFieldKind.Link or EditorFieldKind.Choice;

    /// <summary>The values a Link or Choice field may take (rows of the target table, or the game's own values).</summary>
    public IReadOnlyList<string> Options { get; } = [];

    /// <summary>The table a Link field points into ("D_Consumable").</summary>
    public string? LinkTable { get; }

    /// <summary>"game: 6" — the vanilla value in plain words (the text, not NSLOCTEXT; "not in the game" for a field the game doesn't set).</summary>
    public string BasePlainText { get; }

    public bool HasHint => Hint.Length > 0;

    /// <summary>The Fields view's section for this field, by what kind of value it is ("Numbers", "Names and text"…).</summary>
    public string Group => Kind switch
    {
        EditorFieldKind.Text or EditorFieldKind.String => "Names and text",
        EditorFieldKind.Number => "Numbers",
        EditorFieldKind.Bool => "On or off",
        EditorFieldKind.Link or EditorFieldKind.Choice => "Links and choices",
        EditorFieldKind.Stats => "Stats",
        _ => "Lists and other values",
    };

    /// <summary>Where Group sits in the view: names first, the raw JSON last.</summary>
    internal int GroupOrder => Kind switch
    {
        EditorFieldKind.Text or EditorFieldKind.String => 0,
        EditorFieldKind.Number => 1,
        EditorFieldKind.Bool => 2,
        EditorFieldKind.Link or EditorFieldKind.Choice => 3,
        EditorFieldKind.Stats => 4,
        _ => 5,
    };

    /// <summary>↺ shows: the value differs from the game's, and the game has one to go back to.</summary>
    public bool CanUseGameValue => IsChanged && _baseValue is not null;

    /// <summary>Added in this sitting (Add a field): stays in view even with "Only changed fields" on, while it still has the game's value.</summary>
    internal bool KeepInView { get; set; }

    /// <summary>One row per stat (Stats fields only), including game stats this mod's list leaves out.</summary>
    public ObservableCollection<StatEntryViewModel> Stats { get; } = [];

    /// <summary>"Add a stat" (Stats fields only, when the game's stat list is known).</summary>
    public StatPickerViewModel? Picker { get; }

    public bool HasPicker => Picker is not null;

    /// <summary>Called just before a new value is written into the item (the editor's undo snapshot).</summary>
    internal Action? BeforeWrite { get; set; }

    [ObservableProperty]
    private string _valueText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseGameValue))]
    private bool _isChanged;

    /// <summary>True when the live value differs from OriginalValueText — drives whether the "was: X" line shows at all.</summary>
    [ObservableProperty]
    private bool _isChangedFromOriginal;

    [ObservableProperty]
    private string? _parseError;

    private bool _textNotWritten;

    /// <summary>
    /// What the box (or a stat row, or the dropdown) shows isn't in the mod: it didn't parse, so the
    /// last good value stays. Save refuses while any shown field is like this, or it said "Saved."
    /// over a value it never wrote. (A link already pointing nowhere is only a warning, not this.)
    /// </summary>
    public bool HasUnwrittenText => _textNotWritten || Stats.Any(s => !s.IsDropped && s.Error is not null);

    public EditorFieldViewModel(string fieldName, Dictionary<string, JsonNode?> fields, JsonNode? baseValue, JsonNode? originalValue, IReadOnlyList<StatChoice>? statChoices = null,
        FieldChoices? choices = null)
    {
        FieldName = fieldName;
        _fields = fields;
        _baseValue = baseValue;
        _originalValue = originalValue;
        BaseValueText = FormatForDisplay(baseValue);
        OriginalValueText = FormatForDisplay(originalValue);
        var current = fields.TryGetValue(fieldName, out var value) ? value : null;
        _valueText = FormatForDisplay(current);

        Kind = KindOf(fieldName, current ?? baseValue, choices);
        if (Kind is EditorFieldKind.Link or EditorFieldKind.Choice)
        {
            Options = choices!.Options;
            LinkTable = choices.LinkTable;
        }
        (Label, Hint) = GameItemEditor.FieldText(fieldName, Kind == EditorFieldKind.Number);
        BasePlainText = baseValue is null ? "not in the game" : $"game: {EditorValueText.Plain(baseValue)}";
        if (Kind == EditorFieldKind.String && (CurrentString() ?? "").Contains("/Game/", StringComparison.Ordinal))
        {
            Hint = "A game file this uses (a model, texture or sound). Use another game file's path, or ✕ to keep the game's.";
        }
        if (Kind == EditorFieldKind.Link)
        {
            Hint = $"Links to a row of {LinkTable}: pick one the game (or this mod) has. A link to a row that doesn't exist does nothing in game.";
            // Say so straight away when the mod's link already points nowhere (an old copy of the game's data).
            if (SelectedOption is { Length: > 0 } linked && !Options.Contains(linked, StringComparer.OrdinalIgnoreCase))
            {
                ParseError = $"{LinkTable} has no row '{linked}', so this does nothing in game"
                    + (baseValue?["RowName"]?.ToString() is { Length: > 0 } gameRow ? $" (the game uses '{gameRow}')." : ".");
            }
        }
        else if (Kind == EditorFieldKind.Choice)
        {
            Hint = "The values the game uses for this.";
        }

        if (Kind == EditorFieldKind.Stats)
        {
            Hint = "A mod's list replaces the game's whole list: a game stat left out is gone in game. ✕ removes a stat; Put back restores one.";
            BuildStats(current as JsonObject);
            if (statChoices is { Count: > 0 })
            {
                Picker = new StatPickerViewModel(statChoices, AddStat);
            }
        }

        RecomputeChanged();
    }

    /// <summary>Game text as players see it; typing keeps the game's NSLOCTEXT table and key.</summary>
    public string PlainText
    {
        get => CurrentString() is { } s ? NsLocText.TextOf(s) ?? s : "";
        set
        {
            var previous = CurrentString();
            var encoded = previous is not null && NsLocText.TryParse(previous, out var table, out var key, out _)
                ? NsLocText.Make(table, key, value)
                : value;
            ValueText = JsonValue.Create(encoded)!.ToJsonString(ExmodJson.DisplayOptions);
            OnPropertyChanged();
        }
    }

    /// <summary>A true/false field as a checkbox.</summary>
    public bool BoolValue
    {
        get => ((_fields.TryGetValue(FieldName, out var v) ? v : null) ?? _baseValue) is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
        set
        {
            ValueText = value ? "true" : "false";
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// A Link field's row name, or a Choice field's value. Only a value in Options is written (in the
    /// game's spelling); anything else shows why and leaves the field as it was.
    /// </summary>
    public string SelectedOption
    {
        get => Kind == EditorFieldKind.Link
            ? ((_fields.TryGetValue(FieldName, out var v) ? v : null) ?? _baseValue)?["RowName"]?.ToString() ?? ""
            : CurrentString() ?? "";
        set
        {
            var match = Options.FirstOrDefault(o => string.Equals(o, value?.Trim(), StringComparison.OrdinalIgnoreCase));
            _textNotWritten = match is null;
            if (match is null)
            {
                ParseError = Kind == EditorFieldKind.Link
                    ? $"{LinkTable} has no row '{value}'. Pick one from the list."
                    : "Pick one of the values in the list.";
                return;
            }

            if (Kind == EditorFieldKind.Link)
            {
                // Keep the link's other parts (its DataTableName); only the row changes.
                var link = ((_fields.TryGetValue(FieldName, out var v) ? v : null) ?? _baseValue)?.DeepClone() as JsonObject ?? new JsonObject();
                link["RowName"] = match;
                ValueText = link.ToJsonString(ExmodJson.DisplayOptions);
            }
            else
            {
                ValueText = JsonValue.Create(match)!.ToJsonString(ExmodJson.DisplayOptions);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>A plain text field (a model path) without JSON quotes.</summary>
    public string PlainString
    {
        get => CurrentString() ?? "";
        set
        {
            ValueText = JsonValue.Create(value)!.ToJsonString(ExmodJson.DisplayOptions);
            OnPropertyChanged();
        }
    }

    partial void OnValueTextChanged(string value)
    {
        try
        {
            var parsed = string.IsNullOrWhiteSpace(value) ? null : JsonNode.Parse(value);
            // An empty number box would write null, which takes the field out of the mod without a word.
            if (Kind == EditorFieldKind.Number && (parsed is null || !ScaleRules.TryReadNumber(parsed, out var number) || !double.IsFinite(number)))
            {
                throw new FormatException("needs a number");
            }

            // Typed as JSON (a stats field, a raw value): stats are whole numbers in the game.
            if (GameStats.FractionalStat(parsed is JsonObject ? parsed : new JsonObject { [FieldName] = parsed?.DeepClone() }) is { } fractional)
            {
                throw new System.IO.InvalidDataException($"Stats are whole numbers in the game, so {fractional} would lose its fraction. Use a whole number.");
            }

            if (!_fields.TryGetValue(FieldName, out var previous) || !JsonNode.DeepEquals(previous, parsed))
            {
                BeforeWrite?.Invoke();
            }

            _fields[FieldName] = parsed;
            ParseError = null;
            _textNotWritten = false;
        }
        catch (Exception ex)
        {
            _textNotWritten = true;
            // Deliberately doesn't write back — _fields keeps whatever the last successfully-
            // parsed value was until this becomes valid JSON again, so a mid-typing invalid state
            // never corrupts the in-memory package.
            ParseError = Kind == EditorFieldKind.Number && ex is not System.IO.InvalidDataException ? "Needs a number, like 50 or 0.5." : ex.Message;
        }

        RecomputeChanged();
    }

    /// <summary>
    /// ↺: the game's own value again, in every view of the field (the box, the checkbox, the
    /// dropdown, each stat row). The field stays in the mod with that value, which changes nothing
    /// in game (Save drops it when the game's row sets it). Undoable like any edit.
    /// </summary>
    [RelayCommand]
    private void UseGameValue()
    {
        if (_baseValue is null)
        {
            return;
        }

        if (Kind == EditorFieldKind.Stats)
        {
            _syncingStats = true;
            Stats.Clear();
            BuildStats(_baseValue as JsonObject);
        }

        ValueText = FormatForDisplay(_baseValue);
        OnPropertyChanged(nameof(PlainText));
        OnPropertyChanged(nameof(PlainString));
        OnPropertyChanged(nameof(BoolValue));
        OnPropertyChanged(nameof(SelectedOption));
    }

    /// <summary>Called by a stat row: rebuilds this field's JSON from the rows.</summary>
    internal void StatsChanged()
    {
        if (_syncingStats)
        {
            return;
        }

        var obj = new JsonObject();
        foreach (var stat in Stats.Where(s => !s.IsDropped))
        {
            // A row being typed into (not a number yet) keeps its last good value, never 0.
            obj[stat.Key] = stat.LastGoodValue?.DeepClone();
        }

        ValueText = obj.ToJsonString(ExmodJson.DisplayOptions);
    }

    private string? AddStat(StatChoice choice)
    {
        var key = GameStats.StatKeyFor(choice.Name);
        if (Stats.FirstOrDefault(s => s.Key == key) is { } existing)
        {
            if (!existing.IsDropped)
            {
                return "That stat is already here.";
            }

            existing.PutBackCommand.Execute(null);
            return null;
        }

        Stats.Add(new StatEntryViewModel(this, key, null, JsonValue.Create(0)));
        StatsChanged();
        return null;
    }

    private void BuildStats(JsonObject? current)
    {
        _syncingStats = true;
        var game = _baseValue as JsonObject;
        foreach (var (key, value) in current ?? [])
        {
            Stats.Add(new StatEntryViewModel(this, key, game?[key], value));
        }

        foreach (var (key, value) in game ?? [])
        {
            if (current?.ContainsKey(key) != true)
            {
                Stats.Add(new StatEntryViewModel(this, key, value, null) { IsDropped = true });
            }
        }

        _syncingStats = false;
    }

    private string? CurrentString() =>
        ((_fields.TryGetValue(FieldName, out var v) ? v : null) ?? _baseValue) is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    private static EditorFieldKind KindOf(string field, JsonNode? value, FieldChoices? choices) => value switch
    {
        JsonValue b when b.GetValueKind() is JsonValueKind.True or JsonValueKind.False => EditorFieldKind.Bool,
        JsonObject o when choices?.LinkTable is not null && choices.Options.Count > 0 && o["RowName"] is JsonValue && o.Count <= 2 => EditorFieldKind.Link,
        JsonValue s when choices is { LinkTable: null, Options.Count: > 1 } && s.TryGetValue<string>(out var text) && NsLocText.TextOf(text) is null => EditorFieldKind.Choice,
        _ when ScaleRules.TryReadNumber(value, out _) => EditorFieldKind.Number,
        JsonValue v when v.TryGetValue<string>(out var s) => NsLocText.TextOf(s) is not null ? EditorFieldKind.Text : EditorFieldKind.String,
        _ when GameStats.IsStatsObject(field, value) => EditorFieldKind.Stats,
        _ => EditorFieldKind.Raw,
    };

    private void RecomputeChanged()
    {
        try
        {
            var parsed = string.IsNullOrWhiteSpace(ValueText) ? null : JsonNode.Parse(ValueText);
            IsChanged = !JsonNode.DeepEquals(parsed, _baseValue);
            IsChangedFromOriginal = !JsonNode.DeepEquals(parsed, _originalValue);
        }
        catch
        {
            IsChanged = true;
            IsChangedFromOriginal = true;
        }
    }

    private static string FormatForDisplay(JsonNode? value) => value?.ToJsonString(ExmodJson.DisplayOptions) ?? "null";
}

/// <summary>One stat in a stats field: "Backpack slots" 72, game 6.</summary>
public sealed partial class StatEntryViewModel : ObservableObject
{
    private readonly EditorFieldViewModel _owner;
    private readonly JsonNode? _gameValue;

    public StatEntryViewModel(EditorFieldViewModel owner, string key, JsonNode? gameValue, JsonNode? value)
    {
        _owner = owner;
        _gameValue = gameValue;
        Key = key;
        StatName = GameStats.StatNameOf(key);
        Label = GameStats.StatLabel(key);
        Hint = GameStats.StatHint(StatName);
        _valueText = EditorValueText.Plain(value ?? gameValue);
        LastGoodValue = (value ?? gameValue)?.DeepClone();
    }

    /// <summary>What the row writes: its last valid number (the original value until one is typed).</summary>
    internal JsonNode? LastGoodValue { get; private set; }

    public string Key { get; }

    public string StatName { get; }

    public string Label { get; }

    public string Hint { get; }

    /// <summary>Not in the game's list: this mod adds it.</summary>
    public bool IsAdded => _gameValue is null;

    public string GameText => _gameValue is null ? "added by this mod" : IsDropped ? $"game: {EditorValueText.Plain(_gameValue)} · left out, so gone in game" : $"game: {EditorValueText.Plain(_gameValue)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(Error))]
    private string _valueText;

    /// <summary>In the game's list but not in this mod's: the game drops it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameText), nameof(IsKept), nameof(IsChanged))]
    private bool _isDropped;

    public bool IsKept => !IsDropped;

    public bool IsChanged => !IsDropped && (_gameValue is null || !ScaleRules.TryReadNumber(_gameValue, out var game) || NumberValue() != game);

    /// <summary>Stats are whole numbers in the game (an int each), so 5.5 is refused, not rounded behind the user's back.</summary>
    public string? Error => NumberValue() is null ? "Needs a whole number, like 15 or -50 (stats have no fractions)." : null;

    partial void OnValueTextChanged(string value)
    {
        if (NumberValue() is { } n)
        {
            LastGoodValue = JsonValue.Create((long)n);
            _owner.StatsChanged();
        }
    }

    internal double? NumberValue() => EditorValueText.TryParseWholeNumber(ValueText, out var n) ? n : null;

    [RelayCommand]
    private void Remove()
    {
        if (IsAdded)
        {
            _owner.Stats.Remove(this);
        }
        else
        {
            IsDropped = true;
        }

        _owner.StatsChanged();
    }

    [RelayCommand]
    private void PutBack()
    {
        IsDropped = false;
        if (_gameValue is not null && NumberValue() is null)
        {
            ValueText = EditorValueText.Plain(_gameValue); // rebuilds the field itself
            return;
        }

        _owner.StatsChanged();
    }
}
