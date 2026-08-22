using System.Text.Json.Nodes;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.GameplayToggles;

namespace IcarusStarlink.PakIO.Tests;

public class GameplayOptionsApplierTests
{
    private static Dictionary<string, JsonObject> Table(string currentFile, JsonObject rows) => new() { [currentFile] = rows };

    private static JsonObject Row(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void RequiredCurrentFiles_NoOptionsEnabled_IsEmpty()
    {
        Assert.Empty(GameplayOptionsApplier.RequiredCurrentFiles(new GameplayOptions()));
    }

    [Fact]
    public void RequiredCurrentFiles_StacksEnabled_IncludesItemableFile()
    {
        var files = GameplayOptionsApplier.RequiredCurrentFiles(new GameplayOptions { StacksMultiplier = 3 });
        Assert.Contains("Traits-D_Itemable.json", files);
    }

    [Fact]
    public void Apply_StacksMultiplier_ScalesOnlyRowsWithAnExplicitMaxStack()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Traits-D_Itemable.json"] = new()
            {
                ["Item_Fiber"] = Row("""{"MaxStack": 200}"""),
                ["Item_Tool"] = Row("""{"Weight": 50}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 3 }, tables, new MergeReport());

        Assert.Equal(600, (int)tables["Traits-D_Itemable.json"]["Item_Fiber"]!["MaxStack"]!);
        Assert.False(tables["Traits-D_Itemable.json"]["Item_Tool"]!.AsObject().ContainsKey("MaxStack"));
    }

    [Fact]
    public void Apply_StacksMultiplier_NeverScalesBelowOne()
    {
        var tables = Table("Traits-D_Itemable.json", new JsonObject { ["Item_X"] = Row("""{"MaxStack": 2}""") });

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 0.1 }, tables, new MergeReport());

        Assert.Equal(1, (int)tables["Traits-D_Itemable.json"]["Item_X"]!["MaxStack"]!);
    }

    [Fact]
    public void Apply_RemoveWeight_ZeroesOnlyRowsWithAnExplicitWeight()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Traits-D_Itemable.json"] = new()
            {
                ["Item_Stone"] = Row("""{"Weight": 300}"""),
                ["Item_NoWeightOverride"] = Row("""{"MaxStack": 5}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { RemoveWeight = true }, tables, new MergeReport());

        Assert.Equal(0, (int)tables["Traits-D_Itemable.json"]["Item_Stone"]!["Weight"]!);
        Assert.False(tables["Traits-D_Itemable.json"]["Item_NoWeightOverride"]!.AsObject().ContainsKey("Weight"));
    }

    [Fact]
    public void Apply_SlotsMultiplier_ScalesStartingSlots()
    {
        var tables = Table("Inventory-D_InventoryInfo.json", new JsonObject { ["Backpack"] = Row("""{"StartingSlots": 24}""") });

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 2 }, tables, new MergeReport());

