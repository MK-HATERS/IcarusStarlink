using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

public class MergeEngineTests
{
    private static FieldChange ScalarChange(string item, string field, int value) =>
        new("Items-D_ItemsStatic.json", item, field, OriginalValue: null, JsonValue.Create(value), ValueSemantic.Scalar);

    [Fact]
    public void Merge_TwoModsSameField_LastInQueueWins()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 30) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal(30, change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void Merge_ModsTouchingDifferentFields_KeepsBoth()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Weight", 1) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void Merge_ThreeWayConflict_ManualPickOverridesRegistryForThatField()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };
        var modC = new List<FieldChange> { ScalarChange("Sword", "Damage", 30) };

        var key = ("Items-D_ItemsStatic.json", "Sword", "Damage");
        var manualPicks = new Dictionary<(string, string, string), int> { [key] = 1 }; // pick modB, not the last

        var resolved = MergeEngine.Merge([modA, modB, modC], new MergeRuleRegistry(), manualPicks);

        var change = Assert.Single(resolved);
        Assert.Equal(20, change.NewValue!.GetValue<int>());
    }

    [Fact]
    public void Merge_GameplayTagQueryFields_CombineInsteadOfOverwriting()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery),
        };
        var modB = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Resources"]"""), ValueSemantic.GameplayTagQuery),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        var tags = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Tools", "Resources"], tags);
    }

    [Fact]
    public void Merge_GameplayTagQuery_OneModRemovesField_DefersToLastWriteWinsInsteadOfCorruptingArray()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery),
        };
        var modB = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: JsonNode.Parse("""["Tools"]"""), NewValue: null, ValueSemantic.GameplayTagQuery,
                IsFieldRemoved: true),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public void Merge_GameplayTagQuery_SingleModTouchesField_PassesThroughUnwrapped()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonValue.Create("Tools"), ValueSemantic.GameplayTagQuery),
        };

        var resolved = MergeEngine.Merge([modA], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal("Tools", change.NewValue!.GetValue<string>());
    }

    [Fact]
    public void Merge_GameplayTagQuery_MixedSemanticsAcrossMods_DoesNotMisfireCombine()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery),
        };
        var modB = new List<FieldChange>
        {
            // A different mod's value for the same field name happened to classify differently
            // (e.g. structurally shaped like a row reference) — the combine rule must not fire.
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""{"RowName": "X"}"""), ValueSemantic.RowReference),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal("X", change.NewValue!["RowName"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_LastWriteWins_IsNewItemIsOredAcrossCandidates_EvenWhenWinnerSaysOtherwise()
    {
        // ModA was diffed while the row didn't exist yet (IsNewItem=true); ModB was diffed later,
        // after some other change added the row (IsNewItem=false) and wins last-write-wins. The
        // resolved change must still say IsNewItem=true so TableApplier creates the row instead
        // of skipping it if the row is in fact absent from whatever base gets applied to.
        var modA = new FieldChange(
            "Items-D_ItemsStatic.json", "SpecialItem", "Damage",
            OriginalValue: null, JsonValue.Create(10), ValueSemantic.Scalar, IsNewItem: true);
        var modB = new FieldChange(
            "Items-D_ItemsStatic.json", "SpecialItem", "Damage",
            OriginalValue: JsonValue.Create(10), JsonValue.Create(20), ValueSemantic.Scalar, IsNewItem: false);

        var resolved = MergeEngine.Merge([[modA], [modB]], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal(20, change.NewValue!.GetValue<int>()); // modB's value won
        Assert.True(change.IsNewItem); // but new-item status is still honored
    }

    [Fact]
    public void Merge_GameplayTagQueryCombine_IsNewItemIsOredAcrossCandidates()
    {
        var modA = new FieldChange(
            "Deployables-D_DeployableSetup.json", "SpecialBench", "UnlockTagQuery",
            OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery, IsNewItem: true);
        var modB = new FieldChange(
            "Deployables-D_DeployableSetup.json", "SpecialBench", "UnlockTagQuery",
            OriginalValue: null, JsonNode.Parse("""["Resources"]"""), ValueSemantic.GameplayTagQuery, IsNewItem: false);

        var resolved = MergeEngine.Merge([[modA], [modB]], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.True(change.IsNewItem);
    }

    [Fact]
    public void Merge_ManualPickIndexOutOfRange_ThrowsClearException()
    {
        var modA = new List<FieldChange> { ScalarChange("Sword", "Damage", 10) };
        var modB = new List<FieldChange> { ScalarChange("Sword", "Damage", 20) };

        var key = ("Items-D_ItemsStatic.json", "Sword", "Damage");
        var manualPicks = new Dictionary<(string, string, string), int> { [key] = 5 }; // stale pick

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => MergeEngine.Merge([modA, modB], new MergeRuleRegistry(), manualPicks));
        Assert.Contains("Sword", ex.Message);
        Assert.Contains("Damage", ex.Message);
    }

    [Fact]
    public void Merge_GameplayTagQuery_DuplicateEntriesAreNotRepeated()
    {
        var modA = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools"]"""), ValueSemantic.GameplayTagQuery),
        };
        var modB = new List<FieldChange>
        {
            new("Deployables-D_DeployableSetup.json", "TakeHomeBench", "UnlockTagQuery",
                OriginalValue: null, JsonNode.Parse("""["Tools", "Resources"]"""), ValueSemantic.GameplayTagQuery),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        var tags = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Tools", "Resources"], tags);
    }
}
