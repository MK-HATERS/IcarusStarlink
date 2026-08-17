namespace IcarusStarlink.Diffing;

public sealed class MergeReport
{
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;

    public void AddWarning(string message) => _warnings.Add(message);
}
