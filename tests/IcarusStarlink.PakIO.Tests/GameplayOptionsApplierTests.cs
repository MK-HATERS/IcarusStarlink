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
    public void Apply_StacksMultiplier_FieldRenamedInGameData_WarnsInsteadOfSilentlyDoingNothing()
    {
        // Simulates a future game update renaming MaxStack — every row present, none carrying the
        // field this option relies on. Before Phase 2 this just silently did nothing; now it warns.
        var tables = Table("Traits-D_Itemable.json", new JsonObject { ["Item_Fiber"] = Row("""{"StackLimit": 200}""") });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 3 }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Stacks multiplier") && w.Contains("MaxStack"));
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
    public void Apply_RemoveWeight_FieldRenamedInGameData_WarnsInsteadOfSilentlyDoingNothing()
    {
        var tables = Table("Traits-D_Itemable.json", new JsonObject { ["Item_Stone"] = Row("""{"ItemWeight": 300}""") });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { RemoveWeight = true }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Remove Weight") && w.Contains("Weight"));
    }

    [Fact]
    public void Apply_SlotsMultiplier_ScalesStartingSlots()
    {
        var tables = Table("Inventory-D_InventoryInfo.json", new JsonObject { ["Backpack"] = Row("""{"StartingSlots": 24}""") });

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 2 }, tables, new MergeReport());

        Assert.Equal(48, (int)tables["Inventory-D_InventoryInfo.json"]["Backpack"]!["StartingSlots"]!);
    }

    [Fact]
    public void Apply_SlotsMultiplier_FieldRenamedInGameData_WarnsInsteadOfSilentlyDoingNothing()
    {
        var tables = Table("Inventory-D_InventoryInfo.json", new JsonObject { ["Backpack"] = Row("""{"SlotCount": 24}""") });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 2 }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Slots multiplier") && w.Contains("StartingSlots"));
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
    public void Apply_CraftCostReduction_NeitherFileHasInputsOrResourceInputs_WarnsInsteadOfSilentlyDoingNothing()
    {
        var tables = Table("Crafting-D_ProcessorRecipes.json", new JsonObject { ["Dough_Bread"] = Row("""{"Ingredients": []}""") });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { CraftCost = CraftCostReduction.FiftyPercent }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Craft Cost") && w.Contains("Inputs"));
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
    public void Apply_SpeedCrafting_NoEligibleRecipes_WarnsInsteadOfSilentlyDoingNothing()
    {
        // Every row present, but every one has a ResourceInputs entry — none is eligible, matching
        // the exact silent-failure shape a game update removing RequiredMillijoules would also cause.
        var tables = Table("Crafting-D_ProcessorRecipes.json", new JsonObject
        {
            ["Dough_Bread"] = Row("""{"RequiredMillijoules": 2500, "ResourceInputs": [{"Type": {"Value": "Water"}, "RequiredUnits": 100}]}"""),
        });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { SpeedCraftingReductionPercent = 50 }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Speed Crafting") && w.Contains("RequiredMillijoules"));
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
    public void Apply_UnlimitedAmmo_TableHasNoRowsAtAll_WarnsInsteadOfSilentlyDoingNothing()
    {
        // Unlike the other options, this one sets its field on every row regardless of any prior
        // value — so the only way to have "zero rows to touch" is the table itself being empty
        // (e.g. a game update removed every firearm row, or renamed the file's own row set away).
        var tables = Table("Tools-D_FirearmData.json", new JsonObject());
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { UnlimitedAmmo = true }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Unlimited Ammo"));
    }

    // Speed Boost/Player Boost/XP Boost/Disable Temperatures moved to
    // GameplayOptionsFieldChangeGeneratorTests.cs — they're real FieldChanges now
    // (GameplayOptionsFieldChangeGenerator), not something GameplayOptionsApplier.Apply handles.

    [Fact]
    public void Apply_NoTablesPresentForAnEnabledOption_DoesNotThrow()
    {
        var exception = Record.Exception(() =>
            GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 2, UnlimitedAmmo = true }, new Dictionary<string, JsonObject>(), new MergeReport()));

        Assert.Null(exception);
    }
}
