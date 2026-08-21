using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests;

public class Ue4ssGamePathsTests
{
    [Fact]
    public void ResolveModsFolder_ContentPathWithNoTrailingSlash_ReturnsSiblingBinariesModsPath()
    {
        var result = Ue4ssGamePaths.ResolveModsFolder(@"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Content");

        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\ue4ss\Mods", result);
    }

    [Fact]
    public void ResolveModsFolder_ContentPathWithTrailingSlash_ReturnsSameResult()
    {
        var result = Ue4ssGamePaths.ResolveModsFolder(@"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Content\");

        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\ue4ss\Mods", result);
    }
}
