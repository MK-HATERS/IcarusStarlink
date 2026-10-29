using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>Rename this row: a row the mod adds gets a new name and the mod's links to it follow; game rows and clashes are refused.</summary>
public sealed class RowRenamerTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "RowRenamer_" + Guid.NewGuid().ToString("N"));

    public RowRenamerTests()
    {
        Write("Items/D_ItemsStatic.json", """
            {"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"},"Meshable":{"RowName":"None","DataTableName":"D_Meshable"}},"Rows":[
              {"Name":"Fiber","Itemable":{"RowName":"Item_Fiber"}}
            ]}
            """);
        Write("Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{"Weight":0},"Rows":[{"Name":"Item_Fiber","Weight":10}]}""");
        Write("Traits/D_Meshable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Mesh_Fiber"}]}""");
        Write("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{"Outputs":[]},"Rows":[{"Name":"Fiber_Recipe"}]}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private void Write(string relative, string json)
    {
        var path = Path.Combine(_data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>A new item: its static row links its own Itemable (plain RowName, table from Defaults); its recipe names it with DataTableName; a mesh link with the same name points elsewhere.</summary>
    private static ExmodPackage NewItemMod() => ExmodJson.Parse("""
        {"name":"Dragon","author":"Me","version":"1","description":"","fileName":"Dragon","Rows":[
          {"CurrentFile":"Items-D_ItemsStatic.json","File_Items":[
            {"Name":"Dragon_Suit","Itemable":{"RowName":"item_dragon_suit"},"Meshable":{"RowName":"Item_Dragon_Suit"}}]},
          {"CurrentFile":"Traits-D_Itemable.json","File_Items":[
            {"Name":"Item_Dragon_Suit","Weight":5},
            {"Name":"Item_Other","Weight":1}]},
          {"CurrentFile":"Crafting-D_ProcessorRecipes.json","File_Items":[
            {"Name":"Dragon_Recipe","Outputs":[{"Element":{"RowName":"Item_Dragon_Suit","DataTableName":"D_Itemable"},"Count":1}]}]},
          {"CurrentFile":"EndOfMod","File_Items":[]}]}
        """);

    [Fact]
    public void Rename_MovesTheRowAndEveryLinkToItInItsTable()
    {
        var mod = NewItemMod();
        var game = new GameTableIndex(_data);

        var result = RowRenamer.Rename(mod, game, "Traits-D_Itemable.json", "Item_Dragon_Suit", "Item_Wyvern_Suit");

        Assert.Equal(2, result.LinksMoved);
        var items = mod.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems;
        Assert.Contains(items, i => i.Name == "Item_Wyvern_Suit" && i.Fields["Weight"]!.GetValue<int>() == 5);
        Assert.DoesNotContain(items, i => i.Name == "Item_Dragon_Suit");
        var suit = mod.Rows.Single(r => r.CurrentFile == "Items-D_ItemsStatic.json").FileItems.Single();
        Assert.Equal("Item_Wyvern_Suit", suit.Fields["Itemable"]!["RowName"]!.GetValue<string>());
        // Same name, other table (D_Meshable): not a link to this row, left alone.
        Assert.Equal("Item_Dragon_Suit", suit.Fields["Meshable"]!["RowName"]!.GetValue<string>());
        var output = (JsonArray)mod.Rows.Single(r => r.CurrentFile == "Crafting-D_ProcessorRecipes.json").FileItems.Single().Fields["Outputs"]!;
        Assert.Equal("Item_Wyvern_Suit", output[0]!["Element"]!["RowName"]!.GetValue<string>());
        Assert.Equal("D_Itemable", output[0]!["Element"]!["DataTableName"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Item_Fiber", "The game already has a row")]   // a game row, other case too
    [InlineData("item_fiber", "The game already has a row")]
    [InlineData("ITEM_OTHER", "This mod already has a row")]  // another row of the mod
    [InlineData("Item Wyvern", "letters, digits and _")]
    [InlineData("None", "'None'")]
    [InlineData("", "Type a name")]
    public void Problem_RefusesClashesAndBadNames(string newName, string expected)
    {
        var problem = RowRenamer.Problem(NewItemMod(), new GameTableIndex(_data), "Traits-D_Itemable.json", "Item_Dragon_Suit", newName);

        Assert.Contains(expected, problem);
    }

    /// <summary>The mod changes a game row by its name: renaming it would add a new row and leave the game's as it is.</summary>
    [Fact]
    public void AGameRow_CantBeRenamed()
    {
        var mod = NewItemMod();
        mod.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Add(new ExmodFileItem { Name = "item_fiber", Fields = new() { ["Weight"] = 1 } });
        var game = new GameTableIndex(_data);

        Assert.Contains("game's own row", RowRenamer.RowProblem(game, "Traits-D_Itemable.json", "item_fiber"));
        Assert.Throws<InvalidOperationException>(() => RowRenamer.Rename(mod, game, "Traits-D_Itemable.json", "item_fiber", "Item_Mine"));
        Assert.Null(RowRenamer.Problem(mod, game, "Traits-D_Itemable.json", "Item_Dragon_Suit", "item_dragon_SUIT")); // only the spelling: fine
    }
}
