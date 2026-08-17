namespace IcarusStarlink.Diffing;

/// <summary>OrderedChanges is ordered by merge-queue position: index 0 is lowest priority.</summary>
public sealed record FieldChangeGroup(
    string CurrentFile,
    string ItemName,
    string FieldName,
    IReadOnlyList<FieldChange> OrderedChanges);
