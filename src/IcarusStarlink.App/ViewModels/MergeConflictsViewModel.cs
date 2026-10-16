using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A winner choice in a conflict's dropdown: a mod by name, or null for the merge's default.</summary>
public sealed record ConflictWinnerChoice(string? ModName, string Label)
{
    public override string ToString() => Label;
}

/// <summary>How a cell in the side-by-side comparison compares with the game's own value.</summary>
public enum ConflictCellState
{
    Same,
    Added,
    Missing,
    Different,
    Empty,
}

public sealed record ConflictCell(string? Text, ConflictCellState State)
{
    public bool IsAdded => State == ConflictCellState.Added;

    public bool IsMissing => State == ConflictCellState.Missing;

    public bool IsDifferent => State == ConflictCellState.Different;
}

/// <summary>One row of the comparison: one cell per column (the game, each mod, the result).</summary>
public sealed record ConflictMatrixRow(IReadOnlyList<ConflictCell> Cells);

/// <summary>A column header: the game now, a mod (clickable to make it win), or the result.</summary>
public sealed record ConflictColumn(string Title, string? ModName, bool IsGame, bool IsResult, bool IsWinner)
{
    public bool CanPick => ModName is not null && !IsWinner;
}

/// <summary>One field two or more queued mods change differently: who wins, and the values side by side.</summary>
public sealed partial class MergeConflictViewModel : ObservableObject
{
    private readonly MergeRuleRegistry _registry;
    private readonly bool _defaultIsLastWins;

    public MergeConflictViewModel(FieldConflict conflict, string? winner, MergeRuleRegistry registry)
    {
        Conflict = conflict;
        _registry = registry;
        var group = Group();
        _defaultIsLastWins = registry.DefaultIsLastWriteWins(group);
        DefaultLabel = _defaultIsLastWins
            ? $"Default: last in the queue wins ({conflict.Candidates[^1].ModName})"
            : "Default: combine every mod's additions";
        Choices = [new(null, DefaultLabel), .. conflict.Candidates.Select(c => new ConflictWinnerChoice(c.ModName, $"{c.ModName} wins"))];
        _selectedChoice = Choices.FirstOrDefault(c => c.ModName is not null && string.Equals(c.ModName, winner, StringComparison.Ordinal)) ?? Choices[0];
    }

    public FieldConflict Conflict { get; }

    public string Key => MergeConflictsViewModel.WinnerKey(Conflict.CurrentFile, Conflict.ItemName, Conflict.FieldName);

    public string Row => Conflict.ItemName;

    public string Field => Conflict.FieldName;

    public string Table => GameTableIndex.TableNameOf(Conflict.CurrentFile).Replace("D_", "", StringComparison.Ordinal);

    public IReadOnlyList<string> ModNames => [.. Conflict.Candidates.Select(c => c.ModName)];

    public string ModsText => string.Join(" · ", ModNames);

    public string DefaultLabel { get; }

