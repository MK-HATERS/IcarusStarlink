using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>"What this mod changes" in a few lines — checked line-for-line against a real QoL mod (Deyvid's All-in-One); these pin each pattern.</summary>
public sealed class ModChangeSummarizerTests
{
    /// <summary>Unreal row names ignore case: "item_bread" edits the game's Item_Bread, it doesn't add a row.</summary>
    [Fact]
    public void SummarizePackage_ARowSpelledInAnotherCase_IsAnEditOfTheGamesRow()
    {
        var root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Summary_" + Guid.NewGuid().ToString("N"));
        try
        {
            MiniGameData.Write(root);
            var package = new ExmodPackage
            {
                Name = "m", Author = "a", Version = "1", Description = "", FileName = "m",
                Rows = [new ExmodFileRow { CurrentFile = "Traits-D_Itemable.json", FileItems = [new ExmodFileItem { Name = "item_bread", Fields = new() { ["Weight"] = 500 } }] }],
            };

            var lines = ModChangeSummarizer.SummarizePackage(package, root);

            Assert.DoesNotContain(lines, l => l.Contains("new row", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(lines, l => l.Contains("Weight", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FieldChange Change(string file, string row, string field, JsonNode? before, JsonNode? after, bool isNew = false) =>
        new(file, row, field, before, after, ValueSemantic.Scalar, IsNewItem: isNew);

    [Fact]
    public void OneFactorAcrossATable_IsOneLine()
    {
        var lines = ModChangeSummarizer.Summarize(
        [
            Change("Traits-D_Itemable.json", "Item_Fiber", "MaxStack", 200, 2000),
            Change("Traits-D_Itemable.json", "Item_Stone", "MaxStack", 20, 200),
            Change("Traits-D_Itemable.json", "Item_Wood", "MaxStack", 50, 500),
        ]);

        Assert.Equal(["D_Itemable · MaxStack ×10 on 3 rows"], lines);
    }

    [Fact]
    public void ModsOwnRounding_StillReadsAsOneFactor()
    {
        var lines = ModChangeSummarizer.Summarize(
        [
            Change("Farming-D_FarmingGrowthStates.json", "A", "TimeToNextState", 600, 400),
            Change("Farming-D_FarmingGrowthStates.json", "B", "TimeToNextState", 200, 133),
            Change("Farming-D_FarmingGrowthStates.json", "C", "TimeToNextState", 1000, 668),
        ]);

        Assert.Equal(["D_FarmingGrowthStates · TimeToNextState ×0.67 on 3 rows"], lines);
    }

    [Fact]
    public void ManyStatsWithOneFactor_FoldIntoOneLine()
    {
        static JsonObject Stats(int cold, int heat, int physical) => new()
        {
            ["(Value=\"BaseColdResistance_%\")"] = cold,
            ["(Value=\"BaseHeatResistance_%\")"] = heat,
            ["(Value=\"BasePhysicalDamageResistance_%\")"] = physical,
        };

        var lines = ModChangeSummarizer.Summarize(
        [
            Change("Traits-D_Armour.json", "Chest", "ArmourStats", Stats(6, 4, 2), Stats(9, 6, 3)),
            Change("Traits-D_Armour.json", "Legs", "ArmourStats", Stats(4, 2, 2), Stats(6, 3, 3)),
        ]);

        Assert.Equal(["D_Armour · ArmourStats (3 stats) ×1.5 on 2 rows"], lines);
    }

    [Fact]
    public void SameValueEverywhere_IncludingAddedFields_ReadsAsSetTo()
    {
        var lines = ModChangeSummarizer.Summarize(
        [
            Change("Building-D_BuildingStability.json", "Wood", "MaxHardStability", 50, 1000000),
            Change("Building-D_BuildingStability.json", "Stone", "MaxHardStability", 20, 1000000),
            Change("Building-D_BuildingStability.json", "Iron", "MaxHardStability", null, 1000000),
            Change("Building-D_BuildingStability.json", "Glass", "YellowStability", 30, 0),
            Change("Building-D_BuildingStability.json", "Ice", "YellowStability", 10, 0),
        ]);

        Assert.Equal(
        [
            "D_BuildingStability · MaxHardStability set to 1000000 on 3 rows",
            "D_BuildingStability · YellowStability set to 0 on 2 rows",
        ], lines);
    }

    [Fact]
    public void NewRowsAndMixedChanges_AreDescribedHonestly()
    {
        var lines = ModChangeSummarizer.Summarize(
        [
            Change("UI-D_MapIcons.json", "DeepVeinOreDeposit", "MapIcon", null, "Icon", isNew: true),
            Change("UI-D_MapIcons.json", "Cave", "MapIcon", null, "Icon", isNew: true),
            Change("Traits-D_Thermal.json", "Campfire", "OuterRadius", 500, 1200),
            Change("Traits-D_Thermal.json", "Fireplace", "OuterRadius", 350, 500),
            Change("Traits-D_Thermal.json", "Heater", "OuterRadius", 1000, 2000),
        ]);

        Assert.Contains("D_Thermal · OuterRadius changed on 3 rows", lines);
        Assert.Contains("D_MapIcons · adds 2 new row(s), e.g. Cave", lines);
    }

    /// <summary>
    /// Regression: an emptied list (laanp-WorkshopFree's ResearchCost) used to read as each of the
    /// game's inner paths "changed" ("ResearchCost[].Meta.DataTableName changed on 339 rows").
    /// </summary>
    [Fact]
    public void AnEmptiedList_ReadsAsEmptied_NotAsItsInnerPathsChanged()
    {
        static JsonNode Cost() => JsonNode.Parse("""[{"Meta":{"RowName":"Credits","DataTableName":"D_MetaCurrency"},"Amount":50}]""")!;
        var lines = ModChangeSummarizer.Summarize(
        [
            Change("MetaWorkshop-D_WorkshopItems.json", "A", "ResearchCost", Cost(), new JsonArray()),
            Change("MetaWorkshop-D_WorkshopItems.json", "B", "ResearchCost", Cost(), new JsonArray()),
            Change("MetaWorkshop-D_WorkshopItems.json", "C", "ResearchCost", Cost(), new JsonArray()),
        ]);

        Assert.Equal(["D_WorkshopItems · ResearchCost emptied on 3 rows"], lines);
    }

    /// <summary>Regression: a field the mod takes out (null) used to read as its inner values changing.</summary>
    [Fact]
    public void ATakenOutField_SaysTheTablesDefaultApplies()
    {
        var lines = ModChangeSummarizer.Summarize([Change("Traits-D_Itemable.json", "Item_Wood", "Weight", 10, null)]);

        Assert.Equal(["D_Itemable · Weight taken out on 1 row (the table's default applies)"], lines);
    }

    /// <summary>Regression: which table a link points into is noise ("OptimalBiomes[].DataTableName changed on 35 rows").</summary>
    [Fact]
    public void ALinksTableName_IsNotACountedChange()
    {
        var lines = ModChangeSummarizer.Summarize(
        [
            Change("Farming-D_FarmingSeeds.json", "Seed_Corn", "OptimalBiomes",
                JsonNode.Parse("""[{"RowName":"Forest","DataTableName":"D_Biomes"}]"""), JsonNode.Parse("""[{"RowName":"Swamp","DataTableName":"D_BiomesX"}]""")),
        ]);

        Assert.Equal(["D_FarmingSeeds · OptimalBiomes[].RowName changed on 1 row"], lines);
    }

    /// <summary>Regression: rows of a table the game doesn't have used to be summed as edits, though the merge skips them.</summary>
    [Fact]
    public void SummarizePackage_RowsInATableTheGameDoesntHave_AreCountedApart()
    {
        var root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Summary_" + Guid.NewGuid().ToString("N"));
        try
        {
            MiniGameData.Write(root);
            var package = new ExmodPackage
            {
                Name = "m", Author = "a", Version = "1", Description = "", FileName = "m",
                Rows =
                [
                    new ExmodFileRow
                    {
                        CurrentFile = "Nope-D_Nope.json",
                        FileItems =
                        [
                            new ExmodFileItem { Name = "Row_A", Fields = new() { ["Weight"] = 5 } },
                            new ExmodFileItem { Name = "Row_B", Fields = new() { ["Weight"] = 5 } },
                        ],
                    },
                ],
            };

            var lines = ModChangeSummarizer.SummarizePackage(package, root);

            Assert.Equal(["D_Nope · 2 row(s) in a table the game doesn't have (the merge skips them)"], lines);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AVeryBroadMod_IsCappedWithAPointerToTheFullList()
    {
        var changes = Enumerable.Range(0, 40)
            .Select(i => Change($"T-D_Table{i}.json", "Row", "Field", 1, 2))
            .ToList();

        var lines = ModChangeSummarizer.Summarize(changes);

        Assert.Equal(26, lines.Count);
        Assert.StartsWith("…and 15 smaller", lines[^1]);
    }
}
