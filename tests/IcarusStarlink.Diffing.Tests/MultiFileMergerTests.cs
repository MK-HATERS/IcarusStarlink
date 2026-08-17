using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

public class MultiFileMergerTests
{
    private static readonly ISemanticClassifier Classifier = new DefaultSemanticClassifier();

    [Fact]
    public void Apply_TwoModsAcrossTwoFiles_ProducesMergedTablesForEachFile()
    {
        var baseItems = JsonNode.Parse("""{"Sword": {"Damage": 10}}""")!.AsObject();
        var baseRecipes = JsonNode.Parse("""{"SwordRecipe": {"CraftTime": 30}}""")!.AsObject();

        // Mod A: buffs the sword and adds a brand new recipe.
        var moddedItemsA = JsonNode.Parse("""{"Sword": {"Damage": 25}}""")!.AsObject();
        var moddedRecipesA = JsonNode.Parse("""
            {"SwordRecipe": {"CraftTime": 30}, "LaserSwordRecipe": {"CraftTime": 5}}
            """)!.AsObject();
        var modAChanges = TableDiffer.Diff(baseItems, moddedItemsA, "Items-D_ItemsStatic.json", Classifier)
            .Concat(TableDiffer.Diff(baseRecipes, moddedRecipesA, "Crafting-D_ProcessorRecipes.json", Classifier))
            .ToList();

        // Mod B: speeds up crafting on the existing recipe only.
        var moddedRecipesB = JsonNode.Parse("""{"SwordRecipe": {"CraftTime": 1}}""")!.AsObject();
        var modBChanges = TableDiffer.Diff(baseRecipes, moddedRecipesB, "Crafting-D_ProcessorRecipes.json", Classifier)
            .ToList();

        var resolved = MergeEngine.Merge([modAChanges, modBChanges], new MergeRuleRegistry());

        var baseTablesByFile = new Dictionary<string, JsonObject>
        {
            ["Items-D_ItemsStatic.json"] = baseItems,
            ["Crafting-D_ProcessorRecipes.json"] = baseRecipes,
        };

        var report = new MergeReport();
        var mergedFiles = MultiFileMerger.Apply(baseTablesByFile, resolved, report);

        Assert.Empty(report.Warnings);
        Assert.Equal(2, mergedFiles.Count);
        Assert.Equal(25, mergedFiles["Items-D_ItemsStatic.json"]["Sword"]!["Damage"]!.GetValue<int>());
        Assert.Equal(1, mergedFiles["Crafting-D_ProcessorRecipes.json"]["SwordRecipe"]!["CraftTime"]!.GetValue<int>());
        Assert.Equal(5, mergedFiles["Crafting-D_ProcessorRecipes.json"]["LaserSwordRecipe"]!["CraftTime"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_ChangesForUnknownFile_SkipsWithWarning()
    {
        var change = new FieldChange("Unknown-D_File.json", "Row", "Field", null, JsonValue.Create(1), ValueSemantic.Scalar);
        var report = new MergeReport();

        var result = MultiFileMerger.Apply(new Dictionary<string, JsonObject>(), [change], report);

        Assert.Empty(result);
        Assert.Single(report.Warnings);
    }
}
