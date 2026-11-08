namespace IcarusStarlink.Diffing;

/// <summary>
/// One value as the merge sees it (MergeEngine.Explain): the mods changing it, in queue order
/// (ModIndexes[i] made Changes[i]), what the merge keeps, and whose value that is.
/// </summary>
/// <param name="WinnerModIndex">The mod whose value is kept; null when the rules combined several (a list taking everyone's entries).</param>
/// <param name="PickedIndex">Set when a saved winner decided it: its position in Changes.</param>
public sealed record FieldOutcome(
    string CurrentFile,
    string ItemName,
    string FieldName,
    IReadOnlyList<int> ModIndexes,
    IReadOnlyList<FieldChange> Changes,
    FieldChange Resolved,
    int? WinnerModIndex,
    int? PickedIndex);
