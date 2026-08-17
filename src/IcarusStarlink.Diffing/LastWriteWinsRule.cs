namespace IcarusStarlink.Diffing;

/// <summary>Default fallback: whichever mod is latest in the merge queue wins the field.</summary>
public sealed class LastWriteWinsRule : IFieldMergeRule
{
    public bool Applies(FieldChangeGroup group) => true;

    public FieldChange Resolve(FieldChangeGroup group) => group.OrderedChanges[^1];
}
