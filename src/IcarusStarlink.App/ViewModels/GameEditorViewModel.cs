using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
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

/// <summary>A mod the Editor can save into: "New mod", or one of the user's own mods.</summary>
public sealed record EditorSaveTarget(string? FolderName, string Display)
{
    public bool IsNew => FolderName is null;

    /// <summary>The closed dropdown shows this (the record's own text was "EditorSaveTarget { FolderName = … }").</summary>
    public override string ToString() => Display;
}

/// <summary>A quick-start button on the Editor's empty state: a common item to change, and what people usually change on it.</summary>
public sealed record EditorQuickStart(ItemChoice Item, string Hint)
{
    public string Label => Item.Title;
}

/// <summary>
/// The Editor page: change the game's OWN items (the default Envirosuit's weight, Bread's buff) —
/// the counterpart of New mod, which only ever adds new things. Pick an item, change values, and
/// save into a new mod or one of your own mods (never a downloaded one: an update would replace it,
/// and it isn't yours). Built on GameItemEditor, which writes only what changed.
/// </summary>
public sealed partial class GameEditorViewModel : ObservableObject
{
    private readonly ILibraryRepository _repository;
    private readonly IActivityLog? _activityLog;
    private readonly string _dataFolder;
    private GameTableIndex? _game;
    private IReadOnlyList<ItemChoice> _allItems = [];
    private IReadOnlyList<StatChoice>? _statChoices;
    private bool _refreshingFilter;

