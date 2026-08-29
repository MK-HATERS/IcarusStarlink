using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class GameAssetExtensionsTests
{
    [Theory]
    [InlineData("BP/Building/BP_Wall.uasset")]
    [InlineData("BP/Building/BP_Wall.uexp")]
    [InlineData("BP/Building/BP_Wall.ubulk")]
    [InlineData("BP/Building/BP_Wall.UASSET")]
    public void IsRealGameAsset_RealUnrealAssetExtension_ReturnsTrue(string relativePath) =>
        Assert.True(GameAssetExtensions.IsRealGameAsset(relativePath));

    [Theory]
    [InlineData("Readme.txt")]
    [InlineData("ImageOnly.png")]
    [InlineData("Banner.PNG")]
    [InlineData("preview.jpg")]
    public void IsRealGameAsset_NonGameFile_ReturnsFalse(string relativePath) =>
        Assert.False(GameAssetExtensions.IsRealGameAsset(relativePath));
}
