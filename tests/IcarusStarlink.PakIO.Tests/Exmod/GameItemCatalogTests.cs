using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>The Editor page's item list: categories from the game's own icon folders, what each item has, where it's made, and which items a mod changes.</summary>
public sealed class GameItemCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "GameItemCatalog_" + Guid.NewGuid().ToString("N"));
    private readonly GameTableIndex _game;

    public GameItemCatalogTests()
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

    private GameItemInfo Item(string name) => GameItemCatalog.List(_game).Single(i => i.Name == name);

    private static ExmodPackage ModWith(string file, string row) => new()
    {
        Name = "m", Author = "a", Version = "1", Description = "", FileName = "m",
        Rows = [new ExmodFileRow { CurrentFile = file, FileItems = [new ExmodFileItem { Name = row, Fields = { ["Weight"] = 1 } }] }, new ExmodFileRow { CurrentFile = "EndOfMod" }],
    };

    /// <summary>Real icon paths from the 2026-11-03 game data; the small folders join a bigger category (Voxels are the ores).</summary>
    [Theory]
    [InlineData("/Game/Assets/2DArt/UI/Items/Item_Icons/Armour/ITEM_Envirosuit_1.ITEM_Envirosuit_1", "Armour")]
    [InlineData("/Game/Assets/2DArt/UI/Items/Item_Icons/Consumeables/ITEM_Bread.ITEM_Bread", "Consumables")]
    [InlineData("/Game/Assets/2DArt/UI/Items/Item_Icons/Voxels/ITEM_Stone.ITEM_Stone", "Resources")]
    [InlineData("/Game/Assets/2DArt/UI/Items/Item_Icons/LegendaryWeapons/ITEM_X.ITEM_X", "Weapons")]
    [InlineData("/Game/Assets/2DArt/UI/Items/Item_Icons/SomethingNew/ITEM_X.ITEM_X", "Other")]
    [InlineData("/Game/Assets/2DArt/UI/Icons/Icon_Blueprints.Icon_Blueprints", "Other")]
    [InlineData(null, "Other")]
    public void Category_IsTheGamesOwnIconFolder(string? iconPath, string category)
    {
        Assert.Equal(category, GameItemCatalog.CategoryOf(iconPath));
    }

    [Fact]
    public void EveryItem_IsListed_WithItsNamesAndDescription()
    {
        var items = GameItemCatalog.List(_game);

        Assert.Equal(["Envirosuit", "Food_Bread", "Kit_Stone_Furnace", "Meta_Carbon_Chest_Alpha"], items.Select(i => i.Name));
        Assert.Equal("Xigo Envirosuit", Item("Envirosuit").Item.Title);
        Assert.Equal("A suit.", Item("Envirosuit").Description);
    }

    /// <summary>A recipe names the item's template (Kit_Stone_Furnace is made as "stone_furnace"), and its bench is where it's made.</summary>
    [Fact]
    public void RecipesAndBenches_AreFoundThroughTheItemsTemplate()
    {
        var furnace = Item("Kit_Stone_Furnace");
        Assert.Contains(GameItemCatalog.RecipeKey, furnace.Has);
        Assert.Equal(["Crafting_Bench"], furnace.Benches);

        var suit = Item("Envirosuit");
        Assert.DoesNotContain(GameItemCatalog.RecipeKey, suit.Has);
        Assert.Contains(GameItemCatalog.WorkshopKey, suit.Has);
        Assert.Contains("Armour", suit.Has);
        Assert.Contains("Decayable", suit.Has);
        Assert.Empty(suit.Benches);
    }

    [Fact]
    public void BenchNames_ReadAsPlainWords()
    {
        Assert.Equal("Crafting bench", GameItemCatalog.BenchName(_game, "Crafting_Bench"));
        Assert.Equal("By hand (Character)", GameItemCatalog.BenchName(_game, "Character"));
    }

    /// <summary>A mod changes an item when it touches any row the Editor shows for it, the buff its food gives included.</summary>
    [Fact]
    public void ItemsChangedBy_AModTouchingAnyOfTheItemsRows()
    {
        var items = GameItemCatalog.List(_game);

        Assert.Equal(["Envirosuit"], GameItemCatalog.ItemsChangedBy(items, ModWith("Traits-D_Itemable.json", "item_envirosuit")));
        Assert.Equal(["Food_Bread"], GameItemCatalog.ItemsChangedBy(items, ModWith("Modifiers-D_ModifierStates.json", "Bread")));
        Assert.Equal(["Kit_Stone_Furnace"], GameItemCatalog.ItemsChangedBy(items, ModWith("Crafting-D_ProcessorRecipes.json", "Stone_Furnace")));
        Assert.Empty(GameItemCatalog.ItemsChangedBy(items, null));
    }
}
