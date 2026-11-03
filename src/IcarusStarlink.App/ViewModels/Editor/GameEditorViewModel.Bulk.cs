using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One item in the bulk edit's preview: its values now, the new ones (worked out, or typed over
/// for just this item: "2, 1, 5" for three ingredients), and a tick to leave it out.
/// </summary>
public sealed partial class EditorBulkRow : ObservableObject
{
    private readonly Action<EditorBulkRow>? _edited;

    /// <param name="typed">What was typed over this row before the preview was rebuilt (kept until another value is picked).</param>
    public EditorBulkRow(EditorItemEntry item, IReadOnlyList<GameItemFieldViewModel> fields, IReadOnlyList<string> computed, bool shared, string? typed = null, Action<EditorBulkRow>? edited = null)
    {
        Item = item;
        Fields = fields;
        IsShared = shared;
        CurrentText = string.Join(", ", fields.Select(f => f.ValueText.Trim()));
        ComputedText = string.Join(", ", computed);
        _newText = typed ?? ComputedText;
        _edited = edited;
    }

    public EditorItemEntry Item { get; }

    /// <summary>The item's values this changes (one, or one per ingredient / cost).</summary>
    internal IReadOnlyList<GameItemFieldViewModel> Fields { get; }

    /// <summary>"4, 2" for an item's two ingredients.</summary>
    public string CurrentText { get; }

    /// <summary>What the bulk edit works out.</summary>
    public string ComputedText { get; }

    /// <summary>The new value(s), editable: type over it to give this item something else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(IsEdited))]
    private string _newText;

    /// <summary>Typed over by hand (not what the bulk edit works out).</summary>
    public bool IsEdited => NewText.Trim() != ComputedText;

    public string? Error => TryChanges(out _, out var error) ? null : error;

    /// <summary>The value is on a row other items use too: the shared-rows choice decides what happens.</summary>
    public bool IsShared { get; }

    [ObservableProperty]
    private bool _include = true;

    partial void OnNewTextChanged(string value) => _edited?.Invoke(this);

    /// <summary>The values to put in (only those that differ from now); false with the reason when the typed text isn't right.</summary>
    internal bool TryChanges(out List<(GameItemFieldViewModel Field, string Text)> changes, out string? error)
    {
        changes = [];
        if (!BulkEdit.TryReadValues(NewText, [.. Fields.Select(f => f.IsWholeNumber)], out var values, out error))
        {
            return false;
        }

        for (var i = 0; i < Fields.Count; i++)
        {
            var text = EditorValueText.FormatNumber(values[i]);
            if (text != Fields[i].ValueText.Trim())
            {
                changes.Add((Fields[i], text));
            }
        }

        return true;
    }
}

/// <summary>
/// Bulk edit: tick items in the list (or let a value's chip tick them), pick a value they share,
/// set, multiply or add, look at the preview (typing over any item's new value), and put it into
/// Your changes. It fills in the same
/// per-item changes typing does, so they're reviewed, undone and saved the same way.
/// </summary>
public sealed partial class GameEditorViewModel
{
    /// <summary>Cards of ticked items nothing was typed for yet, built for the bulk edit (against the target mod read then).</summary>
    private readonly Dictionary<string, List<GameItemSectionViewModel>> _bulkCards = new(StringComparer.OrdinalIgnoreCase);
    private GameItemLookup? _bulkLookup;
    private BulkUndo? _bulkUndo;
    private bool _suspendPending;
    private bool _ticking;

    /// <summary>Item name → what was typed over its preview row, for the value picked now.</summary>
    private readonly Dictionary<string, string> _bulkOverrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the last bulk edit changed, to take it back out.</summary>
    private sealed record BulkUndo(List<(GameItemFieldViewModel Field, string Text)> Values, List<(GameItemSectionViewModel Card, bool OwnCopy)> Copies, List<string> Items);

    public IReadOnlyList<BulkOperationChoice> BulkOperations => BulkEdit.Operations;

    public IReadOnlyList<EditorBulkShortcut> BulkShortcuts => BulkEdit.Shortcuts;

    public IReadOnlyList<string> QuickMultipliers => BulkEdit.QuickMultipliers;

    public ObservableCollection<BulkFieldChoice> BulkFields { get; } = [];

