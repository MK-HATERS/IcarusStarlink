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

    private const string ContentPath = @"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Content";

    [Fact]
    public void ResolveWin64Folder_ReturnsSiblingBinariesWin64()
    {
        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64", Ue4ssGamePaths.ResolveWin64Folder(ContentPath));
    }

    [Fact]
    public void ResolveDwmapiPath_ReturnsWin64RootDwmapi()
    {
        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\dwmapi.dll", Ue4ssGamePaths.ResolveDwmapiPath(ContentPath));
    }

    [Fact]
    public void ResolveLoaderDllPath_ReturnsNestedUe4ssDll()
    {
        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\ue4ss\UE4SS.dll", Ue4ssGamePaths.ResolveLoaderDllPath(ContentPath));
    }

    [Fact]
    public void ResolveLoaderLogPath_ReturnsNestedUe4ssLog()
    {
        Assert.Equal(@"E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\ue4ss\UE4SS.log", Ue4ssGamePaths.ResolveLoaderLogPath(ContentPath));
    }
}
