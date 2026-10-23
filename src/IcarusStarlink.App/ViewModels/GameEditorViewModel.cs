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
public sealed record EditorQuickStart(ItemChoice Item, string Hint)
{
    public string Label => Item.Title;
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

    /// <summary>The target mod's .EXMOD stamp when its values were read for the cards (TargetPackage); Save refuses when it moved on.</summary>
    private DateTime? _shownStamp;
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
        _writer = new OwnModWriter(repository);
        _activityLog = activityLog;
        _dataFolder = dataFolder;
        NewModAuthor = Environment.UserName;
        // Game data changes under the page when Refresh game data runs.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((GameEditorViewModel)recipient).ResetGameData());
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((GameEditorViewModel)recipient).RefreshTargets());
        // Typed values live only on this page until Save: closing the app would drop them unasked.
        WeakReferenceMessenger.Default.Register<UnsavedEditsRequestMessage>(this, (recipient, message) =>
        {
            if (((GameEditorViewModel)recipient).PendingCount > 0)
            {
                message.Reply("typed changes on the Editor page");
            }
        });
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
    [NotifyPropertyChangedFor(nameof(HasNoPendingChanges))]
    private int _pendingCount;

    public bool HasNoPendingChanges => PendingCount == 0;

    [ObservableProperty]
    private string _pendingSummary = "Nothing changed yet.";

    /// <summary>"Your changes", one line per changed value, each with its own × (undo).</summary>
    public ObservableCollection<EditorPendingChange> PendingChanges { get; } = [];

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

    // The values shown are the game's with the target mod's changes on top; typed changes come
    // along to the newly chosen mod instead of being lost or applied to values not on screen.
    private void ReloadForTarget() => ReloadKeepingTyped("don't apply to that mod's version of this item");

    /// <summary>Reloads the shown item's cards, putting back every typed value that still has a place (and each card's own-copy choice).</summary>
    /// <param name="whyDropped">Ends "N typed change(s) …, so they were dropped."</param>
    private void ReloadKeepingTyped(string whyDropped)
    {
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
            StatusMessage = $"{typed.Count - carried} typed change(s) {whyDropped}, so they were dropped.";
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

            // The mod editor may have saved into this mod since its values were shown here: the
            // cards (and what was typed against them) would be out of date.
            if (!_writer.TrySave(folder, package, target.IsNew ? null : _shownStamp, out _))
            {
                StatusMessage = null;
                ReloadKeepingTyped("aren't in that mod's new version");
                StatusMessage = "Not saved: that mod was changed somewhere else (an open mod editor?) since it was shown here. Its current values are shown now, with your typed changes on top; Save again to save them."
                    + (StatusMessage is { } dropped ? $" {dropped}" : "");
                return;
            }

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
        _statChoices ??= GameStats.StatChoices(game);
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
        _shownStamp = null;
        if (SelectedTarget is not { IsNew: false, FolderName: { } folder })
        {
            return null;
        }

        try
        {
            var path = _repository.GetFolderPath(folder);
            var package = ExmodFolder.ReadPackageOnly(path, _repository.ListFolderFiles(folder));
            _shownStamp = _writer.StampOf(folder, package.FileName);
            return package;
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
        var lines = pending.Select(p => (Text: $"{p.Section.Title} · {p.Field.Label}: {p.Field.ValueText}" + SharedNote(p.Section), p.Field)).ToList();
        PendingSummary = pending.Count == 0
            ? "Nothing changed yet."
            : string.Join("\n", lines.Take(8).Select(l => l.Text))
              + (pending.Count > 8 ? $"\n…and {pending.Count - 8} more" : "");
        ObservableCollectionSync.SyncTo(PendingChanges, [.. lines.Select(l => new EditorPendingChange(l.Text, l.Field))]);
    }

    private static string SharedNote(GameItemSectionViewModel section) =>
        section.IsShared && !section.Section.IsOwnCopy ? (section.OwnCopy ? "  (its own copy)" : $"  (all {section.Section.SharedBy:N0} items)") : "";

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
        // Refresh game data used to throw away whatever was typed here, without a word.
        ReloadKeepingTyped("aren't in the refreshed game data");
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
