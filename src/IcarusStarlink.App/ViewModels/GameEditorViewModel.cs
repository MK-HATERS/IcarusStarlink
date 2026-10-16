using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One value on the Editor page: the game's, and the one being typed.</summary>
public sealed partial class GameItemFieldViewModel : ObservableObject
{
    private readonly string _startText;
    private readonly string _gameText;

    public GameItemFieldViewModel(EditableField field)
    {
        Field = field;
        _gameText = Display(field.GameValue);
        _startText = Display(field.CurrentValue ?? field.GameValue);
        _valueText = _startText;
    }

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

    public string? Error => Field.Kind == EditableKind.Number && !double.TryParse(ValueText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
        ? "Needs a number, like 50 or 0.5."
        : null;

    [RelayCommand]
    private void UseGameValue() => ValueText = _gameText;

    /// <summary>The value to write: null means "the game's again".</summary>
    public JsonNode? NewValue()
    {
        if (ValueText.Trim() == _gameText && Field.GameValue is not null)
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

    public GameItemSectionViewModel(EditableSection section, IReadOnlyList<StatChoice> stats, Action changed)
    {
        Section = section;
        _stats = stats;
        _changed = changed;
        foreach (var field in section.Fields)
        {
            Add(new GameItemFieldViewModel(field));
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

    private readonly IReadOnlyList<StatChoice> _stats;

    /// <summary>What the user typed to find a stat: "cold", "movement", "health"…</summary>
    [ObservableProperty]
    private string _statSearch = "";

    /// <summary>Stats matching the search (by plain name or game name), at most 40.</summary>
    public ObservableCollection<StatChoice> MatchingStats { get; } = [];

    public bool HasMatchingStats => MatchingStats.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStatText))]
    private StatChoice? _selectedStat;

    public string SelectedStatText => SelectedStat is { } s ? $"{s.Label}  ·  {s.Kind}" : "Nothing picked yet: search, then click a stat.";

    [ObservableProperty]
    private string? _statError;

    partial void OnStatSearchChanged(string value)
    {
        MatchingStats.Clear();
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0)
        {
            foreach (var stat in _stats.Where(s => words.All(w => s.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(40))
            {
                MatchingStats.Add(stat);
            }
        }

        OnPropertyChanged(nameof(HasMatchingStats));
    }

    [RelayCommand]
    private void AddStat()
    {
        if (SelectedStat is not { } stat)
        {
            StatError = "Search for a stat (e.g. cold, movement, health) and click one first.";
            return;
        }

        var name = stat.Name;
        var key = GameItemEditor.StatKeyFor(name);
        var group = Section.StatGroups[0];
        if (Fields.Any(f => f.Field.Path.Top == group && f.Field.Path.Steps.Count == 1 && (string)f.Field.Path.Steps[0] == key))
        {
            StatError = "That stat is already here.";
            return;
        }

        Add(new GameItemFieldViewModel(new EditableField(new EditPath(group, [key]), stat.Label, $"Added by you: a {stat.Kind}. Higher = more, lower = less; 0 = no bonus.", EditableKind.Number, null, null, false)) { ValueText = "0" });
        StatError = null;
        SelectedStat = null;
        StatSearch = "";
        _changed();
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
    private readonly string _dataFolder;
    private GameTableIndex? _game;
    private IReadOnlyList<ItemChoice> _allItems = [];
    private IReadOnlyList<StatChoice>? _statNames;
    private bool _refreshingFilter;

    public GameEditorViewModel(ILibraryRepository repository, string dataFolder)
    {
        _repository = repository;
        _dataFolder = dataFolder;
        NewModAuthor = Environment.UserName;
        // Game data changes under the page when Settings' Update data folder runs.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((GameEditorViewModel)recipient).ResetGameData());
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((GameEditorViewModel)recipient).RefreshTargets());
        RefreshTargets();
    }

    public string Title => "Editor";

    public ObservableCollection<ItemChoice> FilteredItems { get; } = [];

    public ObservableCollection<GameItemSectionViewModel> Sections { get; } = [];

    public ObservableCollection<EditorSaveTarget> Targets { get; } = [];

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
            NoDataMessage = "No game data yet. Run Update data folder in Settings first; the Editor reads the game's items from it.";
            return;
        }

        NoDataMessage = null;
        _allItems = ItemCloner.ListItems(game);
        RefreshFilter();
    }

    partial void OnFilterTextChanged(string value) => RefreshFilter();

    partial void OnSelectedItemChanged(ItemChoice? value)
    {
        if (!_refreshingFilter)
        {
            LoadItem();
        }
    }

    partial void OnSelectedTargetChanged(EditorSaveTarget? value)
    {
        // The values shown are the game's with the target mod's changes on top.
        if (PendingCount == 0)
        {
            LoadItem();
        }
    }

    [RelayCommand]
    private void DiscardChanges() => LoadItem();

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
            string folder;
            if (target.IsNew)
            {
                if (string.IsNullOrWhiteSpace(NewModName) || string.IsNullOrWhiteSpace(NewModAuthor))
                {
                    StatusMessage = "Give the new mod a name and an author.";
                    return;
                }

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
            }

            var path = _repository.GetFolderPath(folder);
            var package = ExmodFolder.ReadPackageOnly(path, _repository.ListFolderFiles(folder));
            var ownCopies = Sections.Where(s => s.IsShared && s.OwnCopy).Select(s => s.Section.Key).ToHashSet();
            var lines = GameItemEditor.Apply(game, package, item.Name, changes, ownCopies);
            ExmodFolder.Write(path, new ExmodPackageContents(package, []));
            _repository.MarkLocallyEdited(folder);
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

            var name = _repository.GetAll().FirstOrDefault(e => e.FolderName == folder)?.Name ?? folder;
            RefreshTargets(selectFolder: folder);
            LoadItem();
            StatusMessage = $"Saved {lines.Count} change(s) to '{name}'. Tick it in Library and Merge & Install to use it in game.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            StatusMessage = $"Couldn't save: {ex.Message}";
        }
    }

    private void LoadItem()
    {
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
        _statNames ??= GameItemEditor.StatChoices(game);
        foreach (var section in view.Sections)
        {
            Sections.Add(new GameItemSectionViewModel(section, _statNames, UpdatePending));
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

    private void RefreshTargets(string? selectFolder = null)
    {
        var keep = selectFolder ?? SelectedTarget?.FolderName;
        var wasNew = selectFolder is null && SelectedTarget?.IsNew == true;
        Targets.Clear();
        Targets.Add(new EditorSaveTarget(null, "A new mod"));
        foreach (var entry in _repository.GetAll().Where(e => e.IsOwnMod).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            Targets.Add(new EditorSaveTarget(entry.FolderName, entry.Name));
        }

        SelectedTarget = (wasNew ? null : Targets.FirstOrDefault(t => t.FolderName == keep)) ?? Targets[0];
    }

    private void ResetGameData()
    {
        _game = null;
        _allItems = [];
        _statNames = null;
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
