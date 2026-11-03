using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A mod the Editor can save into: "New mod", or one of the user's own mods.</summary>
public sealed record EditorSaveTarget(string? FolderName, string Display)
{
    public bool IsNew => FolderName is null;

    /// <summary>The closed dropdown shows this (the record's own text was "EditorSaveTarget { FolderName = … }").</summary>
    public override string ToString() => Display;
}

/// <summary>A quick-start button on the Editor's empty state: a common item to change, and what people usually change on it.</summary>
public sealed record EditorQuickStart(EditorItemEntry Item, string Hint)
{
    public string Label => Item.Title;

    /// <summary>What a screen reader says for the quick-start button (the record's own text otherwise).</summary>
    public override string ToString() => Label;
}

/// <summary>One line of the Editor's "Your changes"; the × undoes it through the field.</summary>
public sealed record EditorPendingChange(string Text, GameItemFieldViewModel Field);

/// <summary>
/// The Editor page: change the game's OWN items (the default Envirosuit's weight, Bread's buff) —
/// the counterpart of New mod, which only ever adds new things. Pick an item, change values, and
/// save into a new mod or one of your own mods (never a downloaded one: an update would replace it,
/// and it isn't yours). Built on GameItemEditor, which writes only what changed.
/// </summary>
public sealed partial class GameEditorViewModel : ObservableObject
{
    private readonly ILibraryRepository _repository;
    private readonly OwnModWriter _writer;
    private readonly IMessenger _messenger;

    /// <summary>The target mod's .EXMOD stamp when its values were read for the cards (TargetPackage); Save refuses when it moved on.</summary>
    private DateTime? _shownStamp;
    private readonly IActivityLog? _activityLog;
    private readonly string _dataFolder;
    private GameTableIndex? _game;
    private IReadOnlyList<StatChoice>? _statChoices;

