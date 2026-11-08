using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// What Merge &amp; Install would merge right now, as its own conflict check reads it: every part of
/// the merge in merge order (Ids[i] / Names[i] made OrderedChanges[i], index 0 = lowest priority),
/// the game values the merge compares against, the saved winners, and the multipliers it applies
/// after the mods.
/// </summary>
/// <param name="Ids">A Library mod's folder name; other ids for a pak, ArcFission's rows or the built-in options.</param>
/// <param name="Winners">Saved winners: "file|row|field" (MergeConflictsViewModel.WinnerKey) → the winner's id.</param>
public sealed record MergeQueueState(
    IReadOnlyList<string> Ids,
    IReadOnlyList<string> Names,
    IReadOnlyList<IReadOnlyList<FieldChange>> OrderedChanges,
    IReadOnlyDictionary<string, JsonObject>? BaseTables,
    IReadOnlyDictionary<string, string> Winners,
    double? StacksMultiplier = null,
    double? SlotsMultiplier = null)
{
    public static MergeQueueState Empty { get; } = new([], [], [], null, new Dictionary<string, string>());
}

/// <summary>Merge &amp; Install's queue as the Editor page reads it (Merge &amp; Install itself in the app; a fake in tests).</summary>
public interface IMergeQueueReader
{
    /// <summary>Reads the queue the way the merge does (on the UI thread; the reading runs in the background).</summary>
    Task<MergeQueueState> ReadQueueAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves this Library mod to the end of the merge list, where it wins; false when it isn't in the list or a build runs.</summary>
    bool MoveToEnd(string folderName);
}
