using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.App.Tests.ViewModels;

public class ConflictRowViewModelTests
{
    // The real default rule set (GameplayTagQueryCombineRule, ArrayUnionCombineRule,
    // LastWriteWinsRule) — every conflict built by this file's own helpers below is a plain Scalar
    // field (or, for the two GenericCompound cases, one that doesn't qualify as a pure addition over
    // base / has a removed candidate), so LastWriteWinsRule is what actually applies throughout;
    // this is only here because the constructor now requires a registry to ask.
    private static readonly MergeRuleRegistry Registry = new();

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
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20), existingPickIndex: null, Registry);

        Assert.Equal(0, row.SelectedOptionIndex);
        Assert.Null(row.PickedCandidateIndex);
    }

    [Fact]
    public void Constructor_ExistingPickIndex_OffsetsByOneForTheDefaultOption()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20), existingPickIndex: 1, Registry);

        Assert.Equal(2, row.SelectedOptionIndex); // Options[0] is "Default", Options[2] is Candidates[1]
        Assert.Equal(1, row.PickedCandidateIndex);
    }

    [Fact]
    public void Constructor_DisplayNamesTheFileItemAndField()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20), null, Registry);

        Assert.Contains("Items/D_ItemsStatic.json", row.Display); // EXMOD dash convention converted back to a real path
        Assert.Contains("Sword.Damage", row.Display);
    }

    [Fact]
    public void Constructor_OptionsListsDefaultThenEachCandidate()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20), null, Registry);

        Assert.Equal(3, row.Options.Count);
        Assert.Contains("Default (last mod wins: Mod B)", row.Options[0]);
        Assert.Contains("Mod A", row.Options[1]);
        Assert.Contains("Mod B", row.Options[2]);
    }

    [Fact]
    public void Constructor_NoBaseValue_BaseValueDisplayIsNull()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20, hasBaseValue: false), null, Registry);

        Assert.Null(row.BaseValueDisplay);
    }

    [Fact]
    public void Constructor_BaseValueGiven_BaseValueDisplayShowsIt()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20, hasBaseValue: true, baseValue: 5), null, Registry);

        Assert.NotNull(row.BaseValueDisplay);
        Assert.Contains("5", row.BaseValueDisplay);
    }

    [Fact]
    public void Constructor_CandidateAboveBase_OptionCarriesAnUpHint()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(20, 20, hasBaseValue: true, baseValue: 10), null, Registry);

        Assert.Contains("▲", row.Options[1]);
    }

    [Fact]
    public void Constructor_CandidateBelowBase_OptionCarriesADownHint()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(5, 20, hasBaseValue: true, baseValue: 10), null, Registry);

        Assert.Contains("▼", row.Options[1]);
    }

    [Fact]
    public void Constructor_CandidateEqualsBase_OptionCarriesANoChangeHint()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20, hasBaseValue: true, baseValue: 10), null, Registry);

        Assert.Contains("= same as base", row.Options[1]);
    }

    [Fact]
    public void Constructor_NoBaseValueKnown_NoDirectionalHintOnAnyOption()
    {
        var row = new ConflictRowViewModel(TwoCandidateConflict(10, 20, hasBaseValue: false), null, Registry);

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

        var row = new ConflictRowViewModel(conflict, null, Registry);

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

        var row = new ConflictRowViewModel(conflict, null, Registry);

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

        var row = new ConflictRowViewModel(conflict, null, Registry);

        Assert.Contains("(removed)", row.Options[1]);
        Assert.DoesNotContain("▲", row.Options[1]);
        Assert.DoesNotContain("▼", row.Options[1]);
    }

    /// <summary>
    /// Regression guard: two mods each cleanly adding a different entry to the same brand-new
    /// array field used to get the SAME "Default (last mod wins: X)" label a genuine last-write-wins
    /// field gets — misleading, since ArrayUnionCombineRule actually unions both additions here, not
    /// "the last mod wins." A user who picks one mod's raw value "to be safe" would silently lose
    /// the OTHER mod's own addition that Default would have kept.
    /// </summary>
    [Fact]
    public void Constructor_FieldArrayUnionRuleWouldApply_DefaultLabelDoesNotClaimLastModWins()
    {
        // No base value at all (a brand-new item) — ArrayUnionCombineRule's own "most common real
        // case" per its doc comment: each mod's array is trivially a pure addition over an empty
        // base, so it unions rather than deferring to LastWriteWinsRule.
        var conflict = new FieldConflict(
            "Crafting-D_ProcessorRecipes.json", "New_Recipe", "Inputs",
            [
                new ConflictCandidate("Mod A", new FieldChange("Crafting-D_ProcessorRecipes.json", "New_Recipe", "Inputs", null, JsonNode.Parse("""[{"Element":"X"}]"""), ValueSemantic.GenericCompound)),
                new ConflictCandidate("Mod B", new FieldChange("Crafting-D_ProcessorRecipes.json", "New_Recipe", "Inputs", null, JsonNode.Parse("""[{"Element":"Y"}]"""), ValueSemantic.GenericCompound)),
            ],
            HasBaseValue: false);

        var row = new ConflictRowViewModel(conflict, null, Registry);

        Assert.DoesNotContain("last mod wins", row.Options[0]);
        Assert.Contains("combines every mod's own additions", row.Options[0]);
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
