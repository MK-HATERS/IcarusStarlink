using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The fix window's side-by-side comparison: lists entry by entry, other values line by line.</summary>
public sealed class ValueDiffTests
{
    [Fact]
    public void ALinkList_LinesUpByEntry_TheGamesOrderFirst_ThenWhatOnlyTheModHas()
    {
        var mod = Links("Carpentry_Bench", "Fabricator");
        var game = Links("Crafting_Bench", "Fabricator");
        var merged = Links("Crafting_Bench", "Fabricator", "Carpentry_Bench");

        var rows = ValueDiff.Build(mod, game, merged);

        Assert.True(ValueDiff.IsList(mod, game));
        Assert.Equal(
            [(null, "Crafting_Bench", "Crafting_Bench", DiffSide.OnlyGame), ("Fabricator", "Fabricator", "Fabricator", DiffSide.Same), ("Carpentry_Bench", null, "Carpentry_Bench", DiffSide.OnlyMod)],
            rows.Select(r => (r.Mod, r.Game, r.After, r.Side)));
    }

    [Fact]
    public void RecipeItems_ReadAsNameAndCount()
    {
        var mod = new JsonArray(new JsonObject { ["Element"] = new JsonObject { ["RowName"] = "Wood" }, ["Count"] = 3 });

        Assert.Equal(["Wood ×3"], ValueDiff.Entries(mod));
    }

    [Fact]
    public void OtherValues_AlignOnTheLinesBothShare_TheRestMarkedChanged()
    {
        var mod = new JsonObject { ["A"] = 1, ["B"] = 2, ["C"] = 3 };
        var game = new JsonObject { ["A"] = 1, ["B"] = 5, ["C"] = 3 };

        var rows = ValueDiff.Build(mod, game, game);

        var changed = Assert.Single(rows, r => r.Side == DiffSide.Changed);
        Assert.Contains("2", changed.Mod);
        Assert.Contains("5", changed.Game);
        Assert.Equal(changed.Game, changed.After);
        Assert.All(rows.Where(r => r.Side == DiffSide.Same), r => Assert.Equal(r.Mod, r.Game));
    }

    [Fact]
    public void ADeletedField_ShowsAsNotSet_AgainstTheGamesValue()
    {
        var rows = ValueDiff.Build(null, JsonValue.Create(20000), JsonValue.Create(20000));

        var row = Assert.Single(rows);
        Assert.StartsWith("(not set", row.Mod);
        Assert.Equal("20000", row.Game);
    }

    private static JsonArray Links(params string[] rows) =>
        new([.. rows.Select(r => (JsonNode)new JsonObject { ["RowName"] = r, ["DataTableName"] = "D_RecipeSets" })]);
}
