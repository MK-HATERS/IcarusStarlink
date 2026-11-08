using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

/// <summary>Explain: per value, which mods change it and what the merge keeps (the Editor shows this).</summary>
public class MergeEngineExplainTests
{
    private const string File = "Traits-D_Itemable.json";

    private static FieldChange Change(string item, string field, int value) =>
        new(File, item, field, OriginalValue: null, JsonValue.Create(value), ValueSemantic.Scalar);

    private static Dictionary<string, JsonObject> BaseTables() => new(StringComparer.OrdinalIgnoreCase)
    {
        [File] = JsonNode.Parse("""{"Item_Suit": {"Weight": 100, "MaxStack": 1}}""")!.AsObject(),
    };

    [Fact]
    public void Explain_NamesEveryModChangingAValue_AndWhatTheMergeKeeps()
    {
        List<FieldChange> modA = [Change("Item_Suit", "Weight", 50)];
        List<FieldChange> modB = [Change("Item_Suit", "Weight", 200), Change("Item_Suit", "MaxStack", 1)];

        var outcomes = MergeEngine.Explain([modA, modB], new MergeRuleRegistry(), baseTablesByFile: BaseTables());

        // MaxStack 1 is the game's own value: like the merge, it's no change at all.
        var weight = Assert.Single(outcomes);
        Assert.Equal("Weight", weight.FieldName);
        Assert.Equal([0, 1], weight.ModIndexes);
        Assert.Equal(200, weight.Resolved.NewValue!.GetValue<int>());
        Assert.Equal(1, weight.WinnerModIndex);
        Assert.Null(weight.PickedIndex);
    }

    [Fact]
    public void Explain_UsesASavedWinner_OnlyWhereTheValueIsInConflict()
    {
        List<FieldChange> modA = [Change("Item_Suit", "Weight", 50)];
        List<FieldChange> modB = [Change("Item_Suit", "Weight", 200)];
        var key = (File, "Item_Suit", "Weight");
        var winners = new Dictionary<(string, string, string), int>(FieldChangeKeyComparer.Instance) { [key] = 0 };

        var picked = Assert.Single(MergeEngine.Explain([modA, modB], new MergeRuleRegistry(), winners, BaseTables()));

        Assert.Equal(50, picked.Resolved.NewValue!.GetValue<int>());
        Assert.Equal(0, picked.WinnerModIndex);
        Assert.Equal(0, picked.PickedIndex);
        // The build agrees: the same pick by index.
        var merged = Assert.Single(MergeEngine.Merge([modA, modB], new MergeRuleRegistry(), new Dictionary<(string, string, string), int> { [key] = 0 }, BaseTables()));
        Assert.Equal(50, merged.NewValue!.GetValue<int>());

        // A winner for a mod not changing the value is no pick.
        var alone = Assert.Single(MergeEngine.Explain([[], modB], new MergeRuleRegistry(), winners, BaseTables()));
        Assert.Null(alone.PickedIndex);
        Assert.Equal(1, alone.WinnerModIndex);
    }
}
