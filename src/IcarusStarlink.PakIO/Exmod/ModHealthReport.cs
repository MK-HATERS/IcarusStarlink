using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Exmod;

/// <param name="BuiltFor">Which game version the mod's rows come from (when the game's row history is known).</param>
public sealed record ModHealthReport(IReadOnlyList<ModIssue> Issues, int AuthorChangeCount, ModGameVersion? BuiltFor = null)
{
    public int SureCount => Issues.Count(i => i.IsSure);

    /// <summary>What Fix sure problems would change by itself (<see cref="ModIssue.IsAutomatic"/>): never tidying alone.</summary>
    public int SureProblemCount => Issues.Count(i => i.IsAutomatic);

    public int ReviewCount => Issues.Count(i => !i.IsSure);

    public bool HasIssues => Issues.Count > 0;

    /// <summary>Findings that matter: everything but tidying (values that already match the game, and old game rows nothing uses), which changes nothing in game.</summary>
    public int ProblemCount => Issues.Count(i => !i.IsTidyOnly);

    public bool HasProblems => ProblemCount > 0;

    public int Count(ModIssueKind kind) => Issues.Count(i => i.Kind == kind);
}
