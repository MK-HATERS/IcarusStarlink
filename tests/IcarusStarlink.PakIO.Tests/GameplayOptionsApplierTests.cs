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

        // No originalFilesByFile given (the 3-argument overload) — no Defaults are visible, so a row
        // with no explicit MaxStack of its own stays untouched exactly like before this session's
        // Defaults-fallback fix. See Apply_StacksMultiplier_DefaultsOnlyRow_* below for the new path.
        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 3 }, tables, new MergeReport());

        Assert.Equal(600, (int)tables["Traits-D_Itemable.json"]["Item_Fiber"]!["MaxStack"]!);
        Assert.False(tables["Traits-D_Itemable.json"]["Item_Tool"]!.AsObject().ContainsKey("MaxStack"));
    }

    private static Dictionary<string, JsonObject> ItemableOriginalWithDefaultMaxStack(int defaultMaxStack = 1) => new()
    {
        ["Traits-D_Itemable.json"] = JsonNode.Parse($$"""{"Defaults": {"MaxStack": {{defaultMaxStack}}}, "Rows": []}""")!.AsObject(),
    };

    /// <summary>
    /// Regression guard for the Defaults-fallback fix: real Data\Traits\D_Itemable.json has 2143 of
    /// 3469 rows with no explicit MaxStack of their own — they inherit MaxStack=1 from the file's own
    /// Defaults block and were previously invisible to this option entirely, even though the app's own
    /// tooltip promises "e.g. 3x = triple every item's stack size" (an unqualified "every item").
    /// </summary>
    [Fact]
    public void Apply_StacksMultiplier_DefaultsOnlyRow_GetsANewExplicitMaxStack()
    {
        var tables = Table("Traits-D_Itemable.json", new JsonObject
        {
            ["Item_Crafting_Bench"] = Row("""{"Weight": 20000, "Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Deployables/ITEM_Crafting_Bench.ITEM_Crafting_Bench"}"""),
        });

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 3 }, tables, new MergeReport(), ItemableOriginalWithDefaultMaxStack());

        Assert.Equal(3, (int)tables["Traits-D_Itemable.json"]["Item_Crafting_Bench"]!["MaxStack"]!);
    }

    /// <summary>
    /// Regression guard: real, functional boss-reward weapons (Item_Hornet_Pistol, Item_Plant_Boss_Bow,
    /// Item_Cat_Boss_Gauntlets — each confirmed present in Tools-D_FirearmData.json or
    /// Tools-D_ToolDamage.json) have no Icon field of their own at all in the real Traits-D_Itemable.json,
    /// unlike every other real weapon/tool row. Treating "no Icon to categorize by" as "definitely not
    /// a weapon/tool" (fail open) would make these unique items stackable — exactly what
    /// IsWeaponOrTool exists to prevent. Must fail SAFE (exclude) instead.
    /// </summary>
    [Fact]
    public void Apply_StacksMultiplier_DefaultsOnlyRowWithNoIconAtAll_FailsSafeAndStaysUntouched()
    {
        var tables = Table("Traits-D_Itemable.json", new JsonObject { ["Item_Hornet_Pistol"] = Row("""{"Weight": 300}""") });

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 3 }, tables, new MergeReport(), ItemableOriginalWithDefaultMaxStack());

        Assert.False(tables["Traits-D_Itemable.json"]["Item_Hornet_Pistol"]!.AsObject().ContainsKey("MaxStack"));
    }

    /// <summary>
    /// D_Itemable.json has no explicit item-category field — a row's own Icon path (e.g.
    /// ".../Item_Icons/Weapons/...", ".../Item_Icons/Tools/...") is the one data-driven signal
    /// available to exclude actual weapons/tools, matching what two real, independent "increase
    /// stacks" mods (Jimk72's, relentlessmoose's) never touch even while freely stack-boosting
    /// hundreds of other Defaults-only rows (deployables, furniture, trophies, attachments).
    /// </summary>
    [Theory]
    [InlineData("/Game/Assets/2DArt/UI/Items/Item_Icons/Weapons/ITEM_Wood_Bow.ITEM_Wood_Bow")]
    [InlineData("/Game/Assets/2DArt/UI/Items/Item_Icons/Tools/ITEM_Stone_Axe.ITEM_Stone_Axe")]
    [InlineData("/Game/Assets/2DArt/UI/Items/Item_Icons/LegendaryWeapons/ITEM_LegendaryWeapon_Bow.ITEM_LegendaryWeapon_Bow")]
    public void Apply_StacksMultiplier_DefaultsOnlyWeaponOrToolRow_StaysUntouched(string iconPath)
    {
        var tables = Table("Traits-D_Itemable.json", new JsonObject { ["Item_Weapon_Or_Tool"] = Row($$"""{"Icon": "{{iconPath}}"}""") });

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 3 }, tables, new MergeReport(), ItemableOriginalWithDefaultMaxStack());

        Assert.False(tables["Traits-D_Itemable.json"]["Item_Weapon_Or_Tool"]!.AsObject().ContainsKey("MaxStack"));
    }

    [Fact]
    public void Apply_StacksMultiplier_DefaultsOnlyDeployableRow_IconOutsideExcludedFolders_GetsANewExplicitMaxStack()
    {
        var tables = Table("Traits-D_Itemable.json", new JsonObject
        {
            ["Item_Armour_Stand"] = Row("""{"Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Deployables/ITEM_Armour_Stand.ITEM_Armour_Stand"}"""),
        });

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 5 }, tables, new MergeReport(), ItemableOriginalWithDefaultMaxStack());

        Assert.Equal(5, (int)tables["Traits-D_Itemable.json"]["Item_Armour_Stand"]!["MaxStack"]!);
    }

    [Fact]
    public void Apply_StacksMultiplier_ExplicitAndDefaultsOnlyRowsTogether_BothScaledCorrectly()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Traits-D_Itemable.json"] = new()
            {
                ["Item_Fiber"] = Row("""{"MaxStack": 200}"""),
                ["Item_Crafting_Bench"] = Row("""{"Weight": 20000, "Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Deployables/ITEM_Crafting_Bench.ITEM_Crafting_Bench"}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 3 }, tables, new MergeReport(), ItemableOriginalWithDefaultMaxStack());

        Assert.Equal(600, (int)tables["Traits-D_Itemable.json"]["Item_Fiber"]!["MaxStack"]!);
        Assert.Equal(3, (int)tables["Traits-D_Itemable.json"]["Item_Crafting_Bench"]!["MaxStack"]!);
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

    /// <summary>
    /// Regression guard for a real bug in classic IMM's own history (its changelog: "Fixed Remove
    /// weight merge option from crashing the game. When only it was selected the stack size was
    /// going to 0.") — Remove Weight and Stacks both write into the same Traits-D_Itemable.json,
    /// on the same rows, so this is the one place a shared-mutation bug between them could occur.
    /// Confirms MaxStack and Weight are scaled/zeroed completely independently: enabling both
    /// together must scale MaxStack exactly as if Stacks were the only option enabled, and zero
    /// Weight exactly as if Remove Weight were the only option enabled.
    /// </summary>
    [Fact]
    public void Apply_StacksMultiplierAndRemoveWeightTogether_NeitherOptionCorruptsTheOthersField()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Traits-D_Itemable.json"] = new()
            {
                ["Item_Fiber"] = Row("""{"MaxStack": 200, "Weight": 10}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { StacksMultiplier = 3, RemoveWeight = true }, tables, new MergeReport());

        var row = tables["Traits-D_Itemable.json"]["Item_Fiber"]!;
        Assert.Equal(600, (int)row["MaxStack"]!);
        Assert.Equal(0, (int)row["Weight"]!);
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

    /// <summary>
    /// Regression test: Quickbar's own real shape (10 regular slots, then SlotOverrides reserving
    /// position 10 for "Any_Utility" and 11 for "Player_Fists", right after them) — those two
    /// positions only mean "the last two slots" relative to Quickbar's OWN 12-slot count. Scaling
    /// StartingSlots to 24 without excluding this row would leave the reserved slots stranded at
    /// positions 10-11 out of 24 (the middle, not the end), with 12 new regular slots appended after
    /// them. Confirmed against two real, independent community "increase slots" mods
    /// (Sarge_Deployable_Slots_Changes, Jimk72's own Increased Slots) that neither ever touches
    /// Quickbar, Equipment, Space_Equipment, or ArmourStand — all four have a SlotOverrides array —
    /// while both freely rewrite dozens of ordinary storage/processor rows that don't.
    /// </summary>
    [Fact]
    public void Apply_SlotsMultiplier_SkipsRowsWithSlotOverrides_LikeQuickbar()
    {
        var tables = Table("Inventory-D_InventoryInfo.json", new JsonObject
        {
            ["Quickbar"] = Row("""
                {
                    "StartingSlots": 12,
                    "SlotOverrides": [
                        { "Query": { "RowName": "Any_Utility" }, "Location": 10 },
                        { "Query": { "RowName": "Player_Fists" }, "Location": 11 }
                    ]
                }
                """),
            ["Backpack"] = Row("""{"StartingSlots": 24}"""),
        });

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 2 }, tables, new MergeReport());

        var result = tables["Inventory-D_InventoryInfo.json"];
        Assert.Equal(12, (int)result["Quickbar"]!["StartingSlots"]!); // untouched — SlotOverrides present
        Assert.Equal(48, (int)result["Backpack"]!["StartingSlots"]!); // scaled normally — no SlotOverrides
    }

    [Fact]
    public void Apply_SlotsMultiplier_RowWithEmptySlotOverridesArray_StillGetsScaled()
    {
        // An empty SlotOverrides array (the real Defaults value every ordinary row starts from) is
        // not the same as one that actually reserves a position — only a non-empty array should
        // skip the row.
        var tables = Table("Inventory-D_InventoryInfo.json", new JsonObject
        {
            ["Container"] = Row("""{"StartingSlots": 20, "SlotOverrides": []}"""),
        });

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 2 }, tables, new MergeReport());

        Assert.Equal(40, (int)tables["Inventory-D_InventoryInfo.json"]["Container"]!["StartingSlots"]!);
    }

    [Fact]
    public void RequiredCurrentFiles_SlotsEnabled_IncludesInventoryAlterationsAndTalents()
    {
        var files = GameplayOptionsApplier.RequiredCurrentFiles(new GameplayOptions { SlotsMultiplier = 2 });
        Assert.Contains("Inventory-D_InventoryInfo.json", files);
        Assert.Contains("Alterations-D_Alterations.json", files);
        Assert.Contains("Talents-D_Talents.json", files);
    }

    /// <summary>
    /// A player's real total slot count for many deployables isn't just the base Inventory table —
    /// crafting a Storage_1-4 or Carrying_Bonus_1/2 alteration grants a flat "+N slots" bonus on top
    /// of it. Matches the real Sarge_Deployable_Slots_Changes community mod, which scales this
    /// alongside the base table rather than leaving it as a smaller, un-scaled fraction of the total.
    /// </summary>
    [Fact]
    public void Apply_SlotsMultiplier_ScalesAlterationSlotBonusStats()
    {
        var tables = Table("Alterations-D_Alterations.json", new JsonObject
        {
            ["Storage_2"] = Row("""{"Stats": {"(Value=\"BaseGenericSlots_+\")": 5}}"""),
            ["Carrying_Bonus_1"] = Row("""{"Stats": {"(Value=\"BaseBackpackSlots_+\")": 2}}"""),
            ["Unrelated_Alteration"] = Row("""{"Stats": {"(Value=\"BaseMovementSpeed_+\")": 50}}"""),
        });

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 2 }, tables, new MergeReport());

        var result = tables["Alterations-D_Alterations.json"];
        Assert.Equal(10, (int)result["Storage_2"]!["Stats"]!["(Value=\"BaseGenericSlots_+\")"]!);
        Assert.Equal(4, (int)result["Carrying_Bonus_1"]!["Stats"]!["(Value=\"BaseBackpackSlots_+\")"]!);
        // Unrelated stat untouched, and the row isn't even in `changes` for this option at all.
        Assert.Equal(50, (int)result["Unrelated_Alteration"]!["Stats"]!["(Value=\"BaseMovementSpeed_+\")"]!);
    }

    [Fact]
    public void Apply_SlotsMultiplier_NoAlterationRowsHaveASlotStat_WarnsForThatTableSpecifically()
    {
        var tables = Table("Alterations-D_Alterations.json", new JsonObject
        {
            ["Some_Alteration"] = Row("""{"Stats": {"(Value=\"BaseMovementSpeed_+\")": 50}}"""),
        });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 2 }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Alteration upgrades"));
    }

    /// <summary>
    /// Same reasoning as the Alterations test, for the "Extra Space" Workshop talent line's own
    /// flat deployable-storage bonus — each reward TIER (one per talent point spent) gets scaled
    /// independently, matching real Data\Talents\D_Talents.json's own multi-tier Rewards array shape.
    /// </summary>
    [Fact]
    public void Apply_SlotsMultiplier_ScalesEveryTalentRewardTierGrantingDeployableStorage()
    {
        var tables = Table("Talents-D_Talents.json", new JsonObject
        {
            ["Building_Storage_Increase_0"] = Row("""
                {
                    "Rewards": [
                        { "GrantedStats": {"(Value=\"CreatedDeployableStorageAlt_+\")": 1}, "GrantedFlags": [] },
                        { "GrantedStats": {"(Value=\"CreatedDeployableStorageAlt_+\")": 2}, "GrantedFlags": [] }
                    ]
                }
                """),
            ["Unrelated_Talent"] = Row("""
                {
                    "Rewards": [
                        { "GrantedStats": {"(Value=\"BaseMaximumHealth_+\")": 50}, "GrantedFlags": [] }
                    ]
                }
                """),
        });

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 3 }, tables, new MergeReport());

        var rewards = tables["Talents-D_Talents.json"]["Building_Storage_Increase_0"]!["Rewards"]!.AsArray();
        Assert.Equal(3, (int)rewards[0]!["GrantedStats"]!["(Value=\"CreatedDeployableStorageAlt_+\")"]!);
        Assert.Equal(6, (int)rewards[1]!["GrantedStats"]!["(Value=\"CreatedDeployableStorageAlt_+\")"]!);
        // A talent granting something else entirely is untouched.
        Assert.Equal(50, (int)tables["Talents-D_Talents.json"]["Unrelated_Talent"]!["Rewards"]![0]!["GrantedStats"]!["(Value=\"BaseMaximumHealth_+\")"]!);
    }

    [Fact]
    public void Apply_SlotsMultiplier_NoTalentRowsGrantDeployableStorage_WarnsForThatTableSpecifically()
    {
        var tables = Table("Talents-D_Talents.json", new JsonObject
        {
            ["Some_Talent"] = Row("""{"Rewards": [{"GrantedStats": {"(Value=\"BaseMaximumHealth_+\")": 50}, "GrantedFlags": []}]}"""),
        });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { SlotsMultiplier = 2 }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Workshop talents"));
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

    /// <summary>
    /// Regression guard: 24 real rows in D_ProcessorRecipes.json (Cooked_Fish, the Butcher_*/
    /// Farmer_Plant_*/Fisher_*/Animal_*_Gruel families, etc.) rely ENTIRELY on QueryInputs — a
    /// separate, tag-based ingredient-cost structure ("consume 1 of any raw fish") — with a
    /// completely empty Inputs array, so Creative mode's promised "0 cost" wasn't actually 0 for
    /// them. Confirmed a real, shipped "everything is free" mod (laanp's FreeBuild) has this exact
    /// same gap itself.
    /// </summary>
    [Fact]
    public void Apply_CraftCostReduction_ScalesQueryInputsCount()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new()
            {
                ["Cooked_Fish"] = Row("""{"Inputs": [], "QueryInputs": [{"Query": {"RowName": "Any_Raw_Fish"}, "Count": 4}]}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { CraftCost = CraftCostReduction.FiftyPercent }, tables, new MergeReport());

        Assert.Equal(2, (int)tables["Crafting-D_ProcessorRecipes.json"]["Cooked_Fish"]!["QueryInputs"]![0]!["Count"]!);
    }

    [Fact]
    public void Apply_CraftCostReduction_CreativeZeroesQueryInputsCompletely()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = new()
            {
                ["Cooked_Fish"] = Row("""{"Inputs": [], "QueryInputs": [{"Query": {"RowName": "Any_Raw_Fish"}, "Count": 4}]}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { CraftCost = CraftCostReduction.Creative }, tables, new MergeReport());

        Assert.Equal(0, (int)tables["Crafting-D_ProcessorRecipes.json"]["Cooked_Fish"]!["QueryInputs"]![0]!["Count"]!);
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

    /// <summary>
    /// Regression guard: an older version of this method skipped any recipe with a non-empty
    /// ResourceInputs/ResourceOutputs (a misread of a classic-IMM changelog line) — dropped after two
    /// independent, real "speed up crafting" mods (AgentKush's, TheLysdexicOne's) were both confirmed
    /// to simply halve RequiredMillijoules on these recipes exactly like any other, never excluding them.
    /// </summary>
    [Fact]
    public void Apply_SpeedCrafting_ScalesRecipesWithResourceInputsToo()
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

        Assert.Equal(1250, (int)tables["Crafting-D_ProcessorRecipes.json"]["Dough_Bread"]!["RequiredMillijoules"]!);
    }

    [Fact]
    public void Apply_SpeedCrafting_ScalesRecipesWithResourceOutputsToo()
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

        Assert.Equal(1250, (int)tables["Crafting-D_ProcessorRecipes.json"]["Biofuel1"]!["RequiredMillijoules"]!);
    }

    [Fact]
    public void Apply_SpeedCrafting_NoEligibleRecipes_WarnsInsteadOfSilentlyDoingNothing()
    {
        // Every row present, but none carries a RequiredMillijoules field and no Defaults value is
        // available (the 3-argument Apply overload) — matching the exact silent-failure shape a game
        // update removing RequiredMillijoules would also cause.
        var tables = Table("Crafting-D_ProcessorRecipes.json", new JsonObject
        {
            ["Dough_Bread"] = Row("""{"CraftTime": 2500}"""),
        });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { SpeedCraftingReductionPercent = 50 }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Speed Crafting") && w.Contains("RequiredMillijoules"));
    }

    /// <summary>
    /// Regression guard for the Defaults-fallback fix: real Data\Crafting\D_ProcessorRecipes.json has
    /// 817 recipes (Stone_Pickaxe, Stone_Axe, Wood_Spear, etc.) with no explicit RequiredMillijoules
    /// of their own — they inherit the real 2500 value from the file's own Defaults block, and were
    /// previously skipped entirely (zero effect for any percentage). A real, actively-maintained mod
    /// (AgentKush's "Faster Crafting") explicitly sets RequiredMillijoules on 800 of those exact rows,
    /// confirming they're legitimate targets.
    /// </summary>
    [Fact]
    public void Apply_SpeedCrafting_DefaultsOnlyRecipe_FallsBackToTheFilesOwnDefaultValue()
    {
        var tables = Table("Crafting-D_ProcessorRecipes.json", new JsonObject
        {
            ["Stone_Pickaxe"] = Row("""{"Inputs": []}"""),
        });
        var originalFiles = new Dictionary<string, JsonObject>
        {
            ["Crafting-D_ProcessorRecipes.json"] = JsonNode.Parse("""{"Defaults": {"RequiredMillijoules": 2500}, "Rows": []}""")!.AsObject(),
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { SpeedCraftingReductionPercent = 50 }, tables, new MergeReport(), originalFiles);

        Assert.Equal(1250, (int)tables["Crafting-D_ProcessorRecipes.json"]["Stone_Pickaxe"]!["RequiredMillijoules"]!);
    }

    [Fact]
    public void Apply_SpeedCrafting_NoDefaultsGiven_DefaultsOnlyRecipeStaysUntouched()
    {
        // Same shape as the fallback test above, but called through the 3-argument overload (no
        // originalFilesByFile) — matches the old behavior exactly when Defaults truly aren't available.
        var tables = Table("Crafting-D_ProcessorRecipes.json", new JsonObject
        {
            ["Stone_Pickaxe"] = Row("""{"Inputs": []}"""),
        });

        GameplayOptionsApplier.Apply(new GameplayOptions { SpeedCraftingReductionPercent = 50 }, tables, new MergeReport());

        Assert.False(tables["Crafting-D_ProcessorRecipes.json"]["Stone_Pickaxe"]!.AsObject().ContainsKey("RequiredMillijoules"));
    }

    [Fact]
    public void RequiredCurrentFiles_TamingSpeedEnabled_IncludesTamesFile()
    {
        var files = GameplayOptionsApplier.RequiredCurrentFiles(new GameplayOptions { TamingSpeedReductionPercent = 50 });
        Assert.Contains("AI-D_Tames.json", files);
    }

    [Fact]
    public void Apply_FasterTaming_ScalesTameDurationOnEveryCreatureRow()
    {
        var tables = new Dictionary<string, JsonObject>
        {
            ["AI-D_Tames.json"] = new()
            {
                ["Moa"] = Row("""{"TameDurationInSeconds": 900}"""),
                ["Wolf"] = Row("""{"TameDurationInSeconds": 1800}"""),
            },
        };

        GameplayOptionsApplier.Apply(new GameplayOptions { TamingSpeedReductionPercent = 50 }, tables, new MergeReport());

        Assert.Equal(450, (int)tables["AI-D_Tames.json"]["Moa"]!["TameDurationInSeconds"]!);
        Assert.Equal(900, (int)tables["AI-D_Tames.json"]["Wolf"]!["TameDurationInSeconds"]!);
    }

    [Fact]
    public void Apply_FasterTaming_NeverGoesBelowOneSecond()
    {
        var tables = Table("AI-D_Tames.json", new JsonObject { ["Moa"] = Row("""{"TameDurationInSeconds": 2}""") });

        GameplayOptionsApplier.Apply(new GameplayOptions { TamingSpeedReductionPercent = 99 }, tables, new MergeReport());

        Assert.Equal(1, (int)tables["AI-D_Tames.json"]["Moa"]!["TameDurationInSeconds"]!);
    }

    [Fact]
    public void Apply_FasterTaming_FieldRenamedInGameData_WarnsInsteadOfSilentlyDoingNothing()
    {
        var tables = Table("AI-D_Tames.json", new JsonObject { ["Moa"] = Row("""{"TamingDuration": 900}""") });
        var report = new MergeReport();

        GameplayOptionsApplier.Apply(new GameplayOptions { TamingSpeedReductionPercent = 50 }, tables, report);

        Assert.Contains(report.Warnings, w => w.Contains("Faster Taming") && w.Contains("TameDurationInSeconds"));
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
