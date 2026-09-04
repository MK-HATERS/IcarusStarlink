using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

/// <summary>
/// Exercised through MergeEngine.Merge/FindConflicts (the registry's own public surface), matching
/// how GameplayTagQueryCombineRule itself is tested — no queued mod ever calls an IFieldMergeRule
/// directly.
/// </summary>
public class ArrayUnionCombineRuleTests
{
    private static FieldChange CompoundChange(string item, string field, JsonNode? value, bool isNewItem = false) =>
        new("Crafting-D_ProcessorRecipes.json", item, field, OriginalValue: null, value, ValueSemantic.GenericCompound, isNewItem);

    private static IReadOnlyDictionary<string, JsonObject> BaseTableWithArray(string item, string field, string jsonArray) =>
        new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new JsonObject { [item] = new JsonObject { [field] = JsonNode.Parse(jsonArray) } },
        };

    [Fact]
    public void Merge_TwoModsEachPureAdditionOverBase_UnionsBothAdditions()
    {
        var baseTables = BaseTableWithArray("Stone_Pickaxe", "Inputs", """["Fiber"]""");
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber","Wood"]""")) };
        var modB = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber","Stone"]""")) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry(), baseTablesByFile: baseTables);

        var change = Assert.Single(resolved);
        var entries = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Fiber", "Wood", "Stone"], entries);
    }

    [Fact]
    public void Merge_NoBaseValueAtAll_TreatsBaseAsEmptyAndStillUnions()
    {
        // The most common real case: two mods each adding different entries to a brand-new item's
        // own array field, so there's no base array to compare against at all.
        var modA = new List<FieldChange> { CompoundChange("Brand_New_Recipe", "Inputs", JsonNode.Parse("""["Fiber"]"""), isNewItem: true) };
        var modB = new List<FieldChange> { CompoundChange("Brand_New_Recipe", "Inputs", JsonNode.Parse("""["Wood"]"""), isNewItem: true) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        var entries = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Fiber", "Wood"], entries);
        Assert.True(change.IsNewItem);
    }

    [Fact]
    public void Merge_DuplicateAdditionAcrossMods_NotRepeatedInResult()
    {
        var baseTables = BaseTableWithArray("Stone_Pickaxe", "Inputs", """["Fiber"]""");
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber","Wood"]""")) };
        var modB = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber","Wood"]""")) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry(), baseTablesByFile: baseTables);

        var change = Assert.Single(resolved);
        var entries = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Fiber", "Wood"], entries);
    }

    [Fact]
    public void Merge_OneModRemovesABaseEntry_FallsThroughToLastWriteWinsInsteadOfSilentlyUnioning()
    {
        // ModA purely adds; ModB drops "Wood" that was in base — not cleanly "each side purely
        // adds", so this must NOT auto-union (that would silently resurrect an entry ModB
        // deliberately removed). It falls back to plain last-write-wins instead.
        var baseTables = BaseTableWithArray("Stone_Pickaxe", "Inputs", """["Fiber","Wood"]""");
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber","Wood","Stone"]""")) };
        var modB = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber"]""")) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry(), baseTablesByFile: baseTables);

        var change = Assert.Single(resolved);
        var entries = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Fiber"], entries); // ModB (last in queue) wins outright, nothing unioned
    }

    [Fact]
    public void FindConflicts_OneModRemovesABaseEntry_StillSurfacesAsARealConflict()
    {
        // The other half of the test above: since Applies() declined, this field must still be a
        // normal, visible conflict for the manual picker — not silently resolved either way.
        var baseTables = BaseTableWithArray("Stone_Pickaxe", "Inputs", """["Fiber","Wood"]""");
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber","Wood","Stone"]""")) };
        var modB = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber"]""")) };

        var conflicts = MergeEngine.FindConflicts(["Mod A", "Mod B"], [modA, modB], baseTables);

        Assert.Single(conflicts);
    }

    [Fact]
    public void Merge_OneModEditsAnExistingBaseEntry_FallsThroughToLastWriteWins()
    {
        // ModB's own array is the same LENGTH as base but the entry itself was changed
        // ({"Count":1} -> {"Count":99}) — a deliberate edit, not an addition, so this must not be
        // auto-combined either.
        var baseTables = BaseTableWithArray("Stone_Pickaxe", "Inputs", """[{"Item":"Fiber","Count":1}]""");
        var modA = new List<FieldChange>
        {
            CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""[{"Item":"Fiber","Count":1},{"Item":"Wood","Count":2}]""")),
        };
        var modB = new List<FieldChange>
        {
            CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""[{"Item":"Fiber","Count":99}]""")),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry(), baseTablesByFile: baseTables);

        var change = Assert.Single(resolved);
        var row = change.NewValue!.AsArray().Single()!;
        Assert.Equal(99, (int)row["Count"]!); // ModB (last in queue) wins outright
    }

    [Fact]
    public void Merge_SingleModTouchesArrayField_PassesThroughUnwrapped()
    {
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber"]""")) };

        var resolved = MergeEngine.Merge([modA], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        var entries = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Fiber"], entries);
    }

    [Fact]
    public void Merge_GenericCompoundNonArrayObjectField_IsNotCombined()
    {
        // Guards against accidentally broadening this rule beyond array-shaped values — a plain
        // compound OBJECT field (not an array) has no "entries" to union, so it must still resolve
        // via plain last-write-wins like it did before this rule existed.
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Requirements", JsonNode.Parse("""{"Level":1}""")) };
        var modB = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Requirements", JsonNode.Parse("""{"Level":2}""")) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal(2, (int)change.NewValue!["Level"]!);
    }

    [Fact]
    public void Merge_MixedSemanticsAcrossMods_DoesNotMisfireCombine()
    {
        // One mod's own value classifies as GenericCompound (an array); the other's classifies as
        // RowReference (a single {"RowName": ...} struct, not an array) — this rule must not fire
        // just because both are "in scope" semantics; the non-array shape rules it out.
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Outputs", JsonNode.Parse("""["Fiber"]""")) };
        var modB = new FieldChange(
            "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe", "Outputs",
            OriginalValue: null, JsonNode.Parse("""{"RowName":"X"}"""), ValueSemantic.RowReference);

        var resolved = MergeEngine.Merge([modA, [modB]], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Equal("X", change.NewValue!["RowName"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_ArrayShapedRowReferenceSemantic_StillCombines()
    {
        // DefaultSemanticClassifier never actually produces an array-shaped RowReference change
        // today, but this rule is written to accept it (Applies checks GenericCompound OR
        // RowReference) for a caller/future classifier that does — proven here by constructing one
        // directly rather than going through the classifier.
        var modA = new FieldChange(
            "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe", "Outputs",
            OriginalValue: null, JsonNode.Parse("""["Fiber"]"""), ValueSemantic.RowReference);
        var modB = new FieldChange(
            "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe", "Outputs",
            OriginalValue: null, JsonNode.Parse("""["Wood"]"""), ValueSemantic.RowReference);

        var resolved = MergeEngine.Merge([[modA], [modB]], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        var entries = change.NewValue!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["Fiber", "Wood"], entries);
    }

    [Fact]
    public void Merge_OneModRemovesTheField_FallsThroughToLastWriteWinsInsteadOfCorruptingTheUnion()
    {
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber"]""")) };
        var modB = new List<FieldChange>
        {
            new("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe", "Inputs",
                OriginalValue: JsonNode.Parse("""["Fiber"]"""), NewValue: null, ValueSemantic.GenericCompound, IsFieldRemoved: true),
        };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public void Merge_IsNewItemIsOredAcrossCandidatesEvenWhenTheUnionRuleResolvesTheField()
    {
        var modA = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber"]"""), isNewItem: true) };
        var modB = new List<FieldChange> { CompoundChange("Stone_Pickaxe", "Inputs", JsonNode.Parse("""["Fiber","Wood"]"""), isNewItem: false) };

        var resolved = MergeEngine.Merge([modA, modB], new MergeRuleRegistry());

        var change = Assert.Single(resolved);
        Assert.True(change.IsNewItem);
    }
}
