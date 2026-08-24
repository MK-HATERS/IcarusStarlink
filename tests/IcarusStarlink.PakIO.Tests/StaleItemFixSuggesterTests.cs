using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class StaleItemFixSuggesterTests
{
    private static JsonObject Row(params (string Field, int Value)[] fields)
    {
        var row = new JsonObject();
        foreach (var (field, value) in fields)
        {
            row[field] = JsonValue.Create(value);
        }
        return row;
    }

    [Fact]
    public void Suggest_UnambiguousCloseMatchWithGoodFieldOverlap_CanAutoApply()
    {
        var baseTable = new JsonObject
        {
            ["Stone_Pickaxe_MK2"] = Row(("RequiredMillijoules", 100), ("Weight", 5)),
            ["Wooden_Club"] = Row(("Damage", 10)),
        };

        var suggestion = StaleItemFixSuggester.Suggest("Stone_Pickaxe_Mk2", ["RequiredMillijoules"], baseTable);

        Assert.NotNull(suggestion);
        Assert.Equal("Stone_Pickaxe_MK2", suggestion.SuggestedItemName);
        Assert.True(suggestion.CanAutoApply);
    }

    [Fact]
    public void Suggest_TwoEquidistantCandidates_IsAmbiguousAndCannotAutoApply()
    {
        // Models the real Icarus trap this guard exists for: two DIFFERENT real rows (e.g. Left/
        // Right building-piece variants) that both sit at the same short distance from a stale
        // target — a one-character difference from either candidate here stands in for "Left" vs
        // "Right" both being one word away from a target that's neither.
        var baseTable = new JsonObject
        {
            ["Item_Variant_A"] = Row(("BuildingTier", 1)),
            ["Item_Variant_B"] = Row(("BuildingTier", 1)),
        };

        var suggestion = StaleItemFixSuggester.Suggest("Item_Variant_C", ["BuildingTier"], baseTable);

        Assert.NotNull(suggestion);
        Assert.False(suggestion.CanAutoApply);
    }

    [Fact]
    public void Suggest_CloseNameButPoorFieldOverlap_CannotAutoApply()
    {
        var baseTable = new JsonObject
        {
            // Close enough by name, but the mod's own fields don't exist on this candidate at all —
            // a real sign it's the wrong row despite the name similarity.
            ["Stone_Pickaxe_Mk2"] = Row(("Unrelated1", 1), ("Unrelated2", 2), ("Unrelated3", 3)),
        };

        var suggestion = StaleItemFixSuggester.Suggest(
            "Stone_Pickaxe_MK2X", ["RequiredMillijoules", "Weight", "Durability"], baseTable);

        Assert.NotNull(suggestion);
        Assert.False(suggestion.CanAutoApply);
    }

    [Fact]
    public void Suggest_NoCandidateWithinDistance_ReturnsNull()
    {
        var baseTable = new JsonObject
        {
            ["Totally_Unrelated_Item"] = Row(("Field", 1)),
        };

        var suggestion = StaleItemFixSuggester.Suggest("Stone_Pickaxe", ["RequiredMillijoules"], baseTable);

        Assert.Null(suggestion);
    }

    [Fact]
    public void Suggest_EmptyBaseTable_ReturnsNull()
    {
        var suggestion = StaleItemFixSuggester.Suggest("Anything", ["Field"], new JsonObject());

        Assert.Null(suggestion);
    }
}
