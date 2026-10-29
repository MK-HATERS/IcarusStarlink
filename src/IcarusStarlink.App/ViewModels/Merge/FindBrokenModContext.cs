using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>What the finder window needs from the app (the launcher fills it; tests pass fakes).</summary>
public sealed class FindBrokenModContext
{
    public required IBrokenModFinderHost Host { get; init; }

    public required BrokenModSearchStore Store { get; init; }

    public required IDialogService Dialogs { get; init; }

    /// <summary>The queued data mods with their EXMODs read.</summary>
    public required Func<Task<IReadOnlyList<BrokenModCandidate>>> ReadCandidates { get; init; }

    /// <summary>The game tables (null without extracted game data: no suspects or links then).</summary>
    public Func<GameTableIndex?> GameIndex { get; init; } = () => null;

    /// <summary>Every game row, for the "what's broken" search.</summary>
    public Func<Task<IReadOnlyList<GameDataItemRef>>> GameRows { get; init; } = () => Task.FromResult<IReadOnlyList<GameDataItemRef>>([]);

    /// <summary>Queued mod (folder) → how many problems the health check finds in it.</summary>
    public Func<Task<IReadOnlyDictionary<string, int>>> ProblemCounts { get; init; } =
        () => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    /// <summary>Opens the Library's fix window for a mod; why it couldn't, or null.</summary>
    public Func<string, Task<string?>> OpenFixWindow { get; init; } = _ => Task.FromResult<string?>("The fix window isn't available here.");

    public Func<string, int?> NexusModIdOf { get; init; } = _ => null;

    public Action<string> OpenUrl { get; init; } = _ => { };
}
