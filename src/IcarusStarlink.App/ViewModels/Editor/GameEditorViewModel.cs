using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
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
        _filterDebounce = new DebounceTimer(TimeSpan.FromMilliseconds(250), RefreshFilter);
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

    /// <summary>Row names (D_ItemsStatic) offered on the empty state, checked against the game's own D_ItemsStatic; one missing from the loaded data is just left out.</summary>
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
        var version = ++_loadVersion;
        _loading = Task.Run(() =>
        {
            var game = gameReady ?? OpenGameData(dataFolder);
            return (game, game is null ? (IReadOnlyList<GameItemInfo>)[] : GameItemCatalog.List(game));
        });
        NoDataMessage = LoadingMessage;
        try
        {
            var (game, items) = await _loading;
            if (version == _loadVersion)
            {
                FinishLoading(game, items);
            }
        }
        catch (Exception ex)
        {
            // Every error: this runs from the page's async void IsVisibleChanged, where an exception
            // would reach the app's crash handler. A load that Refresh game data overtook says nothing:
            // its message would cover the list read since.
            if (version == _loadVersion)
            {
                _loading = null;
                Serilog.Log.Warning(ex, "The Editor page couldn't read the game's items");
                NoDataMessage = $"Couldn't read the game's items: {ex.Message}. Run Refresh game data in Settings, then open the Editor again.";
            }
        }
    }

    /// <summary>Moves on when Refresh game data abandons a running first load, so that load's result (or error) is never shown.</summary>
    private int _loadVersion;

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
        _itemsByName.Clear();
        foreach (var entry in _allItems)
        {
            entry.PropertyChanged += OnEntryPropertyChanged;
            _itemsByName.TryAdd(entry.Name, entry); // the first of two rows with one name, as a search from the top finds
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

    /// <summary>The list's items by row name: "Your changes" looks each waiting item up on every keystroke.</summary>
    private readonly Dictionary<string, EditorItemEntry> _itemsByName = new(StringComparer.OrdinalIgnoreCase);

    private EditorItemEntry? FindItem(string name) => _itemsByName.GetValueOrDefault(name);

    /// <summary>
    /// Shows one game item (from the mod editor's "Open in Editor"). With the mod it came from, and
    /// that's one of the user's own mods, that mod becomes the save target, so the values shown are
    /// the mod's and saving goes back into it (not the game's values into whatever was picked before).
    /// Any other mod can't take changes: Save to stays, and the status line says why.
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
        if (targetFolder is null)
        {
            return;
        }

        if (Targets.FirstOrDefault(t => t.FolderName == targetFolder) is { } target)
        {
            SelectedTarget = target;
            return;
        }

        // A downloaded or classic mod: Save to stays as it was, and the page says so (it used to
        // show the game's values without a word, as if they were that mod's).
        var modName = _repository.GetAll().FirstOrDefault(e => e.FolderName == targetFolder)?.Name ?? targetFolder;
        StatusMessage = SelectedTarget is { IsNew: false } current
            ? $"'{modName}' isn't one of your own mods, so changes can't go into it. Shown: the game's values with the changes in '{current.Display}'; Save goes into '{current.Display}'."
            : $"'{modName}' isn't one of your own mods, so changes can't go into it. Shown: the game's values; Save makes a new mod.";
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
        RebuildDraftsIfTargetMoved(stampBefore, target);
        ShowItem(target);
    }

    /// <summary>
    /// The mod under Save to changed on disk since the waiting items were built from it (the mod
    /// editor or a fix saved into it): they're built again from its new version, keeping what was
    /// typed. Every read of the target takes its new stamp, so skipping this would let Save write
    /// the waiting items' lists from the old file.
    /// </summary>
    private void RebuildDraftsIfTargetMoved(DateTime? stampBefore, ExmodPackage? target)
    {
        if (stampBefore is not null && _shownStamp != stampBefore && RebuildDrafts(target) is var dropped and > 0)
        {
            StatusMessage = $"That mod was changed somewhere else: {dropped} typed change(s) aren't in its new version, so they were dropped.";
        }
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

        if (SharedRowClash(edited) is { } clash)
        {
            StatusMessage = clash;
            return;
        }

        // Saved anyway (a free craft by hand can be on purpose), but said: a machine never starts it.
        var noIngredients = edited.Where(e => e.Cards.Any(c => c.TakesOutEveryIngredient && !c.MadeOnlyByHand))
            .Select(e => FindItem(e.Name)?.Title ?? e.Name).ToList();

        if (SelectedTarget is not { } target)
        {
            StatusMessage = "Choose where to save: a new mod, or one of your mods.";
            return;
        }

        // The new mod this Save made, until its first write is done: a failed write deletes it, so
        // Save again doesn't make <Name>_2 next to an empty one.
        string? created = null;
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

                if (NewModNameProblem(NewModName) is { } problem)
                {
                    StatusMessage = problem + " Change the name, then Save.";
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
            // One lookup for every item of this Save (built for this package: never _bulkLookup).
            var lines = new List<string>();
            var lookup = new GameItemLookup(game, package);
            var exceptBefore = package.ScaleRules.Sum(r => r.ExceptRows.Count);
            foreach (var (itemName, changes, ownCopies) in work)
            {
                lines.AddRange(GameItemEditor.Apply(game, package, itemName, changes, ownCopies, lookup));
            }

            // A value typed on a row a Scale rule sets puts the row on the rule's skip list.
            var nowSkipped = package.ScaleRules.Sum(r => r.ExceptRows.Count) - exceptBefore;

            if (target.IsNew)
            {
                folder = _repository.CreateBlankMod(NewModName.Trim(), NewModAuthor.Trim()).FolderName;
                created = folder;
            }

            // The mod editor may have saved into this mod since its values were shown here: the
            // cards (and what was typed against them) would be out of date.
            var moved = !target.IsNew && _shownStamp is { } shownStamp && _writer.StampOf(folder, package.FileName) != shownStamp;
            var backedUp = false;
            if (!moved && !target.IsNew)
            {
                // One Save can change hundreds of items: Restore latest backup undoes it, like any automatic change.
                _repository.BackupMod(folder);
                backedUp = true;
            }

            var saved = !moved && _writer.TrySave(folder, package, target.IsNew ? null : _shownStamp, out _);
            created = null; // written, or refused with the mod as it was: either way it stays
            if (!saved)
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
            var backup = backedUp ? " It was backed up first (right-click it in Library › Restore latest backup undoes this Save)." : "";
            var warning = noIngredients.Count switch
            {
                0 => "",
                1 => $" Note: {noIngredients[0]}'s recipe takes no ingredients now. A machine (a furnace, a processor) never starts a recipe like that by itself: give it one ingredient if a machine makes it.",
                _ => $" Note: {noIngredients.Count} recipes take no ingredients now ({string.Join(", ", noIngredients)}). A machine (a furnace, a processor) never starts a recipe like that by itself: give each one ingredient if a machine makes it.",
            };
            var skipped = nowSkipped > 0
                ? $" {nowSkipped} value(s) a Scale rule sets now stay as you typed them: the rule skips those rows (see Scale rules in the mod editor)."
                : "";
            StatusMessage = $"Saved {lines.Count} change(s) to '{name}'{items}.{backup}{skipped} To use it in game: Turn on & go to Merge & Install, then Install.{warning}";
            _activityLog?.Log($"Saved {lines.Count} change(s) to '{name}'{items} in the Editor.", ActivityEntryKind.Success);
            SavedModFolder = folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (created is not null)
            {
                try
                {
                    _repository.Delete(created);
                }
                catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
                {
                    Serilog.Log.Warning(deleteEx, "The Editor page couldn't remove the empty mod {Folder} a failed save left", created);
                }
            }

            StatusMessage = $"Couldn't save: {ex.Message}";
            _activityLog?.Log($"Editor: couldn't save: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Why Windows can't take this new mod's name as its file name (spaces become _), in plain
    /// words; null when it can. The write would refuse it later with a message about EXMOD fields.
    /// </summary>
    internal static string? NewModNameProblem(string name) =>
        IcarusStarlink.PakIO.Safety.AssetPathGuard.IsSimpleFileName(name.Trim().Replace(' ', '_'))
            ? null
            : "A mod name can't contain \\ / : * ? \" < > |, or be a name Windows keeps for itself (like CON or NUL).";

    /// <summary>
    /// Two items changing one shared row "for all" to different values: whichever is written last
    /// would win without a word. Returns the first such clash in plain words, or null.
    /// </summary>
    private string? SharedRowClash(IEnumerable<(string Name, IReadOnlyList<GameItemSectionViewModel> Cards)> edited)
    {
        var seen = new Dictionary<(string Table, string Row, string Path), (string Item, JsonNode? Value)>();
        foreach (var (itemName, cards) in edited)
        {
            foreach (var card in cards.Where(c => c.IsShared && !c.OwnCopy))
            {
                foreach (var change in card.Changes())
                {
                    var key = (card.Section.Table.ToLowerInvariant(), card.Section.RowName.ToLowerInvariant(), change.Path.Key);
                    if (!seen.TryGetValue(key, out var first))
                    {
                        seen[key] = (itemName, change.Value);
                        continue;
                    }

                    if (string.Equals(first.Item, itemName, StringComparison.OrdinalIgnoreCase) || JsonNode.DeepEquals(first.Value, change.Value))
                    {
                        continue;
                    }

                    var label = card.Fields.FirstOrDefault(f => f.Field.Path.Key == change.Path.Key)?.Label
                        ?? card.Fields.FirstOrDefault(f => f.Field.Path.Top == change.Path.Top)?.Label ?? change.Path.Key;
                    var values = Shown(first.Value) is { } a && Shown(change.Value) is { } b ? $" ({a} and {b})" : "";
                    return $"Not saved: {FindItem(first.Item)?.Title ?? first.Item} and {FindItem(itemName)?.Title ?? itemName} both change {label} on the row they share, {card.Section.RowName}, "
                        + $"to different values{values}. It can only have one: type the same value for both, or give one its own copy.";
                }
            }
        }

        return null;

        static string? Shown(JsonNode? value) => value switch
        {
            null => "the game's value",
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue number => number.ToJsonString(),
            _ => null, // a whole list: too long for one line
        };
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

    /// <summary>Shows the selected item fresh from the game and the target mod (what was typed for it is thrown away; the waiting items are rebuilt when the mod changed meanwhile).</summary>
    private void LoadItem()
    {
        var stampBefore = _shownStamp;
        var target = TargetPackage();
        RebuildDraftsIfTargetMoved(stampBefore, target);
        ShowItem(target);
    }

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
            GameItemView? view = null;
            var unreadable = false;
            try
            {
                view = GameItemEditor.Describe(game, target, item.Name);
            }
            catch (Exception ex)
            {
                // A mod can write a value of the wrong kind (a link as plain text, a number for a row
                // name): say so instead of letting it close the app.
                Serilog.Log.Warning(ex, "The Editor page couldn't read item {Item}", item.Name);
                unreadable = true;
                StatusMessage = target is null
                    ? $"Couldn't show '{item.Name}': the game data has a value this page can't read. Refresh game data in Settings may fix it."
                    : $"Couldn't show '{item.Name}': '{SelectedTarget?.Display}' has a value of the wrong kind for it (like a link written as plain text). Fix that value in the mod editor.";
                dropped = carryFrom?.Sum(s => s.Fields.Count(f => f.IsTyped)) ?? 0;
            }

            // Unreadable: nothing is shown, and what was typed for it before still waits.
            if (view is null && !unreadable)
            {
                StatusMessage = $"'{item.Name}' isn't in the game data any more.";
                _drafts.Remove(item.Name);
                dropped = carryFrom?.Sum(s => s.Fields.Count(f => f.IsTyped)) ?? 0;
            }
            else if (view is not null)
            {
                ItemTitle = view.DisplayName is { Length: > 0 } d ? $"{d}  ({item.Name})" : item.Name;
                ItemDescription = item.Info.Description is { Length: > 0 } about ? $"{item.Category} · {about}" : item.Category;
                KeptAsGames = view.KeptAsGames.Count == 0 ? null : "Kept as the game's (models, sounds, how it's picked up and used): " + string.Join("; ", view.KeptAsGames) + ".";
                // Cards a bulk preview built for it are older than this read of the target: never used again.
                _bulkCards.Remove(item.Name);
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

    /// <summary>
    /// Refresh game data finished: the list and the cards are read again from the new tables. This
    /// runs inside the broadcast, after the refresh already succeeded: a table that can't be read is
    /// said here and never thrown, so Settings doesn't report "Refresh failed" and the listeners
    /// after this one still hear about the new data.
    /// </summary>
    private void ResetGameData()
    {
        try
        {
            var shown = _shownItem?.Name;
            if (_loading is { } running)
            {
                // The first load was still reading the old tables: let it end, and don't use what it
                // read (its own continuation sees the version moved on and leaves the list alone).
                _loadVersion++;
                try
                {
                    running.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // The new data is read again below.
                }

                _loading = null;
            }

            _game = null;
            _allItems = [];
            _itemsByName.Clear();
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
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "The Editor page couldn't read the refreshed game data");
            NoDataMessage = $"Couldn't read the game's items: {ex.Message}. Run Refresh game data in Settings, then open the Editor again.";
        }
    }
}