        Assert.Equal(48, (int)tables["Inventory-D_InventoryInfo.json"]["Backpack"]!["StartingSlots"]!);
    }

    [Fact]
    public void Apply_CraftCostReduction_ScalesInputsCountAndResourceInputsButNotOutputs()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new()
            {
                ["Dough_Bread"] = Row("""
                    {
                        "Inputs": [{"Element": {"RowName": "Flour"}, "Count": 4}],
                        "ResourceInputs": [{"Type": {"Value": "Water"}, "RequiredUnits": 100}],
                        "Outputs": [{"Element": {"RowName": "Dough_Bread"}, "Count": 1}]
                    }
                    """),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { CraftCost = CraftCostReduction.FiftyPercent }, tables, new MergeReport());

        var row = tables["Crafting-D_ProcessorRecipes.json"]["Dough_Bread"]!;
        Assert.Equal(2, (int)row["Inputs"]![0]!["Count"]!);
        Assert.Equal(50, (int)row["ResourceInputs"]![0]!["RequiredUnits"]!);
        Assert.Equal(1, (int)row["Outputs"]![0]!["Count"]!);
    }

    [Fact]
    public void Apply_CraftCostReduction_NeverScalesCountBelowOne()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new()
            {
                ["Cheap_Item"] = Row("""{"Inputs": [{"Element": {"RowName": "Fiber"}, "Count": 1}]}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { CraftCost = CraftCostReduction.FiftyPercent }, tables, new MergeReport());

        Assert.Equal(1, (int)tables["Crafting-D_ProcessorRecipes.json"]["Cheap_Item"]!["Inputs"]![0]!["Count"]!);
    }

    [Fact]
    public void Apply_CraftCostReduction_CreativeZeroesInputsCompletely()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new()
            {
                ["Dough_Bread"] = Row("""
                    {
                        "Inputs": [{"Element": {"RowName": "Flour"}, "Count": 4}],
                        "ResourceInputs": [{"Type": {"Value": "Water"}, "RequiredUnits": 100}],
                        "Outputs": [{"Element": {"RowName": "Dough_Bread"}, "Count": 1}]
                    }
                    """),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { CraftCost = CraftCostReduction.Creative }, tables, new MergeReport());

        var row = tables["Crafting-D_ProcessorRecipes.json"]["Dough_Bread"]!;
        Assert.Equal(0, (int)row["Inputs"]![0]!["Count"]!);
        Assert.Equal(0, (int)row["ResourceInputs"]![0]!["RequiredUnits"]!);
        Assert.Equal(1, (int)row["Outputs"]![0]!["Count"]!);
    }

    [Fact]
    public void Apply_SpeedCrafting_ScalesRequiredMillijoulesOnRecipesWithNoResourceInputOrOutput()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new()
            {
                ["Stone_Pickaxe"] = Row("""{"RequiredMillijoules": 2500}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { SpeedCraftingReductionPercent = 50 }, tables, new MergeReport());

        Assert.Equal(1250, (int)tables["Crafting-D_ProcessorRecipes.json"]["Stone_Pickaxe"]!["RequiredMillijoules"]!);
    }

    [Fact]
    public void Apply_SpeedCrafting_SkipsRecipesWithResourceInputs()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new()
            {
                ["Dough_Bread"] = Row("""
                    {"RequiredMillijoules": 2500, "ResourceInputs": [{"Type": {"Value": "Water"}, "RequiredUnits": 100}]}
                    """),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { SpeedCraftingReductionPercent = 50 }, tables, new MergeReport());

        Assert.Equal(2500, (int)tables["Crafting-D_ProcessorRecipes.json"]["Dough_Bread"]!["RequiredMillijoules"]!);
    }

    [Fact]
    public void Apply_SpeedCrafting_SkipsRecipesWithResourceOutputs()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new()
            {
                ["Biofuel1"] = Row("""
                    {"RequiredMillijoules": 2500, "ResourceOutputs": [{"Type": {"Value": "Biofuel"}, "RequiredUnits": 100}]}
                    """),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { SpeedCraftingReductionPercent = 50 }, tables, new MergeReport());

        Assert.Equal(2500, (int)tables["Crafting-D_ProcessorRecipes.json"]["Biofuel1"]!["RequiredMillijoules"]!);
    }

    [Fact]
    public void Apply_UnlimitedAmmo_SetsFlagOnEveryWeaponRowRegardlessOfExistingOverride()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Tools-D_FirearmData.json"] = new()
            {
                ["Wood_Bow"] = Row("""{"RoundsPerMinute": 300}"""),
                ["Pistol_Handgun"] = Row("""{"bUnlimitedAmmo": false}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { UnlimitedAmmo = true }, tables, new MergeReport());

        Assert.True((bool)tables["Tools-D_FirearmData.json"]["Wood_Bow"]!["bUnlimitedAmmo"]!);
        Assert.True((bool)tables["Tools-D_FirearmData.json"]["Pistol_Handgun"]!["bUnlimitedAmmo"]!);
    }

    [Fact]
    public void Apply_DisableTemperatures_OverridesTheIsTemperatureEnabledStat()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Stats-D_CharacterStartingStats.json"] = new()
            {
                ["Base_Stats"] = Row("""{"StatsGranted": {"(Value=\"IsTemperatureEnabled_?\")": 1}}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { DisableTemperatures = true }, tables, new MergeReport());

        var statsGranted = tables["Stats-D_CharacterStartingStats.json"]["Base_Stats"]!["StatsGranted"]!.AsObject();
        Assert.Equal(0, (int)statsGranted["(Value=\"IsTemperatureEnabled_?\")"]!);
    }

    [Fact]
    public void Apply_XpBoost_AddsExperienceStatWithoutRemovingExistingGrants()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Stats-D_CharacterStartingStats.json"] = new()
            {
                ["Base_Stats"] = Row("""{"StatsGranted": {"(Value=\"BaseMaximumHealth_+\")": 300}}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { XpBoost = XpBoostLevel.Level3 }, tables, new MergeReport());

        var statsGranted = tables["Stats-D_CharacterStartingStats.json"]["Base_Stats"]!["StatsGranted"]!.AsObject();
        Assert.Equal(1000, (int)statsGranted["(Value=\"BaseExperience_+%\")"]!);
        Assert.Equal(300, (int)statsGranted["(Value=\"BaseMaximumHealth_+\")"]!);
    }

    [Theory]
    [InlineData(XpBoostLevel.Level1, 200)]
    [InlineData(XpBoostLevel.Level2, 500)]
    [InlineData(XpBoostLevel.Level3, 1000)]
    public void Apply_XpBoost_EachLevelWritesItsOwnDocumentedPercent(XpBoostLevel level, int expectedPercent)
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Stats-D_CharacterStartingStats.json"] = new() { ["Base_Stats"] = Row("""{"StatsGranted": {}}""") },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { XpBoost = level }, tables, new MergeReport());

        var statsGranted = tables["Stats-D_CharacterStartingStats.json"]["Base_Stats"]!["StatsGranted"]!.AsObject();
        Assert.Equal(expectedPercent, (int)statsGranted["(Value=\"BaseExperience_+%\")"]!);
    }

    [Fact]
    public void Apply_SpeedBoostAndPlayerBoostTogether_BothWriteIntoTheSameStatsGrantedWithoutClobberingEachOther()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Stats-D_CharacterStartingStats.json"] = new() { ["Base_Stats"] = Row("""{"StatsGranted": {}}""") },
        };

        GameplayOptionsApplier.Apply(
            new GameplayOptions { SpeedBoost = BoostLevel.Level1, PlayerBoost = BoostLevel.Level1 }, tables, new MergeReport());

        var statsGranted = tables["Stats-D_CharacterStartingStats.json"]["Base_Stats"]!["StatsGranted"]!.AsObject();
        Assert.Equal(455, (int)statsGranted["(Value=\"BaseMovementSpeed_+\")"]!);
        Assert.Equal(350, (int)statsGranted["(Value=\"BaseMaximumHealth_+\")"]!);
    }

    [Fact]
    public void Apply_CharacterStatsFileMissingBaseStatsRow_AddsWarningInsteadOfThrowing()
    {
        var tables = new Dictionary<string, JsonObject> { ["Stats-D_CharacterStartingStats.json"] = [] };
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { XpBoost = XpBoostLevel.Level1 }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Base_Stats"));
    }

    [Fact]
    public void Apply_NoTablesPresentForAnEnabledOption_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 2, UnlimitedAmmo = true }, new Dictionary<string, JsonObject>(), new MergeReport()));

        Assert.Null(exception);
    }
}