    public IReadOnlyList<ConflictWinnerChoice> Choices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Winner), nameof(IsPicked), nameof(WinnerText), nameof(Columns), nameof(Matrix))]
    private ConflictWinnerChoice _selectedChoice;

    /// <summary>The mod picked to win, or null for the default.</summary>
    public string? Winner => SelectedChoice.ModName;

    public bool IsPicked => Winner is not null;

    public string WinnerText => Winner is { } w ? $"{w} (your pick)" : _defaultIsLastWins ? $"{Conflict.Candidates[^1].ModName} (default)" : "combined (default)";

    partial void OnSelectedChoiceChanged(ConflictWinnerChoice value) => _matrix = null;

    public void Pick(string? modName) => SelectedChoice = Choices.FirstOrDefault(c => string.Equals(c.ModName, modName, StringComparison.Ordinal)) ?? Choices[0];

    [RelayCommand]
    private void PickColumn(ConflictColumn? column)
    {
        if (column?.ModName is { } name)
        {
            Pick(name);
        }
    }

    /// <summary>What the merge will write for this field with the current choice.</summary>
    public JsonNode? Result
    {
        get
        {
            var change = Winner is { } w
                ? Conflict.Candidates.First(c => c.ModName == w).Change
                : _registry.Resolve(Group());
            return change.IsFieldRemoved ? null : change.NewValue;
        }
    }

    public IReadOnlyList<ConflictColumn> Columns =>
    [
        new("THE GAME NOW", null, IsGame: true, IsResult: false, IsWinner: false),
        .. Conflict.Candidates.Select(c => new ConflictColumn(c.ModName, c.ModName, IsGame: false, IsResult: false, IsWinner: Winner == c.ModName || (Winner is null && _defaultIsLastWins && c == Conflict.Candidates[^1]))),
        new("RESULT", null, IsGame: false, IsResult: true, IsWinner: false),
    ];

    public int ColumnCount => Conflict.Candidates.Count + 2;

    /// <summary>Lists are compared entry by entry; other values side by side.</summary>
    public bool IsList => Values().All(v => v is null || ValueDiff.Entries(v) is not null) && Values().Any(v => v is not null);

    private IReadOnlyList<ConflictMatrixRow>? _matrix;

    /// <summary>
    /// The comparison, kdiff-style: for lists one row per entry, lined up across the game, every
    /// mod and the result (green: the game doesn't have it, red: the game has it but this one
    /// doesn't); for other values one row, each cell coloured where it differs from the game.
    /// </summary>
    public IReadOnlyList<ConflictMatrixRow> Matrix => _matrix ??= BuildMatrix();

    private List<JsonNode?> Values() =>
        [Conflict.HasBaseValue ? Conflict.BaseValue : null, .. Conflict.Candidates.Select(c => c.Change.IsFieldRemoved ? null : c.Change.NewValue), Result];

    private List<ConflictMatrixRow> BuildMatrix()
    {
        var values = Values();
        if (!IsList)
        {
            var game = values[0];
            return [new ConflictMatrixRow([.. values.Select((v, i) => new ConflictCell(
                v is null ? (i == 0 ? "(not set)" : "(deleted)") : Pretty(v),
                i == 0 ? ConflictCellState.Same : JsonNode.DeepEquals(v, game) ? ConflictCellState.Same : ConflictCellState.Different))])];
        }

        var lists = values.Select(v => ValueDiff.Entries(v) ?? []).ToList();
        var order = new List<string>();
        foreach (var list in lists)
        {
            foreach (var entry in list.Where(e => !order.Contains(e, StringComparer.OrdinalIgnoreCase)))
            {
                order.Add(entry);
            }
        }

        var gameHas = lists[0].ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. order.Select(entry => new ConflictMatrixRow([.. lists.Select((list, i) =>
        {
            var has = list.Contains(entry, StringComparer.OrdinalIgnoreCase);
            if (i == 0)
            {
                return new ConflictCell(has ? entry : null, has ? ConflictCellState.Same : ConflictCellState.Empty);
            }

            return has
                ? new ConflictCell(entry, gameHas.Contains(entry) ? ConflictCellState.Same : ConflictCellState.Added)
                : new ConflictCell(gameHas.Contains(entry) ? entry : null, gameHas.Contains(entry) ? ConflictCellState.Missing : ConflictCellState.Empty);
        })]))];
    }

    private FieldChangeGroup Group() => new(
        Conflict.CurrentFile, Conflict.ItemName, Conflict.FieldName,
        [.. Conflict.Candidates.Select(c => c.Change)], Conflict.HasBaseValue, Conflict.BaseValue);

    private static string Pretty(JsonNode value) =>
        value is JsonValue v ? v.ToString() : value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>Conflicts between the same set of mods, with "this mod wins all of these".</summary>
public sealed partial class MergeConflictGroupViewModel(IReadOnlyList<string> mods, IReadOnlyList<MergeConflictViewModel> conflicts) : ObservableObject
{
    public IReadOnlyList<string> Mods { get; } = mods;

    public IReadOnlyList<MergeConflictViewModel> Conflicts { get; } = conflicts;

    public string Title => string.Join("  ↔  ", Mods);

    public int Count => Conflicts.Count;

    [RelayCommand]
    private void WinAll(string? modName)
    {
        foreach (var conflict in Conflicts)
        {
            conflict.Pick(modName);
        }
    }
}

