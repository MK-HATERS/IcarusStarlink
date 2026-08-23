using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

/// <summary>
/// Regression tests for a real defect found against the user's own mod library: one .EXMOD can
/// list the same item twice in its own File_Items (laanp-ExtraDeployables lists Prop_PaperTowels
/// with two different recipes), which used to surface as dozens of "conflicts" of a mod against
/// itself in the picker.
/// </summary>
public sealed class MergeEngineDuplicateItemTests
{
    private static FieldChange Change(string item, string field, int value) =>
        new("Crafting-D_ProcessorRecipes.json", item, field, null, JsonValue.Create(value), ValueSemantic.Scalar);

    [Fact]
    public void FindConflicts_SameModListingOneItemTwice_IsNotAConflict()
    {
        var oneModWithDuplicates = new List<IReadOnlyList<FieldChange>>
        {
            new FieldChange[] { Change("Prop_PaperTowels", "Count", 1), Change("Prop_PaperTowels", "Count", 2) },
        };

        var conflicts = MergeEngine.FindConflicts(["ExtraDeployables"], oneModWithDuplicates);

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Merge_SameModListingOneItemTwice_KeepsItsLastValue()
    {
        // Matches what actually lands in the merged output: TableApplier assigns [item][field], so
        // the mod's own later entry is the one that survives regardless.
        var oneModWithDuplicates = new List<IReadOnlyList<FieldChange>>
        {
            new FieldChange[] { Change("Prop_PaperTowels", "Count", 1), Change("Prop_PaperTowels", "Count", 2) },
        };

        var merged = MergeEngine.Merge(oneModWithDuplicates, new MergeRuleRegistry());

        var change = Assert.Single(merged);
        Assert.Equal("2", change.NewValue!.ToJsonString());
    }

    [Fact]
    public void FindConflicts_TwoDifferentModsStillConflict()
    {
        var twoMods = new List<IReadOnlyList<FieldChange>>
        {
            new FieldChange[] { Change("Prop_PaperTowels", "Count", 1) },
            new FieldChange[] { Change("Prop_PaperTowels", "Count", 5) },
        };

        var conflicts = MergeEngine.FindConflicts(["ModA", "ModB"], twoMods);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(2, conflict.Candidates.Count);
        Assert.Equal("ModA", conflict.Candidates[0].ModName);
        Assert.Equal("ModB", conflict.Candidates[1].ModName);
    }

    [Fact]
    public void FindConflicts_ModWithDuplicatesVersusAnotherMod_OffersOneCandidatePerMod()
    {
        // The duplicate-listing mod contributes exactly one candidate (its last value), so the
        // picker offers a real either/or rather than three entries, two of them the same mod.
        var mods = new List<IReadOnlyList<FieldChange>>
        {
            new FieldChange[] { Change("Prop_PaperTowels", "Count", 1), Change("Prop_PaperTowels", "Count", 2) },
            new FieldChange[] { Change("Prop_PaperTowels", "Count", 9) },
        };

        var conflict = Assert.Single(MergeEngine.FindConflicts(["ExtraDeployables", "OtherMod"], mods));

        Assert.Equal(2, conflict.Candidates.Count);
        Assert.Equal("ExtraDeployables", conflict.Candidates[0].ModName);
        Assert.Equal("2", conflict.Candidates[0].Change.NewValue!.ToJsonString());
        Assert.Equal("OtherMod", conflict.Candidates[1].ModName);
    }

    [Fact]
    public void ManualPickIndex_StillLinesUpWithFindConflictsCandidates()
    {
        // The guarantee that makes the picker usable at all: Merge and FindConflicts must agree on
        // what candidate index N means, including when a mod listed the item twice.
        var mods = new List<IReadOnlyList<FieldChange>>
        {
            new FieldChange[] { Change("Prop_PaperTowels", "Count", 1), Change("Prop_PaperTowels", "Count", 2) },
            new FieldChange[] { Change("Prop_PaperTowels", "Count", 9) },
        };

        var conflict = Assert.Single(MergeEngine.FindConflicts(["ExtraDeployables", "OtherMod"], mods));
        var pickFirstMod = new Dictionary<(string, string, string), int>
        {
            [(conflict.CurrentFile, conflict.ItemName, conflict.FieldName)] = 0,
        };

        var merged = MergeEngine.Merge(mods, new MergeRuleRegistry(), pickFirstMod);

        var change = Assert.Single(merged);
        Assert.Equal(conflict.Candidates[0].Change.NewValue!.ToJsonString(), change.NewValue!.ToJsonString());
    }
}