    /// <param name="activityLog">Saves and failed saves also go to the app-wide activity feed (null in tests).</param>
    /// <param name="iconDecoder">The game's item icons for the list (null in tests: no icons).</param>
    /// <param name="messenger">The app's messages (the shared messenger unless a test gives its own).</param>
    public GameEditorViewModel(ILibraryRepository repository, string dataFolder, IActivityLog? activityLog = null, IBaseGameIconDecoder? iconDecoder = null, IMessenger? messenger = null)
    {
        _repository = repository;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _writer = new OwnModWriter(repository, _messenger);
        _activityLog = activityLog;
        _dataFolder = dataFolder;
        _icons = iconDecoder is null ? null : new GameIconLoader(iconDecoder);
        NewModAuthor = Environment.UserName;
        // Game data changes under the page when Refresh game data runs.
        _messenger.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((GameEditorViewModel)recipient).ResetGameData());
        _messenger.Register<LibraryChangedMessage>(this, (recipient, _) => ((GameEditorViewModel)recipient).RefreshTargets());
        // Typed values live only on this page until Save: closing the app would drop them unasked.
        _messenger.Register<UnsavedEditsRequestMessage>(this, (recipient, message) =>
        {
            if (((GameEditorViewModel)recipient).PendingCount > 0)
            {
                message.Reply("typed changes on the Editor page");
            }
        });
        _messenger.Register<OpenGameItemMessage>(this, (recipient, message) => ((GameEditorViewModel)recipient).Open(message.ItemName, message.TargetFolder));
        RefreshTargets();
    }

    public string Title => "Editor";

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
        ("Oxygen_Tank", "how much oxygen it holds"),
        ("Kit_Generator", "fuel use, power"),
        ("Kit_Stone_Furnace", "smelting, slots"),
        ("IceBox", "storage slots"),
        ("Wood_Bow", "accuracy, draw speed"),
        ("Bandage_Basic", "healing"),
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
    [NotifyPropertyChangedFor(nameof(HasItem), nameof(ShowPickPrompt), nameof(ShowItemCards))]
    private EditorItemEntry? _selectedItem;

    public bool HasItem => SelectedItem is not null && Sections.Count > 0;

    /// <summary>The item's cards (not while the bulk edit takes the middle).</summary>
    public bool ShowItemCards => HasItem && !BulkMode;

    public bool ShowPickPrompt => !HasItem && NoDataMessage is null && !BulkMode;

    [ObservableProperty]
    private string? _itemTitle;

    /// <summary>Under the item's title: its category and what the game says about it.</summary>
    [ObservableProperty]
    private string? _itemDescription;

    [ObservableProperty]
    private string? _keptAsGames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPickPrompt))]
    private string? _noDataMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewTarget), nameof(IsOwnModTarget))]
    private EditorSaveTarget? _selectedTarget;

    /// <summary>One of the user's mods is chosen (not "A new mod"): the list can mark and filter what it changes.</summary>
    public bool IsOwnModTarget => !IsNewTarget;

    public bool IsNewTarget => SelectedTarget?.IsNew != false;

    [ObservableProperty]
    private string _newModName = "";

    [ObservableProperty]
    private string _newModAuthor;

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
            _messenger.Send(new TurnOnModMessage(folder));
        }
    }

    /// <summary>Lazily: the page may never be opened in a session. Null while the list loads in the background (that thread has the index to itself).</summary>
    private GameTableIndex? Game
    {
        get
        {
            if (_loading is not null)
            {
                return null;
            }

            _game ??= OpenGameData(_dataFolder);
            return _game;
        }
    }

    private static GameTableIndex? OpenGameData(string dataFolder) =>
        Directory.Exists(dataFolder) && new GameTableIndex(dataFolder) is var game && game.CurrentFileFor("D_ItemsStatic") is not null ? game : null;

    /// <summary>The first load of the item list (3,090 items read from the game's tables, ~0.8 s), off the UI thread.</summary>
    private Task<(GameTableIndex? Game, IReadOnlyList<GameItemInfo> Items)>? _loading;

    public const string LoadingMessage = "Reading the game's items…";

    /// <summary>Called when the page is shown: starts loading the item list in the background the first time (the page says so meanwhile).</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_allItems.Count > 0 || _loading is not null)
        {
            return;
        }

        var dataFolder = _dataFolder;
        var gameReady = _game;
        _loading = Task.Run(() =>
        {
            var game = gameReady ?? OpenGameData(dataFolder);
            return (game, game is null ? (IReadOnlyList<GameItemInfo>)[] : GameItemCatalog.List(game));
        });
        NoDataMessage = LoadingMessage;
        try
        {
            var (game, items) = await _loading;
            FinishLoading(game, items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            _loading = null;
            NoDataMessage = $"Couldn't read the game's items: {ex.Message}. Run Refresh game data in Settings, then open the Editor again.";
        }
    }

    /// <summary>The item list now (Open from the mod editor, tests): waits for a background load already running instead of reading the tables twice.</summary>
    public void EnsureLoaded()
    {
        if (_allItems.Count > 0)
        {
            return;
        }

        if (_loading is { } running)
        {
            var (loadedGame, loadedItems) = running.GetAwaiter().GetResult();
            FinishLoading(loadedGame, loadedItems);
            return;
        }

        if (Game is not { } game)
        {
            NoDataMessage = "No game data yet. Run Refresh game data in Settings first; the Editor reads the game's items from it.";
            return;
        }

        FinishLoading(game, GameItemCatalog.List(game));
    }

    private void FinishLoading(GameTableIndex? game, IReadOnlyList<GameItemInfo> items)
    {
        _loading = null;
        if (_allItems.Count > 0)
        {
            return; // the other way in got there first
        }

        _game = game;
        if (game is null)
        {
            NoDataMessage = "No game data yet. Run Refresh game data in Settings first; the Editor reads the game's items from it.";
            return;
        }

        NoDataMessage = null;
        _allItems = [.. items.Select(info => new EditorItemEntry(info, _icons))];
        foreach (var entry in _allItems)
        {
            entry.PropertyChanged += OnEntryPropertyChanged;
        }

        TicksChanged();
        QuickStarts.Clear();
        foreach (var (name, hint) in QuickStartItems)
        {
            if (FindItem(name) is { } item)
            {
                QuickStarts.Add(new EditorQuickStart(item, hint));
            }
        }

        BuildFilterChoices(game);
        RefreshTargetMarks();
        RefreshFilter();
    }

    private EditorItemEntry? FindItem(string name) => _allItems.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Shows one game item (from the mod editor's "Open in Editor"). With the mod it came from, and
    /// that's one of the user's own mods, that mod becomes the save target, so the values shown are
    /// the mod's and saving goes back into it (not the game's values into whatever was picked before).
    /// </summary>
    public void Open(string itemName, string? targetFolder = null)
    {
        EnsureLoaded();
        if (FindItem(itemName) is not { } item)
        {
            return;
        }

        SelectedItem = item; // what was typed for the item on show waits for Save
        RefreshFilter(); // the filters may hide it; make sure this one is in the list
        if (targetFolder is not null && Targets.FirstOrDefault(t => t.FolderName == targetFolder) is { } target)
        {
            SelectedTarget = target;
        }
    }

    /// <summary>Asks before typed changes are thrown away (Discard, an item's × in Your changes). Tests swap in an answer.</summary>
    internal Func<string, bool> ConfirmDiscard { get; set; } = message =>
        Views.ThemedConfirmDialog.Show(System.Windows.Application.Current?.MainWindow, message, "Unsaved changes", Views.ThemedConfirmSeverity.Warning);

    /// <summary>The item whose cards are showing.</summary>
    private EditorItemEntry? _shownItem;

    // Switching items keeps what was typed for the item left (its cards wait in _drafts) and brings
    // back what was typed for the item picked: one Save then writes every item's changes.
    partial void OnSelectedItemChanged(EditorItemEntry? value)
    {
        if (_refreshingFilter || value == _shownItem)
        {
            return;
        }

        BulkMode = false; // picking an item shows it
        StashShownItem();
        var stampBefore = _shownStamp;
        var target = TargetPackage();

        // The mod editor saved into the target since the waiting items were built from it.
        if (stampBefore is not null && _shownStamp != stampBefore && RebuildDrafts(target) is var dropped and > 0)
        {
            StatusMessage = $"That mod was changed somewhere else: {dropped} typed change(s) aren't in its new version, so they were dropped.";
        }

        ShowItem(target);
    }

    /// <summary>Set while the target list is rebuilt: the list empties under the selection, which isn't a new pick.</summary>
    private bool _refreshingTargets;

    partial void OnSelectedTargetChanged(EditorSaveTarget? value)
    {
        if (!_refreshingTargets)
        {
            ReloadForTarget();
            RefreshTargetMarks();
        }
    }

    // The values shown are the game's with the target mod's changes on top; typed changes come
    // along to the newly chosen mod instead of being lost or applied to values not on screen.
    private void ReloadForTarget() => ReloadKeepingTyped("don't apply to that mod's version of the item");

    /// <summary>Reloads every edited item's cards and the shown item's, putting back every typed value that still has a place (and each card's own-copy choice).</summary>
    /// <param name="whyDropped">Ends "N typed change(s) …, so they were dropped."</param>
    private void ReloadKeepingTyped(string whyDropped)
    {
        var target = TargetPackage();
        var dropped = RebuildDrafts(target) + ShowItem(target, carryFrom: [.. Sections]);
        if (dropped > 0)
        {
            StatusMessage = $"{dropped} typed change(s) {whyDropped}, so they were dropped.";
        }
    }

    /// <summary>Discard: throws away what was typed for every item.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        if (PendingCount > 0 && !ConfirmDiscard(EditedItemCount > 1
                ? $"Throw away the {PendingCount} change(s) you typed for {EditedItemCount} items?"
                : $"Throw away the {PendingCount} change(s) you typed?"))
        {
            return;
        }

        _drafts.Clear();
        ClearBulkCache();
        LoadItem();
    }

    [RelayCommand]
    private void Save()
    {
        var edited = EditedItems();
        if (Game is not { } game || (edited.Count == 0 && SelectedItem is null))
        {
            StatusMessage = "Pick a game item first.";
            return;
        }

        // A value that isn't a number blocks saving: the shown item's first, then the waiting ones.
        var invalid = Sections.SelectMany(s => s.Fields).Select(f => (Item: (string?)null, Field: f))
            .Concat(_drafts.SelectMany(d => d.Value.SelectMany(s => s.Fields).Select(f => (Item: (string?)d.Key, Field: f))))
            .FirstOrDefault(x => x.Field.Error is not null);
        if (invalid.Field is { } bad)
        {
            StatusMessage = invalid.Item is null ? $"{bad.Label}: {bad.Error}" : $"{FindItem(invalid.Item)?.Title ?? invalid.Item} · {bad.Label}: {bad.Error}";
            return;
        }

        var work = edited
            .Select(e => (e.Name,
                Changes: e.Cards.SelectMany(s => s.Changes()).ToList(),
                OwnCopies: e.Cards.Where(s => s.IsShared && s.OwnCopy).Select(s => s.Section.Key).ToHashSet()))
            .Where(w => w.Changes.Count > 0)
            .ToList();
        if (work.Count == 0)
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
            ExmodPackage package;
            if (target.IsNew)
            {
                if (string.IsNullOrWhiteSpace(NewModName) || string.IsNullOrWhiteSpace(NewModAuthor))
                {
                    StatusMessage = "Give the new mod a name and an author.";
                    return;
                }

                package = ModTemplateContent.Create(ModTemplate.Blank, NewModName.Trim(), NewModAuthor.Trim());
                folder = "";
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
            }

            // Every item into the one package, in memory first: if one can't be applied, nothing is
            // written (and no empty new mod is left in Library).
            var lines = new List<string>();
            foreach (var (itemName, changes, ownCopies) in work)
            {
                lines.AddRange(GameItemEditor.Apply(game, package, itemName, changes, ownCopies));
            }

            if (target.IsNew)
            {
                folder = _repository.CreateBlankMod(NewModName.Trim(), NewModAuthor.Trim()).FolderName;
            }

            // The mod editor may have saved into this mod since its values were shown here: the
            // cards (and what was typed against them) would be out of date.
            if (!_writer.TrySave(folder, package, target.IsNew ? null : _shownStamp, out _))
            {
                StatusMessage = null;
                ReloadKeepingTyped("aren't in that mod's new version");
                StatusMessage = "Not saved: that mod was changed somewhere else (an open mod editor?) since it was shown here. Its current values are shown now, with your typed changes on top; Save again to save them."
                    + (StatusMessage is { } dropped ? $" {dropped}" : "");
                _activityLog?.Log("Editor: not saved, the mod was changed somewhere else. Its current values are shown with your changes on top: Save again.", ActivityEntryKind.Warning);
                return;
            }

            var name = _repository.GetAll().FirstOrDefault(e => e.FolderName == folder)?.Name ?? folder;
            var items = work.Count == 1 ? "" : $" ({work.Count} items)";
            _drafts.Clear();
            ClearBulkCache(); // built from the mod before this save
            RefreshTargets(selectFolder: folder);
            LoadItem();
            StatusMessage = $"Saved {lines.Count} change(s) to '{name}'{items}. To use it in game: Turn on & go to Merge & Install, then Install.";
            _activityLog?.Log($"Saved {lines.Count} change(s) to '{name}'{items} in the Editor.", ActivityEntryKind.Success);
            SavedModFolder = folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            StatusMessage = $"Couldn't save: {ex.Message}";
            _activityLog?.Log($"Editor: couldn't save: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>"Only changed values": each card shows just the values that differ from the game's (taken when switched, not while typing).</summary>
    [ObservableProperty]
    private bool _showChangedValuesOnly;

    partial void OnShowChangedValuesOnlyChanged(bool value)
    {
        foreach (var section in Sections)
        {
            section.ShowOnlyChanged(value);
        }
    }

    /// <summary>Shows the selected item fresh from the game and the target mod (what was typed for it is thrown away).</summary>
    private void LoadItem() => ShowItem(TargetPackage());

    /// <summary>
    /// Shows the selected item: its waiting cards when changes were typed for it before, else fresh
    /// cards from the game with <paramref name="target"/>'s changes on top, taking over what was typed
    /// on <paramref name="carryFrom"/> (the same item's previous cards). Returns how many typed values
    /// had no place on the new cards.
    /// </summary>
    private int ShowItem(ExmodPackage? target, IReadOnlyList<GameItemSectionViewModel>? carryFrom = null)
    {
        _shownItem = SelectedItem;
        Sections.Clear();
        ItemTitle = null;
        ItemDescription = null;
        KeptAsGames = null;
        var dropped = 0;
        if (Game is { } game && SelectedItem is { } item)
        {
            var view = GameItemEditor.Describe(game, target, item.Name);
            if (view is null)
            {
                StatusMessage = $"'{item.Name}' isn't in the game data any more.";
                _drafts.Remove(item.Name);
                dropped = carryFrom?.Sum(s => s.Fields.Count(f => f.IsTyped)) ?? 0;
            }
            else
            {
                ItemTitle = view.DisplayName is { Length: > 0 } d ? $"{d}  ({item.Name})" : item.Name;
                ItemDescription = item.Info.Description is { Length: > 0 } about ? $"{item.Category} · {about}" : item.Category;
                KeptAsGames = view.KeptAsGames.Count == 0 ? null : "Kept as the game's (models, sounds, how it's picked up and used): " + string.Join("; ", view.KeptAsGames) + ".";
                if (!_drafts.Remove(item.Name, out var cards))
                {
                    cards = BuildCards(game, view);
                    dropped = carryFrom is null ? 0 : CarryTyped(carryFrom, cards);
                }

                foreach (var card in cards)
                {
                    card.ShowOnlyChanged(ShowChangedValuesOnly);
                    Sections.Add(card);
                }
            }
        }

        UpdatePending();
        OnPropertyChanged(nameof(HasItem));
        OnPropertyChanged(nameof(ShowPickPrompt));
        OnPropertyChanged(nameof(ShowItemCards));
        if (BulkMode)
        {
            RefreshBulkFields(); // the shown item's cards are new
        }

        return dropped;
    }

    /// <summary>The mod under Save to as it is on disk now (null for a new mod); its stamp is what Save checks against.</summary>
    private ExmodPackage? TargetPackage()
    {
        var package = ReadTargetPackage(out var stamp);
        _shownStamp = stamp;
        return package;
    }

    private ExmodPackage? ReadTargetPackage(out DateTime? stamp)
    {
        stamp = null;
        if (SelectedTarget is not { IsNew: false, FolderName: { } folder })
        {
            return null;
        }

        try
        {
            var path = _repository.GetFolderPath(folder);
            var package = ExmodFolder.ReadPackageOnly(path, _repository.ListFolderFiles(folder));
            stamp = _writer.StampOf(folder, package.FileName);
            return package;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            StatusMessage = $"Couldn't read that mod: {ex.Message}";
            return null;
        }
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
        else if (Sections.Count > 0 && !Sections.Any(s => s.HasTyped))
        {
            LoadItem(); // nothing typed on the shown item to lose: show the target mod as it is now
        }

        RefreshTargetMarks(); // a save or a change in Library can change what the mod touches
    }

    private void ResetGameData()
    {
        var shown = _shownItem?.Name;
        if (_loading is { } running)
        {
            // The first load was still reading the old tables: let it end, and don't use what it read
            // (it finds the list rebuilt below and leaves it).
            try
            {
                running.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                // The new data is read again below.
            }

            _loading = null;
        }

        _game = null;
        _allItems = [];
        _itemChoices = null;
        _statChoices = null;
        _linkChoices.Clear();
        QuickStarts.Clear();
        EnsureLoaded();

        // The list was rebuilt: the item on show is a new entry now (the old one would sit in the list twice).
        if (shown is not null && FindItem(shown) is { } again)
        {
            _refreshingFilter = true;
            try
            {
                SelectedItem = again;
            }
            finally
            {
                _refreshingFilter = false;
            }

            RefreshFilter();
        }

        // Refresh game data used to throw away whatever was typed here, without a word.
        ReloadKeepingTyped("aren't in the refreshed game data");
    }
}
