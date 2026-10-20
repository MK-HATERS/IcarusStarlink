using System.Text;
using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>Links to rows that don't exist, and item rows nothing uses — each case shaped on a real Nexus mod.</summary>
public sealed class ReferenceCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "RefCheck_" + Guid.NewGuid().ToString("N"));
    private readonly GameTableIndex _game;

    public ReferenceCheckerTests()
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

    private static ExmodPackage Mod(params (string File, string Name, string FieldsJson)[] items) => new()
    {
        Name = "m", Author = "a", Version = "1", Description = "", FileName = "m",
        Rows = [.. items.GroupBy(i => i.File).Select(g => new ExmodFileRow
        {
            CurrentFile = g.Key,
            FileItems = [.. g.Select(i => new ExmodFileItem
            {
                Name = i.Name,
                Fields = JsonNode.Parse(i.FieldsJson)!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone()),
            })],
        })],
    };

    /// <summary>BetterSupplies: its packages use the mesh "Item_Meta_Ration"; the game's is "Mesh_Meta_Ration".</summary>
    [Fact]
    public void ATopLevelLinkToAMissingRow_IsReported_WithTheTableFromDefaults()
    {
        var report = ReferenceChecker.Check(Mod(("Items-D_ItemsStatic.json", "BS_Carepackage", """{"Meshable":{"RowName":"Item_Meta_Ration"},"Itemable":{"RowName":"Item_Bread"}}""")), _game);

        var broken = Assert.Single(report.Broken);
        Assert.Equal(("BS_Carepackage", "Meshable", "D_Meshable", "Item_Meta_Ration"), (broken.RowName, broken.FieldPath, broken.TargetTable, broken.MissingRow));
        Assert.Contains("doesn't exist", broken.Describe());
    }

    /// <summary>Pickup and Move writes "FirePit"; the game's row is "Firepit". Unreal row names ignore case, so it works.</summary>
    [Fact]
    public void RowNamesCompareIgnoringCase_LikeTheGame()
    {
        Assert.True(ReferenceChecker.Check(Mod(("Items-D_ItemsStatic.json", "Food_Bread", """{"Meshable":{"RowName":"FIREPIT"}}""")), _game).IsClean);
    }

    /// <summary>"None" is Unreal's "no row", and Unreal names ignore case: "none" is no link either, not a link to a row called none.</summary>
    [Fact]
    public void ALinkToNone_InAnyCase_IsNoLink()
    {
        Assert.True(ReferenceChecker.Check(Mod(("Items-D_ItemsStatic.json", "Food_Bread", """{"Meshable":{"RowName":"none"},"Armour":{"RowName":"NONE"}}""")), _game).IsClean);
    }

    [Fact]
    public void NestedLinks_UseTheirOwnDataTableName_OrTheDefaultsShape()
    {
        var report = ReferenceChecker.Check(Mod(
            ("Crafting-D_ProcessorRecipes.json", "Bread", """{"Outputs":[{"Element":{"RowName":"Food_Bred","DataTableName":"D_ItemTemplate"},"Count":1}]}"""),
            ("Traits-D_Consumable.json", "Food_Bread", """{"Modifier":{"Modifier":{"RowName":"Missing_Buff"},"ModifierLifetime":900}}""")), _game);

        Assert.Equal(["Outputs[0].Element|D_ItemTemplate|Food_Bred", "Modifier.Modifier|D_ModifierStates|Missing_Buff"],
            report.Broken.Select(b => $"{b.FieldPath}|{b.TargetTable}|{b.MissingRow}"));
    }

    [Fact]
    public void RowsTheModAddsItself_CountAsExisting_AndNoneMeansEmpty()
    {
        var report = ReferenceChecker.Check(Mod(
            ("Items-D_ItemsStatic.json", "Dragon_Suit", """{"Itemable":{"RowName":"Item_Dragon_Suit"},"Armour":{"RowName":"None"}}"""),
            ("Traits-D_Itemable.json", "Item_Dragon_Suit", """{"Weight":10}""")), _game);

        Assert.True(report.IsClean);
    }

    /// <summary>StacksX10 still sets the weight of items the game dropped.</summary>
    [Fact]
    public void AnItemTraitRowTheGameLacks_AndNothingUses_IsReportedAsUnused()
    {
        var report = ReferenceChecker.Check(Mod(("Traits-D_Itemable.json", "Item_Crab_Head", """{"Weight":5}""")), _game);

        Assert.Equal("Item_Crab_Head", Assert.Single(report.Unused).RowName);
        Assert.Contains("renamed or removed", report.Unused[0].Describe());
    }

    [Fact]
    public void AnUnlinkedRow_TheModsOwnBlueprintNames_IsNotUnused()
    {
        var mod = Mod(("Traits-D_Itemable.json", "Item_Used_By_Blueprint", """{"Weight":5}"""));
        var blueprint = Encoding.ASCII.GetBytes("\0\0Item_Used_By_Blueprint\0\0");

        Assert.True(ReferenceChecker.Check(mod, _game, () => [blueprint]).IsClean);
    }

    [Fact]
    public void NonTraitTables_AndNonGameTables_AreNotCalledUnused()
    {
        var report = ReferenceChecker.Check(Mod(
            ("Crafting-D_ProcessorRecipes.json", "My_Recipe", """{"Inputs":[]}"""),
            ("Mods-D_NotAGameTable.json", "X", """{"Thing":{"RowName":"Nope"}}""")), _game);

        Assert.True(report.IsClean);
    }

    /// <summary>
    /// What the new-item wizard builds must pass its own check. The cloned bread recipe keeps the
    /// game's Dough_Bread input, which the mini data leaves out, so that one link is allowed.
    /// </summary>
    [Fact]
    public void AClonedItem_AddsNoBrokenLinksOrUnusedRows()
    {
        var mod = Mod();
        ItemCloner.AddToPackage(mod, ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Food_Bread", NewName = "Super_Bread", DisplayName = "Super Bread",
            OwnCopyFields = new HashSet<string> { "Itemable", "Consumable" }, CopyBuff = true,
            Obtain = ObtainMethod.Recipe, UnlockWithSource = true,
        }));
        ItemCloner.AddToPackage(mod, ItemCloner.Build(_game, new ItemCloneRequest
        {
            SourceItem = "Envirosuit", NewName = "Dragon_Suit", DisplayName = "Dragon Suit",
            OwnCopyFields = new HashSet<string> { "Itemable", "Armour" }, Obtain = ObtainMethod.Workshop, NewWorkshopTab = "Dragon Gear",
        }, mod));

        var report = ReferenceChecker.Check(mod, _game);

        Assert.All(report.Broken, b => Assert.Equal("Dough_Bread", b.MissingRow));
        Assert.Empty(report.Unused);
    }
}
