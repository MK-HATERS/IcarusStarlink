using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

/// <summary>What of the game's UE4SS goes onto a server (local and FTP pick the same way), and the one shared folder copy.</summary>
public sealed class Ue4ssServerFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Ue4ssServerFiles", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string relative, string text = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>The game's own logs and crash dumps used to be uploaded over the server's; its Mods folder is never part of the loader.</summary>
    [Fact]
    public void LoaderFiles_SkipLogsDumpsAndTheModsFolder()
    {
        Write(@"ue4ss\UE4SS.dll");
        Write(@"ue4ss\UE4SS-settings.ini");
        Write(@"ue4ss\UE4SS.log");
        Write(@"ue4ss\crash_2026.dmp");
        Write(@"ue4ss\UE4SS_Signatures\FName.lua");
        Write(@"ue4ss\Mods\mods.txt");
        Write(@"ue4ss\Mods\Foo\Scripts\main.lua");

        Assert.Equal(
            ["UE4SS-settings.ini", "UE4SS.dll", @"UE4SS_Signatures\FName.lua"],
            Ue4ssServerFiles.LoaderFiles(Path.Combine(_root, "ue4ss")));
    }

    /// <summary>Only mods UE4SS starts in the game go to a server: not switched-off ones, not framework mods, not folders that aren't mods.</summary>
    [Fact]
    public void ModsOnInGame_FollowsUe4ssStartRules()
    {
        Write(@"Mods\mods.txt", "OnByList : 1\r\nOffByList : 0\r\nshared : 1\r\nBPModLoaderMod : 1\r\n");
        Write(@"Mods\OnByList\Scripts\main.lua");
        Write(@"Mods\OffByList\Scripts\main.lua");
        Write(@"Mods\OnByFile\Scripts\main.lua");
        Write(@"Mods\OnByFile\enabled.txt", "");
        Write(@"Mods\Unlisted\Scripts\main.lua");
        Write(@"Mods\BPModLoaderMod\Scripts\main.lua");
        Write(@"Mods\shared\Types.lua");

        Assert.Equal(["OnByFile", "OnByList"], Ue4ssServerFiles.ModsOnInGame(Path.Combine(_root, "Mods")));
    }

    /// <summary>
    /// Regression: only a fixed list of names counted as UE4SS's own mods, so a built-in a newer
    /// UE4SS build lists in its mods.json was offered for the server as one of the player's mods.
    /// </summary>
    [Fact]
    public void ModsOnInGame_ABuiltInTheInstalledBuildListsInModsJson_IsNotOffered()
    {
        Write(@"Mods\mods.txt", "NewBuiltIn : 1\r\nMine : 1\r\n");
        Write(@"Mods\NewBuiltIn\Scripts\main.lua");
        Write(@"Mods\Mine\Scripts\main.lua");
        Write(@"Mods\mods.json", """[{"mod_name":"NewBuiltIn","mod_enabled":true}]""");

        Assert.Equal(["Mine"], Ue4ssServerFiles.ModsOnInGame(Path.Combine(_root, "Mods")));
    }

    /// <summary>A missing or broken mods.json still knows every build's own mods by name, and never reads a mods.json from the current folder.</summary>
    [Fact]
    public void FrameworkModsIn_NoOrBrokenModsJson_IsTheKnownNames()
    {
        Assert.Equal(Ue4ssFrameworkMods.Known, Ue4ssFrameworkMods.In(null));
        Assert.Equal(Ue4ssFrameworkMods.Known, Ue4ssFrameworkMods.In(""));
        Assert.Equal(Ue4ssFrameworkMods.Known, Ue4ssFrameworkMods.In(Path.Combine(_root, "NoSuchFolder")));
        Write(@"Mods\mods.json", "{corrupt");
        Assert.Equal(Ue4ssFrameworkMods.Known, Ue4ssFrameworkMods.In(Path.Combine(_root, "Mods")));
        Write(@"Mods\mods.json", """{"mod_name":"NotAnArray"}""");
        Assert.Equal(Ue4ssFrameworkMods.Known, Ue4ssFrameworkMods.In(Path.Combine(_root, "Mods")));
        Assert.Contains("ActorDumperMod", Ue4ssFrameworkMods.Known);
        Assert.Contains("jsbLuaProfilerMod", Ue4ssFrameworkMods.Known);
        Assert.Contains("shared", Ue4ssFrameworkMods.Known);
    }

    [Fact]
    public void DirectoriesCopy_CopiesNestedAndEmptyFoldersAndKeepsWhatIsThere()
    {
        Write(@"src\a.txt", "new");
        Write(@"src\sub\b.txt", "b");
        Directory.CreateDirectory(Path.Combine(_root, "src", "empty"));
        Write(@"dst\a.txt", "old");
        Write(@"dst\keep.txt", "keep");

        Directories.Copy(Path.Combine(_root, "src"), Path.Combine(_root, "dst"));

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "dst", "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(_root, "dst", "sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(_root, "dst", "empty")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "dst", "keep.txt")));
    }
}
