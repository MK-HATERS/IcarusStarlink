using IcarusStarlink.Core.IO;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>"Which item is this row?": the mod editor's list, "Not in this mod yet" and Find the broken mod name rows by the game item they belong to.</summary>
public sealed class GameItemNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "GameItemNames_" + Guid.NewGuid().ToString("N"));
    private readonly GameItemNames _names;

    public GameItemNamesTests()
    {
        MiniGameData.Write(_root);
        _names = new GameItemNames(new GameTableIndex(_root));
    }

    public void Dispose() => Directories.TryDelete(_root);

    /// <summary>Regression: a template named unlike its item (Stone_Furnace makes Kit_Stone_Furnace) was looked up by its own name and found no item.</summary>
    [Fact]
    public void ATemplate_IsTheItemItsStaticDataNames()
    {
        Assert.Equal("Kit_Stone_Furnace", _names.ItemFor("Items-D_ItemTemplate.json", "Stone_Furnace"));
        Assert.Equal("Stone Furnace", _names.DisplayName("Kit_Stone_Furnace"));
    }

    [Fact]
    public void ATemplateNamedLikeItsItem_StillFindsIt()
    {
        Assert.Equal("Envirosuit", _names.ItemFor("Items-D_ItemTemplate.json", "Envirosuit"));
        Assert.Equal("Envirosuit", _names.ItemFor("Items-D_ItemsStatic.json", "Envirosuit"));
    }
}
