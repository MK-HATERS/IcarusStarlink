namespace IcarusStarlink.App.ViewModels;

/// <summary>A suspect on the first page: why, and whether the health check already knows a problem in it.</summary>
public sealed class FindBrokenModSuspectRow(string folderName, string name, string reason, int problemCount)
{
    public string FolderName { get; } = folderName;

    public string Name { get; } = name;

    public string Reason { get; } = reason;

    public bool HasProblems => problemCount > 0;

    public string ProblemText => problemCount == 1
        ? "The health check found 1 problem in it. Fixing it may be all it needs."
        : $"The health check found {problemCount} problems in it. Fixing them may be all it needs.";
}
