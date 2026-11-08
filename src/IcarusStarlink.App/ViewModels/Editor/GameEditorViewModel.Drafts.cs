using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One item in "Your changes": its icon and name (click to open it), its changed values, and an × that throws them all away.</summary>
public sealed record EditorPendingGroup(string ItemName, EditorItemEntry? Item, IReadOnlyList<EditorPendingChange> Changes, bool IsShown)
{
    public string Title => Item?.Title ?? ItemName;

    public string CountText => Changes.Count == 1 ? "1 change" : $"{Changes.Count} changes";

    /// <summary>What a screen reader says for the group.</summary>
    public override string ToString() => $"{Title}, {CountText}";

    // Equal when it shows the same lines: rebuilding "Your changes" on every keystroke then only
    // replaces the group that changed, not all of them (a bulk edit can leave hundreds).
    public bool Equals(EditorPendingGroup? other) =>
        other is not null && ItemName == other.ItemName && Item == other.Item && IsShown == other.IsShown && Changes.SequenceEqual(other.Changes);

    public override int GetHashCode() => HashCode.Combine(ItemName, IsShown, Changes.Count);
}

/// <summary>
/// Changes to many items before one Save: the cards of an item left with typed changes wait here
/// (values, added stats, own-copy choices) until it's picked again or saved; the shown item's cards
/// are in Sections. "Your changes" lists both, grouped by item.
/// </summary>
public sealed partial class GameEditorViewModel
{
    /// <summary>Item name → its cards, built against the current target mod, with typed changes on them.</summary>
    private readonly Dictionary<string, List<GameItemSectionViewModel>> _drafts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Your changes" grouped by item: the shown item first, then the others by name.</summary>
    public ObservableCollection<EditorPendingGroup> PendingGroups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPendingChanges), nameof(PendingHeaderText))]
    private int _pendingCount;

    public bool HasNoPendingChanges => PendingCount == 0;

    /// <summary>How many items have changes waiting to be saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingHeaderText))]
    private int _editedItemCount;

    public string PendingHeaderText => PendingCount == 0 ? "YOUR CHANGES"
        : EditedItemCount > 1 ? $"YOUR CHANGES ({PendingCount:N0} IN {EditedItemCount:N0} ITEMS)"
        : $"YOUR CHANGES ({PendingCount:N0})";

    /// <summary>The item name in Your changes: shows that item (its changes come with it).</summary>
    [RelayCommand]
    private void OpenPendingItem(EditorPendingGroup? group)
    {
        if (group is { IsShown: false })
        {
            Open(group.ItemName);
        }
    }

    /// <summary>The × beside an item in Your changes: throws away every change typed for it.</summary>
    [RelayCommand]
    private void DiscardPendingItem(EditorPendingGroup? group)
    {
        if (group is null || !ConfirmDiscard($"Throw away the {group.CountText} you typed for {group.Title}?"))
        {
            return;
        }

        if (group.IsShown)
        {
            LoadItem();
        }
        else
        {
            _drafts.Remove(group.ItemName);
            UpdatePending();
        }

        if (BulkMode)
        {
            RefreshBulkPreview(); // its values are the game's (or the mod's) again
        }
    }

    /// <summary>Every item with something typed: the shown one, then the waiting ones.</summary>
    private List<(string Name, IReadOnlyList<GameItemSectionViewModel> Cards)> EditedItems()
    {
        var edited = new List<(string Name, IReadOnlyList<GameItemSectionViewModel> Cards)>();
        if (_shownItem is { } shown && Sections.Any(s => s.HasTyped))
        {
            edited.Add((shown.Name, [.. Sections]));
        }

        edited.AddRange(_drafts.Select(d => (d.Key, (IReadOnlyList<GameItemSectionViewModel>)d.Value)));
        return edited;
    }

    /// <summary>The shown item is being left: its cards wait when something was typed on them.</summary>
    private void StashShownItem()
    {
        if (_shownItem is { } shown && Sections.Any(s => s.HasTyped))
        {
            _drafts[shown.Name] = [.. Sections];
        }
    }

    private List<GameItemSectionViewModel> BuildCards(GameTableIndex game, GameItemView view)
    {
        _statChoices ??= GameStats.StatChoices(game);
        return [.. view.Sections.Select(section => new GameItemSectionViewModel(section, _statChoices, UpdatePending, game, LinkChoicesFor, ItemChoices))];
    }

    /// <summary>The game's items, for a recipe card's "Add ingredient" (rebuilt with the list on Refresh game data).</summary>
    private IReadOnlyList<ItemChoice> ItemChoices() => _itemChoices ??= [.. _allItems.Select(i => i.Info.Item)];

    private IReadOnlyList<ItemChoice>? _itemChoices;

    /// <summary>Table → the rows its drop-downs offer, built once per table (2,000+ item templates).</summary>
    private readonly Dictionary<string, IReadOnlyList<LinkChoice>> _linkChoices = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<LinkChoice> LinkChoicesFor(string table)
    {
        if (!_linkChoices.TryGetValue(table, out var choices))
        {
            choices = Game is { } game ? [.. GameItemCatalog.LinkChoices(game, table).Select(c => new LinkChoice(c.Name, c.Display))] : [];
            _linkChoices[table] = choices;
        }

        return choices;
    }

    /// <summary>
    /// Puts what was typed on <paramref name="from"/> (and each card's own-copy choice) onto the same
    /// item's new cards; returns how many typed values had no place there. A list entry (an
    /// ingredient's amount, a bench) goes to the entry for the same thing, wherever it is in the new
    /// list; ingredients and benches added here are added again; a recipe is never left made nowhere.
    /// </summary>
    private static int CarryTyped(IReadOnlyList<GameItemSectionViewModel> from, IReadOnlyList<GameItemSectionViewModel> to)
    {
        var dropped = 0;
        foreach (var old in from)
        {
            var card = to.FirstOrDefault(s => s.Section.Key == old.Section.Key);
            foreach (var field in old.Fields.Where(f => f.IsTyped).ToList())
            {
                if (card is null)
                {
                    dropped++;
                }
                else if (field.AddedEntry is not null)
                {
                    dropped += card.CarryAdded(field) ? 0 : 1;
                }
                else if (card.SameFieldAs(old, field) is { } same)
                {
                    same.ValueText = field.ValueText;
                    same.IsDropped = field.IsDropped && same.CanDrop;
                }
                else
                {
                    dropped++;
                }
            }
        }

        foreach (var card in to)
        {
            if (from.FirstOrDefault(s => s.Section.Key == card.Section.Key) is { } before)
            {
                card.OwnCopy = before.OwnCopy;
            }

            dropped += card.KeepABench();
        }

        return dropped;
    }

    /// <summary>Rebuilds every waiting item against <paramref name="target"/> (another Save to, refreshed game data, the mod changed elsewhere), keeping what was typed; returns how many typed values had no place.</summary>
    private int RebuildDrafts(ExmodPackage? target)
    {
        ClearBulkCache(); // built against the old target too
        if (_drafts.Count == 0)
        {
            return 0;
        }

        var dropped = 0;
        // One lookup for every waiting item (a fresh one per item scans whole tables, ~18 ms each):
        // the target isn't changed while they're rebuilt.
        var lookup = Game is { } current ? new GameItemLookup(current, target) : null;
        foreach (var (name, old) in _drafts.ToList())
        {
            List<GameItemSectionViewModel> cards = [];
            try
            {
                if (lookup is not null && GameItemEditor.Describe(lookup.Game, target, name, lookup) is { } view)
                {
                    cards = BuildCards(lookup.Game, view);
                }
            }
            catch (Exception ex)
            {
                // A value of the wrong kind in the target (a link as plain text): its typed values
                // have no place, as for an item that has gone, instead of closing the app.
                Serilog.Log.Warning(ex, "The Editor page couldn't read item {Item}", name);
            }

            dropped += CarryTyped(old, cards);
            if (cards.Any(c => c.HasTyped))
            {
                _drafts[name] = cards;
            }
            else
            {
                _drafts.Remove(name);
            }
        }

        UpdatePending();
        return dropped;
    }

    private void UpdatePending()
    {
        if (_suspendPending)
        {
            return; // a bulk edit setting many values: once at the end
        }

        var groups = new List<EditorPendingGroup>();
        if (_shownItem is { } shown && Lines(Sections) is { Count: > 0 } shownLines)
        {
            groups.Add(new EditorPendingGroup(shown.Name, FindItem(shown.Name), shownLines, IsShown: true));
        }

        foreach (var (name, cards) in _drafts.OrderBy(d => FindItem(d.Key)?.Title ?? d.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (Lines(cards) is { Count: > 0 } lines)
            {
                groups.Add(new EditorPendingGroup(name, FindItem(name), lines, IsShown: false));
            }
        }

        PendingCount = groups.Sum(g => g.Changes.Count);
        EditedItemCount = groups.Count;
        ObservableCollectionSync.SyncTo(PendingGroups, groups);

        // The pencil in the list: typed for and not saved (half-typed numbers too). A waiting item
        // whose last change was undone with × keeps its cards here, but has nothing typed.
        var unsaved = _drafts.Where(d => d.Value.Any(c => c.HasTyped)).Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_shownItem is { } current && Sections.Any(s => s.HasTyped))
        {
            unsaved.Add(current.Name);
        }

        foreach (var item in _allItems)
        {
            item.HasUnsavedChanges = unsaved.Contains(item.Name);
        }
    }

    private static List<EditorPendingChange> Lines(IEnumerable<GameItemSectionViewModel> cards) =>
        [.. cards.SelectMany(s => s.Fields.Where(f => f.IsPending).Select(f => new EditorPendingChange($"{s.Title} · {f.Label}: {(f.IsDropped ? "taken out" : f.IsToggle ? (f.IsOn ? "on" : "off") : f.ValueText)}" + SharedNote(s), f)))];

    private static string SharedNote(GameItemSectionViewModel section) =>
        section.IsShared && !section.Section.IsOwnCopy ? (section.OwnCopy ? "  (its own copy)" : $"  (all {section.Section.SharedBy:N0} items)") : "";
}