    /// <param name="activityLog">Saves and failed saves also go to the app-wide activity feed (null in tests).</param>
    public GameEditorViewModel(ILibraryRepository repository, string dataFolder, IActivityLog? activityLog = null)
    {
        _repository = repository;
        _activityLog = activityLog;
        _dataFolder = dataFolder;
        NewModAuthor = Environment.UserName;
        // Game data changes under the page when Settings' Update data folder runs.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((GameEditorViewModel)recipient).ResetGameData());
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((GameEditorViewModel)recipient).RefreshTargets());
        WeakReferenceMessenger.Default.Register<OpenGameItemMessage>(this, (recipient, message) => ((GameEditorViewModel)recipient).Open(message.ItemName, message.TargetFolder));
        RefreshTargets();
    }

    public string Title => "Editor";

    public ObservableCollection<ItemChoice> FilteredItems { get; } = [];

    public ObservableCollection<GameItemSectionViewModel> Sections { get; } = [];

    public ObservableCollection<EditorSaveTarget> Targets { get; } = [];

    /// <summary>Row names (D_ItemsStatic) offered on the empty state, checked against the 2026 game data; one missing from the loaded data is just left out.</summary>
    internal static readonly IReadOnlyList<(string Name, string Hint)> QuickStartItems =
    [
        ("Envirosuit", "weight, armour, temperature"),
        ("Canteen", "how much water it holds"),
        ("Food_Bread", "its food buff"),
        ("Wood_Crate_Medium", "storage slots"),
        ("Metal_Pickaxe", "durability, mining"),
        ("Basic_Backpack", "inventory slots"),
    ];

    /// <summary>The quick starts present in the loaded game data.</summary>
    public ObservableCollection<EditorQuickStart> QuickStarts { get; } = [];

    [RelayCommand]
    private void OpenQuickStart(EditorQuickStart? quickStart)
    {
        if (quickStart is not null)
        {
            Open(quickStart.Item.Name);
        }
    }

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem), nameof(ShowPickPrompt))]
    private ItemChoice? _selectedItem;

    public bool HasItem => SelectedItem is not null && Sections.Count > 0;

    public bool ShowPickPrompt => !HasItem && NoDataMessage is null;

    [ObservableProperty]
    private string? _itemTitle;

    [ObservableProperty]
    private string? _keptAsGames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPickPrompt))]
    private string? _noDataMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewTarget))]
    private EditorSaveTarget? _selectedTarget;

    public bool IsNewTarget => SelectedTarget?.IsNew != false;

    [ObservableProperty]
    private string _newModName = "";

    [ObservableProperty]
    private string _newModAuthor;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private string _pendingSummary = "Nothing changed yet.";

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>The mod just saved into, for "Turn on & go to Merge & Install".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedMod))]
    private string? _savedModFolder;

    public bool HasSavedMod => SavedModFolder is not null;

    [RelayCommand]
    private void TurnOnSavedMod()
    {
        if (SavedModFolder is { } folder)
        {
            WeakReferenceMessenger.Default.Send(new TurnOnModMessage(folder));
        }
    }

    /// <summary>Lazily: the page may never be opened in a session.</summary>
    private GameTableIndex? Game
    {
        get
        {
            if (_game is null && Directory.Exists(_dataFolder))
            {
                _game = new GameTableIndex(_dataFolder);
                if (_game.CurrentFileFor("D_ItemsStatic") is null)
                {
                    _game = null;
                }
            }

            return _game;
        }
    }

    /// <summary>Called when the page is shown: loads the item list the first time.</summary>
    public void EnsureLoaded()
    {
        if (_allItems.Count > 0)
        {
            return;
        }

        if (Game is not { } game)
        {
            NoDataMessage = "No game data yet. Run Refresh game data in Settings first; the Editor reads the game's items from it.";
            return;
        }

        NoDataMessage = null;
        _allItems = ItemCloner.ListItems(game);
        QuickStarts.Clear();
        foreach (var (name, hint) in QuickStartItems)
        {
            if (_allItems.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is { } item)
            {
                QuickStarts.Add(new EditorQuickStart(item, hint));
            }
        }

        RefreshFilter();
    }

    /// <summary>
    /// Shows one game item (from the mod editor's "Open in Editor"). With the mod it came from, and
    /// that's one of the user's own mods, that mod becomes the save target, so the values shown are
    /// the mod's and saving goes back into it (not the game's values into whatever was picked before).
    /// </summary>
    public void Open(string itemName, string? targetFolder = null)
    {
        EnsureLoaded();
        if (_allItems.FirstOrDefault(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase)) is not { } item)
        {
            return;
        }

        SelectedItem = item;
        RefreshFilter(); // the list shows 400 items; make sure this one is in it
        if (SelectedItem != item)
        {
            return; // kept the item being edited (typed changes not thrown away)
        }

        if (targetFolder is not null && Targets.FirstOrDefault(t => t.FolderName == targetFolder) is { } target)
        {
            SelectedTarget = target;
        }
    }

    partial void OnFilterTextChanged(string value) => RefreshFilter();

    /// <summary>Asks before typed changes are thrown away (switching item, Discard). Tests swap in an answer.</summary>
    internal Func<string, bool> ConfirmDiscard { get; set; } = message =>
        Views.ThemedConfirmDialog.Show(System.Windows.Application.Current?.MainWindow, message, "Unsaved changes", Views.ThemedConfirmSeverity.Warning);

    /// <summary>The item whose cards are showing (SelectedItem can move ahead of it while asking).</summary>
    private ItemChoice? _shownItem;

    partial void OnSelectedItemChanged(ItemChoice? value)
    {
        if (_refreshingFilter || value == _shownItem)
        {
            return;
        }

        if (PendingCount > 0 && !ConfirmDiscard($"You changed {PendingCount} value(s) of {ItemTitle} and haven't saved them. Throw them away?"))
        {
            SelectedItem = _shownItem; // stay on the item being edited
            return;
        }

        LoadItem();
    }

    /// <summary>Set while the target list is rebuilt: the list empties under the selection, which isn't a new pick.</summary>
    private bool _refreshingTargets;

    partial void OnSelectedTargetChanged(EditorSaveTarget? value)
    {
        if (!_refreshingTargets)
        {
            ReloadForTarget();
        }
    }

    private void ReloadForTarget()
    {
        // The values shown are the game's with the target mod's changes on top; typed changes come
        // along to the newly chosen mod instead of being lost or applied to values not on screen.
        var typed = Sections.SelectMany(s => s.Fields.Where(f => f.IsPending).Select(f => (Section: s.Section.Key, Path: f.Field.Path.Key, f.ValueText))).ToList();
        var ownCopy = Sections.ToDictionary(s => s.Section.Key, s => s.OwnCopy);
        LoadItem();
        var carried = 0;
        foreach (var (section, path, text) in typed)
        {
            if (Sections.FirstOrDefault(s => s.Section.Key == section)?.Fields.FirstOrDefault(f => f.Field.Path.Key == path) is { } field)
            {
                field.ValueText = text;
                carried++;
            }
        }

        foreach (var section in Sections)
        {
            if (ownCopy.TryGetValue(section.Section.Key, out var own))
            {
                section.OwnCopy = own;
            }
        }

        if (carried < typed.Count)
        {
            StatusMessage = $"{typed.Count - carried} typed change(s) don't apply to that mod's version of this item and were dropped.";
        }
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        if (PendingCount > 0 && !ConfirmDiscard($"Throw away the {PendingCount} change(s) you typed?"))
        {
            return;
        }

        LoadItem();
    }

    [RelayCommand]
    private void Save()
    {
        if (Game is not { } game || SelectedItem is not { } item)
        {
            StatusMessage = "Pick a game item first.";
            return;
        }

        var invalid = Sections.SelectMany(s => s.Fields).FirstOrDefault(f => f.Error is not null);
        if (invalid is not null)
        {
            StatusMessage = $"{invalid.Label}: {invalid.Error}";
            return;
        }

        var changes = Sections.SelectMany(s => s.Fields.Where(f => f.IsPending).Select(f => new GameItemChange(s.Section.Key, f.Field.Path, f.NewValue()))).ToList();
        if (changes.Count == 0)
        {
            StatusMessage = "Nothing to save: change a value first.";
            return;
        }

        if (SelectedTarget is not { } target)
        {
            StatusMessage = "Choose where to save: a new mod, or one of your mods.";
            return;
        }

        try
        {
            var ownCopies = Sections.Where(s => s.IsShared && s.OwnCopy).Select(s => s.Section.Key).ToHashSet();
            string folder;
            ExmodPackage package;
            IReadOnlyList<string> lines;
            if (target.IsNew)
            {
                if (string.IsNullOrWhiteSpace(NewModName) || string.IsNullOrWhiteSpace(NewModAuthor))
                {
                    StatusMessage = "Give the new mod a name and an author.";
                    return;
                }

                // Built in memory first: if the changes can't be applied, no empty mod is left in Library.
                package = ModTemplateContent.Create(ModTemplate.Blank, NewModName.Trim(), NewModAuthor.Trim());
                lines = GameItemEditor.Apply(game, package, item.Name, changes, ownCopies);
                folder = _repository.CreateBlankMod(NewModName.Trim(), NewModAuthor.Trim()).FolderName;
            }
            else
            {
                // Re-checked at save time: the list could be stale, and only the user's own mods may change.
                if (_repository.GetAll().FirstOrDefault(e => e.FolderName == target.FolderName) is not { IsOwnMod: true })
                {
                    StatusMessage = "That mod can't take changes: only mods you made in Starlink can.";
                    RefreshTargets();
                    return;
                }

                folder = target.FolderName!;
                package = ExmodFolder.ReadPackageOnly(_repository.GetFolderPath(folder), _repository.ListFolderFiles(folder));
                lines = GameItemEditor.Apply(game, package, item.Name, changes, ownCopies);
            }

            var path = _repository.GetFolderPath(folder);
            ExmodFolder.Write(path, new ExmodPackageContents(package, []));
            _repository.MarkLocallyEdited(folder);
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

            var name = _repository.GetAll().FirstOrDefault(e => e.FolderName == folder)?.Name ?? folder;
            RefreshTargets(selectFolder: folder);
            LoadItem();
            StatusMessage = $"Saved {lines.Count} change(s) to '{name}'. To use it in game: Turn on & go to Merge & Install, then Install.";
            _activityLog?.Log($"Saved {lines.Count} change(s) to '{name}' in the Editor.", ActivityEntryKind.Success);
            SavedModFolder = folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            StatusMessage = $"Couldn't save: {ex.Message}";
            _activityLog?.Log($"Editor: couldn't save: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    private void LoadItem()
    {
        _shownItem = SelectedItem;
        Sections.Clear();
        ItemTitle = null;
        KeptAsGames = null;
        if (Game is not { } game || SelectedItem is not { } item)
        {
            UpdatePending();
            OnPropertyChanged(nameof(HasItem));
            OnPropertyChanged(nameof(ShowPickPrompt));
            return;
        }

        var view = GameItemEditor.Describe(game, TargetPackage(), item.Name);
        if (view is null)
        {
            StatusMessage = $"'{item.Name}' isn't in the game data any more.";
            UpdatePending();
            return;
        }

        ItemTitle = view.DisplayName is { Length: > 0 } d ? $"{d}  ({item.Name})" : item.Name;
        KeptAsGames = view.KeptAsGames.Count == 0 ? null : "Kept as the game's (models, sounds, how it's picked up and used): " + string.Join("; ", view.KeptAsGames) + ".";
        _statChoices ??= GameItemEditor.StatChoices(game);
        foreach (var section in view.Sections)
        {
            Sections.Add(new GameItemSectionViewModel(section, _statChoices, UpdatePending, game));
        }

        UpdatePending();
        OnPropertyChanged(nameof(HasItem));
        OnPropertyChanged(nameof(ShowPickPrompt));
    }

    private ExmodPackage? TargetPackage()
    {
        if (SelectedTarget is not { IsNew: false, FolderName: { } folder })
        {
            return null;
        }

        try
        {
            var path = _repository.GetFolderPath(folder);
            return ExmodFolder.ReadPackageOnly(path, _repository.ListFolderFiles(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            StatusMessage = $"Couldn't read that mod: {ex.Message}";
            return null;
        }
    }

    private void UpdatePending()
    {
        var pending = Sections.SelectMany(s => s.Fields.Where(f => f.IsPending).Select(f => (Section: s, Field: f))).ToList();
        PendingCount = pending.Count;
        PendingSummary = pending.Count == 0
            ? "Nothing changed yet."
            : string.Join("\n", pending.Take(8).Select(p => $"{p.Section.Title} · {p.Field.Label}: {p.Field.ValueText}"
                + (p.Section.IsShared && !p.Section.Section.IsOwnCopy ? (p.Section.OwnCopy ? "  (its own copy)" : $"  (all {p.Section.Section.SharedBy:N0} items)") : "")))
              + (pending.Count > 8 ? $"\n…and {pending.Count - 8} more" : "");
    }

    /// <summary>
    /// Rebuilds the "save into" list (on every Library change). The shown item is reloaded only when
    /// the target really changed (or nothing is typed): a reload keeps only typed values that exist
    /// afterwards, so a stat added here and not saved yet was lost whenever anything in Library changed.
    /// </summary>
    private void RefreshTargets(string? selectFolder = null)
    {
        var before = SelectedTarget;
        var keep = selectFolder ?? before?.FolderName;
        var wasNew = selectFolder is null && before?.IsNew == true;
        _refreshingTargets = true;
        try
        {
            Targets.Clear();
            Targets.Add(new EditorSaveTarget(null, "A new mod"));
            foreach (var entry in _repository.GetAll().Where(e => e.IsOwnMod).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                Targets.Add(new EditorSaveTarget(entry.FolderName, entry.Name));
            }

            SelectedTarget = (wasNew ? null : Targets.FirstOrDefault(t => t.FolderName == keep)) ?? Targets[0];
        }
        finally
        {
            _refreshingTargets = false;
        }

        if (before is null || SelectedTarget?.FolderName != before.FolderName)
        {
            ReloadForTarget();
        }
        else if (PendingCount == 0 && Sections.Count > 0)
        {
            LoadItem(); // nothing typed to lose: show the target mod as it is now
        }
    }

    private void ResetGameData()
    {
        _game = null;
        _allItems = [];
        _statChoices = null;
        QuickStarts.Clear();
        EnsureLoaded();
        LoadItem();
    }

    private void RefreshFilter()
    {
        var picked = SelectedItem;
        _refreshingFilter = true;
        try
        {
            FilteredItems.Clear();
            var filter = FilterText.Trim();
            foreach (var item in _allItems.Where(i => filter.Length == 0 || i.Display.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(400))
            {
                FilteredItems.Add(item);
            }

            if (picked is not null && !FilteredItems.Contains(picked))
            {
                FilteredItems.Insert(0, picked);
            }

            SelectedItem = picked;
        }
        finally
        {
            _refreshingFilter = false;
        }
    }
}
