namespace IcarusStarlink.Diffing;

public interface IFieldMergeRule
{
    bool Applies(FieldChangeGroup group);

    FieldChange Resolve(FieldChangeGroup group);
}
