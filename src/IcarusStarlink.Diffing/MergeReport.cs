namespace IcarusStarlink.Diffing;

public sealed class MergeReport
{
    private readonly List<string> _warnings = [];
    private readonly List<string> _notes = [];

    /// <summary>Real problems: something the merge could not do, so the output is missing content the mod intended.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Things worth telling the user that aren't failures — most importantly "this mod creates an
    /// item the game's current data doesn't have". That's completely normal for a mod that adds
    /// content, and also the only visible symptom of a mod that's gone stale against a newer game
    /// version, so it can't be silent AND can't be a warning.
    /// </summary>
    public IReadOnlyList<string> Notes => _notes;

    public void AddWarning(string message) => _warnings.Add(message);

    public void AddNote(string message) => _notes.Add(message);
}
