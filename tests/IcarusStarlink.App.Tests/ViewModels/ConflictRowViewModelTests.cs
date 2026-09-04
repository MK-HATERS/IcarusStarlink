using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.App.Tests.ViewModels;

public class ConflictRowViewModelTests
{
    private static FieldChange ScalarChange(int value) =>
        new("Items-D_ItemsStatic.json", "Sword", "Damage", OriginalValue: null, JsonValue.Create(value), ValueSemantic.Scalar);

    private static FieldConflict TwoCandidateConflict(int candidateAValue, int candidateBValue, bool hasBaseValue = false, int? baseValue = null) =>
        new(
            "Items-D_ItemsStatic.json", "Sword", "Damage",
            [new ConflictCandidate("Mod A", ScalarChange(candidateAValue)), new ConflictCandidate("Mod B", ScalarChange(candidateBValue))],
            hasBaseValue, baseValue.HasValue ? JsonValue.Create(baseValue.Value) : null);

    [Fact]
    public void Constructor_NoExistingPick_DefaultsToOptionZero()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20), existingPickIndex: null);

        Assert.Equal(0, row.SelectedOptionIndex);
        Assert.Null(row.PickedCandidateIndex);
    }

    [Fact]
    public void Constructor_ExistingPickIndex_OffsetsByOneForTheDefaultOption()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20), existingPickIndex: 1);

        Assert.Equal(2, row.SelectedOptionIndex); // Options[0] is "Default", Options[2] is Candidates[1]
        Assert.Equal(1, row.PickedCandidateIndex);
    }

    [Fact]
    public void Constructor_DisplayNamesTheFileItemAndField()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20), null);

        Assert.Contains("Items/D_ItemsStatic.json", row.Display); // EXMOD dash convention converted back to a real path
        Assert.Contains("Sword.Damage", row.Display);
    }

    [Fact]
    public void Constructor_OptionsListsDefaultThenEachCandidate()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20), null);

        Assert.Equal(3, row.Options.Count);
        Assert.Contains("Default (last mod wins: Mod B)", row.Options[0]);
        Assert.Contains("Mod A", row.Options[1]);
        Assert.Contains("Mod B", row.Options[2]);
    }

    [Fact]
    public void Constructor_NoBaseValue_BaseValueDisplayIsNull()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20, hasBaseValue: false), null);

        Assert.Null(row.BaseValueDisplay);
    }

    [Fact]
    public void Constructor_BaseValueGiven_BaseValueDisplayShowsIt()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20, hasBaseValue: true, baseValue: 5), null);

        Assert.NotNull(row.BaseValueDisplay);
        Assert.Contains("5", row.BaseValueDisplay);
    }

    [Fact]
    public void Constructor_CandidateAboveBase_OptionCarriesAnUpHint()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(20, 20, hasBaseValue: true, baseValue: 10), null);

        Assert.Contains("▲", row.Options[1]);
    }

    [Fact]
    public void Constructor_CandidateBelowBase_OptionCarriesADownHint()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(5, 20, hasBaseValue: true, baseValue: 10), null);

        Assert.Contains("▼", row.Options[1]);
    }

    [Fact]
    public void Constructor_CandidateEqualsBase_OptionCarriesANoChangeHint()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20, hasBaseValue: true, baseValue: 10), null);

        Assert.Contains("= same as base", row.Options[1]);
    }

    [Fact]
    public void Constructor_NoBaseValueKnown_NoDirectionalHintOnAnyOption()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20, hasBaseValue: false), null);

        Assert.DoesNotContain("▲", row.Options[1]);
        Assert.DoesNotContain("▼", row.Options[1]);
        Assert.DoesNotContain("same as base", row.Options[1]);
    }

    [Fact]
    public void Constructor_NonNumericScalarField_NoDirectionalHint()
    {
        // A string-valued Scalar field (DefaultSemanticClassifier buckets strings into Scalar too,
        // not just numbers) — there's no meaningful "above/below" to show.
        var conflict = new FieldConflict(
            "Items-D_ItemsStatic.json", "Sword", "DisplayName",
            [
                new ConflictCandidate("Mod A", new FieldChange("Items-D_ItemsStatic.json", "Sword", "DisplayName", null, JsonValue.Create("Sharp Sword"), ValueSemantic.Scalar)),
                new ConflictCandidate("Mod B", new FieldChange("Items-D_ItemsStatic.json", "Sword", "DisplayName", null, JsonValue.Create("Rusty Sword"), ValueSemantic.Scalar)),
            ],
            HasBaseValue: true, BaseValue: JsonValue.Create("Sword"));

        var row = new ConflictRowViewModel(conflict, null);

        Assert.DoesNotContain("▲", row.Options[1]);
        Assert.DoesNotContain("▼", row.Options[1]);
    }

    [Fact]
    public void Constructor_NonScalarSemantic_NoDirectionalHintEvenIfValuesLookNumeric()
    {
        var conflict = new FieldConflict(
            "Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe", "Inputs",
            [
                new ConflictCandidate("Mod A", new FieldChange("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe", "Inputs", null, JsonNode.Parse("[1,2]"), ValueSemantic.GenericCompound)),
                new ConflictCandidate("Mod B", new FieldChange("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe", "Inputs", null, JsonNode.Parse("[3]"), ValueSemantic.GenericCompound)),
            ],
            HasBaseValue: true, BaseValue: JsonNode.Parse("[1]"));

        var row = new ConflictRowViewModel(conflict, null);

        Assert.DoesNotContain("▲", row.Options[1]);
        Assert.DoesNotContain("▼", row.Options[1]);
    }

    [Fact]
    public void Constructor_FieldRemovedCandidate_ShowsRemovedNotAHint()
    {
        var conflict = new FieldConflict(
            "Items-D_ItemsStatic.json", "Sword", "Damage",
            [
                new ConflictCandidate("Mod A", new FieldChange("Items-D_ItemsStatic.json", "Sword", "Damage", JsonValue.Create(10), null, ValueSemantic.Scalar, IsFieldRemoved: true)),
                new ConflictCandidate("Mod B", ScalarChange(20)),
            ],
            HasBaseValue: true, BaseValue: JsonValue.Create(10));

        var row = new ConflictRowViewModel(conflict, null);

        Assert.Contains("(removed)", row.Options[1]);
        Assert.DoesNotContain("▲", row.Options[1]);
        Assert.DoesNotContain("▼", row.Options[1]);
    }

    [Fact]
    public void NewItemNameCollisionConstructor_MentionsTheItemNameAndBothMods()
    {
        var collision = new NewItemNameCollision("Items-D_ItemsStatic.json", "Wooden_Table", ["Mod A", "Mod B"]);

        var row = new ConflictRowViewModel(collision);

        Assert.Contains("Wooden_Table", row.Display);
        Assert.Contains("Mod A", row.Display);
        Assert.Contains("Mod B", row.Display);
        Assert.Null(row.Conflict);
        Assert.Null(row.BaseValueDisplay);
    }

    [Fact]
    public void NewItemNameCollisionConstructor_HasExactlyOnePermanentlySelectedOption()
    {
        var collision = new NewItemNameCollision("Items-D_ItemsStatic.json", "Wooden_Table", ["Mod A", "Mod B"]);

        var row = new ConflictRowViewModel(collision);

        Assert.Single(row.Options);
        Assert.Equal(0, row.SelectedOptionIndex);
        Assert.Null(row.PickedCandidateIndex);
    }
}
