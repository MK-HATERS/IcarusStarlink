using System.Globalization;
using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A value the user saves that another turned-on mod still wins.</summary>
/// <param name="ByPick">A saved winner in Check the merge decides it (moving the mod doesn't change that).</param>
public sealed record EditorLoss(string What, string WinnerName, string WinnerValue, bool ByPick);

/// <summary>
/// The Editor's view of the merge: per game row, the values other turned-on mods change and what
/// the merge keeps (MergeEngine.Explain over Merge &amp; Install's own queue, saved winners
/// applied), and whether a value the user is saving would lose. Built off the UI thread.
/// </summary>
public sealed class EditorQueueEffects
{
    private readonly Dictionary<(string File, string Row), List<FieldOutcome>> _byRow = [];

    private EditorQueueEffects(MergeQueueState state, string? targetId)
    {
        State = state;
        TargetId = targetId;
        TargetIndex = targetId is null ? -1 : IndexOf(state.Ids, targetId);
    }

    public static EditorQueueEffects Empty { get; } = new(MergeQueueState.Empty, null);

    public MergeQueueState State { get; }

    /// <summary>The Save-to mod's id (its folder name); null for a new mod.</summary>
    public string? TargetId { get; }

    /// <summary>Where the Save-to mod is in the merge; -1 when it isn't turned on.</summary>
    public int TargetIndex { get; }

    /// <summary>Rows (file, row, lower case) another turned-on mod changes.</summary>
    public IReadOnlySet<(string File, string Row)> RowsChangedByOthers => _rowsChangedByOthers;

    private readonly HashSet<(string File, string Row)> _rowsChangedByOthers = [];

    /// <summary>Just enough for Losses (nothing worked out per row yet).</summary>
    public static EditorQueueEffects ForTarget(MergeQueueState state, string? targetId) => new(state, targetId);

    /// <summary>Every row another part of the merge changes, with what the merge keeps there.</summary>
    public static EditorQueueEffects Build(MergeQueueState state, string? targetId)
    {
        var effects = new EditorQueueEffects(state, targetId);
        var others = new HashSet<(string, string)>();
        for (var i = 0; i < state.OrderedChanges.Count; i++)
        {
            if (i != effects.TargetIndex)
            {
                others.UnionWith(state.OrderedChanges[i].Select(c => RowKey(c.CurrentFile, c.ItemName)));
            }
        }

        if (others.Count == 0)
        {
            return effects;
        }

        // Only rows someone else changes: the rest has nothing to show.
        var changes = state.OrderedChanges.Select(list => (IReadOnlyList<FieldChange>)[.. list.Where(c => others.Contains(RowKey(c.CurrentFile, c.ItemName)))]).ToList();
        foreach (var outcome in MergeEngine.Explain(changes, new MergeRuleRegistry(), WinnerIndexes(state.Winners, state.Ids), state.BaseTables))
        {
            if (outcome.ModIndexes.All(i => i == effects.TargetIndex))
            {
                continue;
            }

            var key = RowKey(outcome.CurrentFile, outcome.ItemName);
            if (!effects._byRow.TryGetValue(key, out var list))
            {
                list = [];
                effects._byRow[key] = list;
            }

            list.Add(outcome);
            effects._rowsChangedByOthers.Add(key);
        }

        return effects;
    }

    /// <summary>The values other turned-on mods change in this row (the Save-to mod's own may be among the candidates).</summary>
    public IReadOnlyList<FieldOutcome> For(string currentFile, string rowName) => _byRow.GetValueOrDefault(RowKey(currentFile, rowName)) ?? [];

    public string NameOf(int modIndex) => modIndex >= 0 && modIndex < State.Names.Count ? State.Names[modIndex] : "another mod";

    /// <summary>The merge's name for a value: a stat of the player's starting stats is merged on its own.</summary>
    public static string FieldKey(string currentFile, EditPath path) =>
        StatEntries.IsPerEntry(currentFile, path.Top) && path.Steps.Count > 0 && path.Steps[0] is string entry
            ? StatEntries.Compose(path.Top, entry)
            : path.Top;