    public ObservableCollection<EditorBulkRow> BulkRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTicked), nameof(TickedText), nameof(BulkTitle))]
    private int _tickedCount;

    public bool HasTicked => TickedCount > 0;

    public string TickedText => $"{TickedCount:N0} ticked";

    public string BulkTitle => TickedCount == 1 ? "Change 1 item at once" : $"Change {TickedCount:N0} items at once";

    /// <summary>The middle shows the bulk edit instead of an item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowItemCards), nameof(ShowPickPrompt))]
    private bool _bulkMode;

    [ObservableProperty]
    private BulkFieldChoice? _selectedBulkField;

    [ObservableProperty]
    private BulkOperationChoice _selectedBulkOperation = BulkEdit.Operations[0];

    [ObservableProperty]
    private string _bulkValueText = "2";

    /// <summary>Shared rows get each item's own copy (true, the default) or are changed for every item using them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BulkChangeShared))]
    private bool _bulkOwnCopy = true;

    public bool BulkChangeShared
    {
        get => !BulkOwnCopy;
        set => BulkOwnCopy = !value;
    }

    [ObservableProperty]
    private string? _bulkError;

    [ObservableProperty]
    private string _bulkSummary = "";

    /// <summary>How many of the items use a shared row; null hides the shared-rows choice.</summary>
    [ObservableProperty]
    private string? _bulkSharedText;

    [ObservableProperty]
    private bool _canUndoBulk;

    partial void OnBulkModeChanged(bool value)
    {
        if (value)
        {
            RefreshBulkFields();
        }
    }

    partial void OnSelectedBulkFieldChanged(BulkFieldChoice? value)
    {
        _bulkOverrides.Clear(); // typed for the value picked before
        RefreshBulkPreview();
    }

    partial void OnSelectedBulkOperationChanged(BulkOperationChoice value) => RefreshBulkPreview();

    partial void OnBulkValueTextChanged(string value) => RefreshBulkPreview();

    [RelayCommand]
    private void OpenBulk() => BulkMode = true;

    /// <summary>Back to the item on show (the ticks stay).</summary>
    [RelayCommand]
    private void CloseBulk() => BulkMode = false;

    /// <summary>Ticks every item the search and filters show.</summary>
    [RelayCommand]
    private void TickAllShown() => SetTicks(FilteredItems, true);

    [RelayCommand]
    private void UntickAll() => SetTicks(_allItems, false);

    /// <summary>
    /// A value's chip: ticks the items the list shows now that have that card (pick a category or
    /// filter first to narrow it), and picks the value. Nothing changes until it's put into Your changes.
    /// </summary>
    [RelayCommand]
    private void PickShortcut(EditorBulkShortcut? shortcut)
    {
        if (shortcut is null || _allItems.Count == 0)
        {
            return;
        }

        var shown = FilteredItems.ToHashSet();
        _ticking = true;
        try
        {
            foreach (var item in _allItems)
            {
                item.IsTicked = shown.Contains(item) && item.Info.Has.Contains(shortcut.Has);
            }
        }
        finally
        {
            _ticking = false;
        }

        TicksChanged();
        BulkMode = true;
        SelectedBulkField = BulkFields.FirstOrDefault(f => f.Key == shortcut.Field);
        if (SelectedBulkField is null)
        {
            BulkSummary = $"None of the {TickedCount:N0} items has {shortcut.Label.ToLowerInvariant()} to change.";
        }
    }

    /// <summary>A quick button beside the number: multiply by it.</summary>
    [RelayCommand]
    private void MultiplyBy(string? factor)
    {
        if (factor is null)
        {
            return;
        }

        SelectedBulkOperation = BulkEdit.Operations.First(o => o.Operation == BulkOperation.Multiply);
        BulkValueText = factor;
    }

    private void SetTicks(IEnumerable<EditorItemEntry> items, bool ticked)
    {
        _ticking = true;
        try
        {
            foreach (var item in items.ToList())
            {
                item.IsTicked = ticked;
            }
        }
        finally
        {
            _ticking = false;
        }

        TicksChanged();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_ticking && e.PropertyName == nameof(EditorItemEntry.IsTicked))
        {
            TicksChanged();
        }
    }

    private void TicksChanged()
    {
        TickedCount = _allItems.Count(i => i.IsTicked);
        if (BulkMode)
        {
            RefreshBulkFields();
        }
    }

    /// <summary>The ticked items' cards are rebuilt next time (another target mod, refreshed game data, Discard): the last bulk edit can't be taken back after that.</summary>
    private void ClearBulkCache()
    {
        _bulkCards.Clear();
        _bulkLookup = null;
        _bulkUndo = null;
        CanUndoBulk = false;
    }

    /// <summary>An item's cards as the page has them: the shown item's, a waiting item's, else built for the bulk edit.</summary>
    private IReadOnlyList<GameItemSectionViewModel> CardsFor(string itemName)
    {
        if (string.Equals(_shownItem?.Name, itemName, StringComparison.OrdinalIgnoreCase))
        {
            return Sections;
        }

        if (_drafts.TryGetValue(itemName, out var draft))
        {
            return draft;
        }

        if (_bulkCards.TryGetValue(itemName, out var built))
        {
            return built;
        }

        if (Game is not { } game)
        {
            return [];
        }

        // One lookup for every ticked item: each whole-table scan once, not once per item.
        _bulkLookup ??= new GameItemLookup(game, ReadTargetPackage(out _));
        built = GameItemEditor.Describe(game, _bulkLookup.Mod, itemName, _bulkLookup) is { } view ? BuildCards(game, view) : [];
        _bulkCards[itemName] = built;
        return built;
    }

    private IEnumerable<(GameItemSectionViewModel Card, GameItemFieldViewModel Field, BulkFieldKey Key)> NumberFields(string itemName) =>
        CardsFor(itemName).SelectMany(card => card.Fields.Where(f => f.IsNumber && !f.IsDropped).Select(f => (card, f, BulkFieldKey.Of(card.Section.Key, f.Field.Path))));

    /// <summary>The values the ticked items have, most shared first.</summary>
    private void RefreshBulkFields()
    {
        var keep = SelectedBulkField?.Key;
        var found = new Dictionary<BulkFieldKey, (string Label, HashSet<string> Items)>();
        foreach (var item in _allItems.Where(i => i.IsTicked))
        {
            foreach (var (card, field, key) in NumberFields(item.Name))
            {
                if (!found.TryGetValue(key, out var entry))
                {
                    found[key] = entry = (BulkEdit.LabelOf(key, card.Title, field.Label), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                entry.Items.Add(item.Name);
            }
        }

        BulkFields.Clear();
        foreach (var (key, (label, items)) in found.OrderByDescending(f => f.Value.Items.Count).ThenBy(f => f.Value.Label, StringComparer.OrdinalIgnoreCase))
        {
            BulkFields.Add(new BulkFieldChoice(key, label, items.Count));
        }

        var again = BulkFields.FirstOrDefault(f => f.Key == keep) ?? BulkFields.FirstOrDefault();
        if (again == SelectedBulkField)
        {
            RefreshBulkPreview(); // same choice, new items or values
        }
        else
        {
            SelectedBulkField = again;
        }
    }

    /// <summary>Every ticked item's values now and after; items whose value wouldn't change are left out.</summary>
    private void RefreshBulkPreview()
    {
        BulkRows.Clear();
        BulkError = null;
        BulkSharedText = null;
        if (TickedCount == 0)
        {
            BulkSummary = "Tick items in the list (or Tick all shown), or start with one of the ideas above.";
            return;
        }

        if (SelectedBulkField is not { } choice)
        {
            BulkSummary = "The ticked items have no number to change together.";
            return;
        }

        if (!EditorValueText.TryParseNumber(BulkValueText, out var amount))
        {
            BulkError = "Needs a number, like 2 or 0.5.";
            BulkSummary = "";
            return;
        }

        var shared = 0;
        foreach (var item in _allItems.Where(i => i.IsTicked))
        {
            var matches = NumberFields(item.Name).Where(m => m.Key == choice.Key).ToList();
            var fields = new List<GameItemFieldViewModel>();
            var computed = new List<string>();
            foreach (var (_, field, _) in matches)
            {
                if (EditorValueText.TryParseNumber(field.ValueText, out var current)
                    && BulkEdit.Apply(SelectedBulkOperation.Operation, amount, current, field.IsWholeNumber) is { } result
                    && EditorValueText.FormatNumber(result) is var text && text != field.ValueText.Trim())
                {
                    fields.Add(field);
                    computed.Add(text);
                }
            }

            if (fields.Count == 0)
            {
                continue;
            }

            var isShared = matches.Any(m => fields.Contains(m.Field) && m.Card.IsShared && !m.Card.Section.IsOwnCopy);
            shared += isShared ? 1 : 0;
            BulkRows.Add(new EditorBulkRow(item, fields, computed, isShared, _bulkOverrides.GetValueOrDefault(item.Name), RememberTyped));
        }

        var values = BulkRows.Sum(r => r.Fields.Count);
        BulkSummary = BulkRows.Count == 0
            ? "Nothing would change: every ticked item already has that value."
            : $"{BulkRows.Count:N0} item{(BulkRows.Count == 1 ? "" : "s")} change{(BulkRows.Count == 1 ? "s" : "")} ({values:N0} value{(values == 1 ? "" : "s")}). Untick any to leave out.";
        BulkSharedText = shared == 0 ? null
            : $"{shared:N0} of them use a row other items share (like the basic decay row, used by hundreds of items).";
    }

    /// <summary>A preview row typed over: kept while the preview is rebuilt (another number or How), until another value is picked.</summary>
    private void RememberTyped(EditorBulkRow row)
    {
        if (row.IsEdited)
        {
            _bulkOverrides[row.Item.Name] = row.NewText;
        }
        else
        {
            _bulkOverrides.Remove(row.Item.Name);
        }
    }

    /// <summary>Puts the previewed values into Your changes (nothing is saved until Save).</summary>
    [RelayCommand]
    private void ApplyBulk()
    {
        var rows = BulkRows.Where(r => r.Include).ToList();
        if (rows.Count == 0)
        {
            BulkError = "Nothing to put in: every item is unticked, or nothing would change.";
            return;
        }

        if (rows.FirstOrDefault(r => r.Error is not null) is { } bad)
        {
            BulkError = $"{bad.Item.Title}: {bad.Error}";
            return;
        }

        var undo = new BulkUndo([], [], []);
        _suspendPending = true;
        try
        {
            foreach (var row in rows)
            {
                var cards = CardsFor(row.Item.Name);
                row.TryChanges(out var changes, out _);
                foreach (var (field, text) in changes)
                {
                    undo.Values.Add((field, field.ValueText));
                    field.ValueText = text;
                }

                foreach (var card in cards.Where(c => c.IsShared && c.Fields.Any(f => changes.Any(ch => ch.Field == f))))
                {
                    undo.Copies.Add((card, card.OwnCopy));
                    card.OwnCopy = BulkOwnCopy;
                }

                // Now an item with changes like any other: it waits in Your changes.
                if (_bulkCards.Remove(row.Item.Name, out var built))
                {
                    _drafts[row.Item.Name] = built;
                }

                undo.Items.Add(row.Item.Name);
            }
        }
        finally
        {
            _suspendPending = false;
        }

        UpdatePending();
        _bulkUndo = undo;
        CanUndoBulk = true;
        StatusMessage = $"Put {undo.Values.Count:N0} change(s) to {rows.Count:N0} item(s) into Your changes. Nothing is saved until you Save.";

        _bulkOverrides.Clear();

        // Not previewed again from the new values: a second click would halve everything twice.
        // Changing what, how or the number shows a new preview.
        BulkRows.Clear();
        BulkSharedText = null;
        BulkSummary = $"Done: {rows.Count:N0} item(s) are in Your changes. Pick another value, or change the number, to go on.";
    }

    /// <summary>Takes the last bulk edit back out of Your changes (values and shared-row choices as they were).</summary>
    [RelayCommand]
    private void UndoBulk()
    {
        if (_bulkUndo is not { } undo)
        {
            return;
        }

        _suspendPending = true;
        try
        {
            foreach (var (field, text) in Enumerable.Reverse(undo.Values))
            {
                field.ValueText = text;
            }

            foreach (var (card, ownCopy) in Enumerable.Reverse(undo.Copies))
            {
                card.OwnCopy = ownCopy;
            }

            foreach (var name in undo.Items)
            {
                if (_drafts.TryGetValue(name, out var cards) && !cards.Any(c => c.HasTyped))
                {
                    _drafts.Remove(name);
                    _bulkCards[name] = cards;
                }
            }
        }
        finally
        {
            _suspendPending = false;
        }

        _bulkUndo = null;
        CanUndoBulk = false;
        UpdatePending();
        StatusMessage = $"Took the last bulk change back out ({undo.Values.Count:N0} value(s)).";
        RefreshBulkPreview();
    }
}
