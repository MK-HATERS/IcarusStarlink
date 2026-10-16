using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>"What this mod changes" in a few lines — checked line-for-line against a real QoL mod (Deyvid's All-in-One) during development; these pin each pattern.</summary>
public sealed class ModChangeSummarizerTests
{
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
