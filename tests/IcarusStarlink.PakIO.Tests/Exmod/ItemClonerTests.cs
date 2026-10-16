using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>"New item from a game item": copies, links, localization, and the recipe/workshop step.</summary>
public sealed class ItemClonerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Cloner_" + Guid.NewGuid().ToString("N"));
    private readonly GameTableIndex _game;

    public ItemClonerTests()
    {
        MiniGameData.Write(_root);
        _game = new GameTableIndex(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static JsonObject Row(IEnumerable<ExmodFileRow> rows, string file, string name)
    {
        var item = rows.Single(r => r.CurrentFile == file).FileItems.Single(i => i.Name == name);
        return new JsonObject(item.Fields.Select(f => KeyValuePair.Create(f.Key, f.Value?.DeepClone())));
    }

    private static string? RowName(JsonNode? link) => link?["RowName"]?.GetValue<string>();

    [Fact]
    public void Inspect_FollowsLinksThroughTheTablesOwnDefaults_AndFindsHowItsObtained()
    {
        var suit = ItemCloner.Inspect(_game, "Envirosuit")!;
        var bread = ItemCloner.Inspect(_game, "Food_Bread")!;

        Assert.Equal("Xigo Envirosuit", suit.DisplayName);
        Assert.Contains(suit.Links, l => l is { Field: "Armour", CurrentFile: "Traits-D_Armour.json", RowName: "IO_Undersuit", SuggestOwnCopy: true });
        Assert.Contains(suit.Links, l => l is { Field: "Meshable", SuggestOwnCopy: false });
        Assert.Equal("Meta_Envirosuit_Basic", suit.WorkshopItemName);
        Assert.Equal((50, 25), (suit.ResearchCredits, suit.ReplicationCredits));

        Assert.Equal("Bread", bread.RecipeName);
        Assert.Equal(["Kitchen_Stove"], bread.RecipeBenches);
        Assert.True(bread.RecipeHasUnlock);
        Assert.Equal("Bread", bread.BuffName);
    }

    [Fact]
    public void Build_OwnCopiesAreRenamedAndRelinked_SharedLinksStayOnTheGamesRows()
    {
        var rows = ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Envirosuit", NewName = "Dragon_Suit", DisplayName = "Dragon \"Fire\" Suit",
            OwnCopyFields = new HashSet<string> { "Itemable", "Armour" },
        });

        var item = Row(rows, "Items-D_ItemsStatic.json", "Dragon_Suit");
        Assert.Equal("Item_Dragon_Suit", RowName(item["Itemable"]));
        Assert.Equal("Dragon_Suit", RowName(item["Armour"]));
        Assert.Equal("Mesh_Generic_Workshop", RowName(item["Meshable"]));

        var itemable = Row(rows, "Traits-D_Itemable.json", "Item_Dragon_Suit");
        Assert.Equal("""NSLOCTEXT("D_Itemable", "Item_Dragon_Suit-DisplayName", "Dragon \"Fire\" Suit")""", itemable["DisplayName"]!.GetValue<string>());
        // No new description given: the original text stays, under the new row's own key.
        Assert.Equal("""NSLOCTEXT("D_Itemable", "Item_Dragon_Suit-Description", "A suit.")""", itemable["Description"]!.GetValue<string>());
        Assert.NotNull(Row(rows, "Traits-D_Armour.json", "Dragon_Suit")["ArmourStats"]);

        // What recipes and the workshop hand out is the template, which must point at the new item.
        Assert.Equal("Dragon_Suit", RowName(Row(rows, "Items-D_ItemTemplate.json", "Dragon_Suit")["ItemStaticData"]));
        Assert.DoesNotContain(rows, r => r.CurrentFile.Contains("Workshop") || r.CurrentFile.Contains("Recipes"));
    }

    [Fact]
    public void Workshop_InAnExistingTab_AddsTheItemAndATalentInTheNextFreeColumn()
    {
        var rows = ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Envirosuit", NewName = "Dragon_Suit", DisplayName = "Dragon Suit",
            Obtain = ObtainMethod.Workshop, WorkshopTree = "Workshop_Envirosuits", ResearchCredits = 200, ReplicationCredits = 40,
        });

        var workshop = Row(rows, "MetaWorkshop-D_WorkshopItems.json", "Meta_Dragon_Suit");
        Assert.Equal("Dragon_Suit", RowName(workshop["Item"]));
        Assert.Equal(200, workshop["ResearchCost"]![0]!["Amount"]!.GetValue<int>());
        Assert.Equal("Credits", RowName(workshop["ReplicationCost"]![0]!["Meta"]));

        var talent = Row(rows, "Talents-D_Talents.json", "Workshop_Dragon_Suit");
        Assert.Equal("Meta_Dragon_Suit", RowName(talent["ExtraData"]));
        Assert.Equal("D_WorkshopItems", talent["ExtraData"]!["DataTableName"]!.GetValue<string>());
        Assert.Equal("Workshop_Envirosuits", RowName(talent["TalentTree"]));
        Assert.Equal(1300, talent["Position"]!["X"]!.GetValue<double>());
        Assert.DoesNotContain(rows, r => r.CurrentFile == "Talents-D_TalentTrees.json");
    }

    /// <summary>How Bloodragon got its own workshop tab: an archetype + tree of the mod's own.</summary>
    [Fact]
    public void Workshop_InANewTab_AddsTheTabsArchetypeAndTree()
    {
        var rows = ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Envirosuit", NewName = "Dragon_Suit", DisplayName = "Dragon Suit",
            Obtain = ObtainMethod.Workshop, NewWorkshopTab = "Dragon Gear",
        });

        var archetype = Row(rows, "Talents-D_TalentArchetypes.json", "Workshop_Mod_Dragon_Gear");
        Assert.Equal("Workshop", RowName(archetype["Model"]));
        Assert.Contains("\"Dragon Gear\"", archetype["DisplayName"]!.GetValue<string>());
        Assert.Equal("Workshop_Mod_Dragon_Gear", RowName(Row(rows, "Talents-D_TalentTrees.json", "Workshop_Mod_Dragon_Gear")["Archetype"]));
        Assert.Equal("Workshop_Mod_Dragon_Gear", RowName(Row(rows, "Talents-D_Talents.json", "Workshop_Dragon_Suit")["TalentTree"]));
    }

    [Fact]
    public void Recipe_CopiesTheOriginalsRecipe_AtTheChosenBench_AndCanGiveItsOwnBuff()
    {
        var rows = ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Food_Bread", NewName = "Super_Bread", DisplayName = "Super Bread",
            OwnCopyFields = new HashSet<string> { "Itemable", "Consumable" }, CopyBuff = true,
            Obtain = ObtainMethod.Recipe, RecipeBench = "Crafting_Bench", UnlockWithSource = true,
        });

        var recipe = Row(rows, "Crafting-D_ProcessorRecipes.json", "Super_Bread");
        Assert.Equal("Super_Bread", RowName(recipe["Outputs"]![0]!["Element"]));
        Assert.Equal("Dough_Bread", RowName(recipe["Inputs"]![0]!["Element"]));
        Assert.Equal("Crafting_Bench", RowName(recipe["RecipeSets"]![0]));
        Assert.Equal("Bread_Blueprint", RowName(recipe["Requirement"]));

        Assert.Equal("Super_Bread", RowName(Row(rows, "Traits-D_Consumable.json", "Super_Bread")["Modifier"]!["Modifier"]));
        var buff = Row(rows, "Modifiers-D_ModifierStates.json", "Super_Bread");
        Assert.Equal("""NSLOCTEXT("D_ModifierStates", "Super_Bread-ModifierName", "Super Bread")""", buff["ModifierName"]!.GetValue<string>());
        Assert.NotNull(buff["GrantedStats"]);
    }

    [Fact]
    public void Recipe_KnownFromTheStart_WhenNotUnlockedWithTheOriginal()
    {
        var rows = ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Food_Bread", NewName = "Super_Bread", DisplayName = "Super Bread", Obtain = ObtainMethod.Recipe,
        });

        Assert.Equal("None", RowName(Row(rows, "Crafting-D_ProcessorRecipes.json", "Super_Bread")["Requirement"]));
    }

    [Fact]
    public void NewRowNames_AvoidRowsTheGameOrTheModAlreadyHas()
    {
        var mod = new ExmodPackage { Name = "m", Author = "a", Version = "1", Description = "", FileName = "m" };
        ItemCloner.AddToPackage(mod, ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Envirosuit", NewName = "Dragon_Suit", DisplayName = "Dragon Suit", OwnCopyFields = new HashSet<string> { "Itemable" },
        }));

        Assert.Throws<ArgumentException>(() => ItemCloner.Build(_game, new ItemCloneRequest { SourceItem = "Envirosuit", NewName = "Food_Bread", DisplayName = "x" }));
        Assert.Throws<ArgumentException>(() => ItemCloner.Build(_game, new ItemCloneRequest { SourceItem = "Envirosuit", NewName = "Dragon_Suit", DisplayName = "x" }, mod));

        // The bread copy wants "Item_Food_Bread"-style naming; a second suit with its itemable taken gets a suffix.
        var second = ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Envirosuit", NewName = "Dragon_Suit_Mk2", DisplayName = "Mk2", OwnCopyFields = new HashSet<string> { "Itemable" },
        }, mod);
        Assert.Equal("Item_Dragon_Suit_Mk2", RowName(Row(second, "Items-D_ItemsStatic.json", "Dragon_Suit_Mk2")["Itemable"]));
    }

    [Fact]
    public void AddToPackage_MergesIntoExistingTables_AndKeepsEndOfModLast()
    {
        var mod = new ExmodPackage
        {
            Name = "m", Author = "a", Version = "1", Description = "", FileName = "m",
            Rows = [new ExmodFileRow { CurrentFile = "Traits-D_Itemable.json", FileItems = [new ExmodFileItem { Name = "Item_Fiber" }] }, new ExmodFileRow { CurrentFile = "EndOfMod" }],
        };

        ItemCloner.AddToPackage(mod, ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Envirosuit", NewName = "Dragon_Suit", DisplayName = "Dragon Suit", OwnCopyFields = new HashSet<string> { "Itemable" },
        }));

        Assert.Equal("EndOfMod", mod.Rows[^1].CurrentFile);
        Assert.Equal(["Item_Fiber", "Item_Dragon_Suit"], mod.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Select(i => i.Name));
    }

    [Fact]
    public void Lists_ItemsWithTheirInGameNames_BenchesAndWorkshopTabs()
    {
        Assert.Contains(ItemCloner.ListItems(_game), c => c is { Name: "Envirosuit", DisplayName: "Xigo Envirosuit" });
        Assert.Contains("Kitchen_Stove", ItemCloner.ListBenches(_game));
        Assert.Equal(["Workshop_Envirosuits"], ItemCloner.ListWorkshopTrees(_game));
    }

    [Fact]
    public void ItemTemplates_HavePresets_AndTheMapIconTemplateIsAValidRow()
    {
        Assert.Equal("Workshop_Envirosuits", ModTemplateContent.ItemPresetFor(IcarusStarlink.Core.Library.ModTemplate.Envirosuit)!.WorkshopTree);
        Assert.True(ModTemplateContent.ItemPresetFor(IcarusStarlink.Core.Library.ModTemplate.ConsumableWithBuff)!.CopyBuff);
        Assert.Null(ModTemplateContent.ItemPresetFor(IcarusStarlink.Core.Library.ModTemplate.Blank));

        var item = ModTemplateContent.Create(IcarusStarlink.Core.Library.ModTemplate.MapIcon, "My \"Base\" Marker", "Me").Rows.Single().FileItems.Single();
        Assert.Equal("My_Base_Marker", item.Name);
        Assert.Equal("""NSLOCTEXT("D_MapIcons", "My_Base_Marker-HoverName", "My Base Marker")""", item.Fields["HoverName"]!.GetValue<string>());
        Assert.Empty(ModTemplateContent.Create(IcarusStarlink.Core.Library.ModTemplate.Envirosuit, "Suit", "Me").Rows);
    }

    /// <summary>Row names ignore case in game: "envirosuit" is the game's "Envirosuit".</summary>
    [Fact]
    public void NewName_ThatDiffersFromAGameItemOnlyInCase_IsTaken()
    {
        Assert.Throws<ArgumentException>(() => ItemCloner.Build(_game, new ItemCloneRequest { SourceItem = "Envirosuit", NewName = "envirosuit", DisplayName = "x" }));
    }
}