    /// <summary>
    /// The values the user is saving that another turned-on mod would still win, with the Save-to
    /// mod's changes as they will be (<paramref name="targetChanges"/>); with
    /// <paramref name="moveTargetLast"/>, as if the mod were moved to the end of the merge list.
    /// Empty when the Save-to mod isn't turned on.
    /// </summary>
    /// <param name="saved">Each value saved: the merge's file, row and field, and how to name it ("Envirosuit · Weight").</param>
    public IReadOnlyList<EditorLoss> Losses(IReadOnlyList<FieldChange> targetChanges, IReadOnlyList<(string File, string Row, string Field, string What)> saved, bool moveTargetLast = false)
    {
        if (TargetIndex < 0 || saved.Count == 0)
        {
            return [];
        }

        var wanted = new HashSet<(string, string, string)>(saved.Select(s => (s.File, s.Row, s.Field)), FieldChangeKeyComparer.Instance);
        var ids = State.Ids.ToList();
        var ordered = State.OrderedChanges.ToList();
        ordered[TargetIndex] = StatEntries.Split(targetChanges);
        var target = TargetIndex;
        if (moveTargetLast)
        {
            // Last among the mods: the built-in options always merge after every mod.
            var end = ids.Count > 0 && ids[^1] == MergeInstallViewModel.OptionsWinnerId && target != ids.Count - 1 ? ids.Count - 1 : ids.Count;
            var (id, changes) = (ids[target], ordered[target]);
            ids.RemoveAt(target);
            ordered.RemoveAt(target);
            target = end - 1;
            ids.Insert(target, id);
            ordered.Insert(target, changes);
        }

        var filtered = ordered.Select(list => (IReadOnlyList<FieldChange>)[.. list.Where(c => wanted.Contains((c.CurrentFile, c.ItemName, c.FieldName)))]).ToList();
        var mine = new Dictionary<(string, string, string), FieldChange>(FieldChangeKeyComparer.Instance);
        foreach (var change in filtered[target])
        {
            mine[(change.CurrentFile, change.ItemName, change.FieldName)] = change;
        }

        var losses = new List<EditorLoss>();
        foreach (var outcome in MergeEngine.Explain(filtered, new MergeRuleRegistry(), WinnerIndexes(State.Winners, ids), State.BaseTables))
        {
            var key = (outcome.CurrentFile, outcome.ItemName, outcome.FieldName);
            // Only what this mod writes; a list the rules combine keeps its entries too.
            if (!mine.TryGetValue(key, out var own) || outcome.WinnerModIndex is not { } winner || winner == target
                || JsonNode.DeepEquals(outcome.Resolved.NewValue, own.NewValue))
            {
                continue;
            }

            var what = saved.First(s => FieldChangeKeyComparer.Instance.Equals((s.File, s.Row, s.Field), key)).What;
            var name = NameOf(IndexOf(State.Ids, ids[winner]));
            losses.Add(new EditorLoss(what, name, Shown(outcome.Resolved.NewValue), outcome.PickedIndex is not null));
        }

        return losses;
    }

    /// <summary>A value as one short line: a list or a group of values is too long to show.</summary>
    internal static string Shown(JsonNode? value) => value switch
    {
        null => "nothing",
        JsonValue v => EditorValueText.Display(v),
        JsonArray a => a.Count == 1 ? "a list of 1 entry" : $"a list of {a.Count} entries",
        _ => "other values",
    };

    /// <summary>The saved winners by mod index, for the merge's own picks (a winner not in the merge is left out).</summary>
    private static Dictionary<(string CurrentFile, string ItemName, string FieldName), int> WinnerIndexes(IReadOnlyDictionary<string, string> winners, IReadOnlyList<string> ids)
    {
        var picks = new Dictionary<(string, string, string), int>(FieldChangeKeyComparer.Instance);
        foreach (var (key, id) in winners)
        {
            var parts = key.Split('|', 3);
            if (parts.Length == 3 && IndexOf(ids, id) is >= 0 and var index)
            {
                picks[(parts[0], parts[1], parts[2])] = index;
            }
        }

        return picks;
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static (string, string) RowKey(string file, string row) => (file.ToLowerInvariant(), row.ToLowerInvariant());

    /// <summary>"×4" for a multiplier.</summary>
    internal static string Times(double value) => "×" + value.ToString("0.##", CultureInfo.InvariantCulture);
}