/// <summary>
/// Merge &amp; Install's conflict review: every field two or more queued mods change differently,
/// grouped by which mods collide, with the game's value, each mod's and the result side by side,
/// and a winner per line (or per group at once). Winners are kept by mod name, so they survive
/// reordering the queue, adding mods and restarting; they're saved with the profile.
/// </summary>
public sealed partial class MergeConflictsViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<string, string> _earlierWinners;
    private readonly List<MergeConflictViewModel> _all;

    public MergeConflictsViewModel(
        IReadOnlyList<FieldConflict> conflicts,
        IReadOnlyList<NewItemNameCollision> collisions,
        IReadOnlyDictionary<string, string>? winners)
    {
        _earlierWinners = winners ?? new Dictionary<string, string>();
        var registry = new MergeRuleRegistry();
        _all = [.. conflicts.Select(c => new MergeConflictViewModel(c, _earlierWinners.GetValueOrDefault(WinnerKey(c.CurrentFile, c.ItemName, c.FieldName)), registry))];
        foreach (var conflict in _all)
        {
            conflict.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MergeConflictViewModel.SelectedChoice))
                {
                    OnPropertyChanged(nameof(PickedCount));
                    OnPropertyChanged(nameof(Summary));
                }
            };
        }

        Groups = [.. _all.GroupBy(c => string.Join("|", c.ModNames)).OrderByDescending(g => g.Count())
            .Select(g => new MergeConflictGroupViewModel(g.First().ModNames, [.. g]))];
        Collisions = [.. collisions.Select(c => $"'{c.ItemName}' ({GameTableIndex.TableNameOf(c.CurrentFile)}) is a new item in {string.Join(", ", c.ModNames)}: both are merged into one.")];
        ConflictsView = CollectionViewSource.GetDefaultView(_all);
        ConflictsView.Filter = o => o is MergeConflictViewModel c
            && (SelectedGroup is null || SelectedGroup.Conflicts.Contains(c))
            && (SearchText.Length == 0 || c.Row.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || c.Field.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || c.ModsText.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        SelectedConflict = _all.FirstOrDefault();
    }

    public static string WinnerKey(string currentFile, string itemName, string fieldName) =>
        $"{currentFile.ToLowerInvariant()}|{itemName}|{fieldName}";

    public ObservableCollection<MergeConflictGroupViewModel> Groups { get; }

    public IReadOnlyList<string> Collisions { get; }

    public bool HasCollisions => Collisions.Count > 0;

    public ICollectionView ConflictsView { get; }

    public int PickedCount => _all.Count(c => c.IsPicked);

    public string Summary => _all.Count == 0
        ? "No conflicts: every changed value is changed by one mod only, or the mods agree."
        : $"{_all.Count} value(s) are changed differently by more than one queued mod. Each shows who wins now: by default the mod lower in the queue, "
          + "except lists that can take everyone's additions. Pick a winner per line, or a whole group at once on the left. "
          + $"{PickedCount} picked by you. Picks are saved with the profile and kept when the queue changes.";

    [ObservableProperty]
    private MergeConflictGroupViewModel? _selectedGroup;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private MergeConflictViewModel? _selectedConflict;

    partial void OnSelectedGroupChanged(MergeConflictGroupViewModel? value)
    {
        ConflictsView.Refresh();
        SelectedConflict = ConflictsView.Cast<MergeConflictViewModel>().FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value) => ConflictsView.Refresh();

    [RelayCommand]
    private void ShowAll() => SelectedGroup = null;

    /// <summary>Everything back to the merge's own default.</summary>
    [RelayCommand]
    private void ResetAll()
    {
        foreach (var conflict in _all)
        {
            conflict.Pick(null);
        }
    }

    /// <summary>The winners to save: the ones picked here, plus earlier picks for values not in conflict right now (a mod taken out of the queue for a while).</summary>
    public Dictionary<string, string> BuildWinners()
    {
        var winners = new Dictionary<string, string>(_earlierWinners, StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in _all)
        {
            winners.Remove(conflict.Key);
            if (conflict.Winner is { } winner)
            {
                winners[conflict.Key] = winner;
            }
        }

        return winners;
    }
}
