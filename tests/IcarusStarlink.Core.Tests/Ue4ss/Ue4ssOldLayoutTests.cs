using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

/// <summary>The old UE4SS setup (files directly in Binaries\Win64) and its move into the ue4ss folder. All in temp folders.</summary>
public sealed class Ue4ssOldLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void Seed(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void SeedOldSetup(string win64)
    {
        Seed(Path.Combine(win64, "dwmapi.dll"), "old dwmapi");
        Seed(Path.Combine(win64, "UE4SS.dll"), "old dll");
        Seed(Path.Combine(win64, "UE4SS-settings.ini"), "my settings");
        Seed(Path.Combine(win64, "Mods", "mods.txt"), "StorageLink : 1\n");
        Seed(Path.Combine(win64, "Mods", "mods.json"), "[]");
        Seed(Path.Combine(win64, "Mods", "Keybinds", "Scripts", "main.lua"), "-- old keybinds");
        Seed(Path.Combine(win64, "Mods", "StorageLink", "Scripts", "main.lua"), "-- my mod");
    }

    [Fact]
    public void Find_NothingOrTheUe4ssFolderLayout_IsNotAnOldSetup()
    {
        var win64 = Path.Combine(_root, "Win64");
        Assert.Null(Ue4ssOldLayout.Find(win64)); // no folder at all

        Seed(Path.Combine(win64, "dwmapi.dll"), "x");
        Seed(Path.Combine(win64, "ue4ss", "UE4SS.dll"), "x");
        Seed(Path.Combine(win64, "ue4ss", "Mods", "mods.txt"), "A : 1");

        Assert.Null(Ue4ssOldLayout.Find(win64));
        Assert.False(Ue4ssOldLayout.MoveIntoUe4ssFolder(win64, Path.Combine(_root, "Backups")).Changed);
        Assert.False(Directory.Exists(Path.Combine(_root, "Backups")));
    }

    [Fact]
    public void MoveIntoUe4ssFolder_MovesThePlayersPart_AndUndoPutsItAllBack()
    {
        var win64 = Path.Combine(_root, "Win64");
        SeedOldSetup(win64);

        var moved = Ue4ssOldLayout.MoveIntoUe4ssFolder(win64, Path.Combine(_root, "Backups"));

        Assert.True(moved.Changed);
        Assert.Equal(["StorageLink"], moved.Mods);
        Assert.Equal("-- my mod", File.ReadAllText(Path.Combine(win64, "ue4ss", "Mods", "StorageLink", "Scripts", "main.lua")));
        Assert.Equal("StorageLink : 1\n", File.ReadAllText(Path.Combine(win64, "ue4ss", "Mods", "mods.txt")));
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(win64, "ue4ss", "UE4SS-settings.ini")));
        // UE4SS's own old files don't come along: the new build brings its own.
        Assert.False(Directory.Exists(Path.Combine(win64, "ue4ss", "Mods", "Keybinds")));
        Assert.False(File.Exists(Path.Combine(win64, "ue4ss", "Mods", "mods.json")));
        Assert.False(File.Exists(Path.Combine(win64, "UE4SS.dll")));
        Assert.False(Directory.Exists(Path.Combine(win64, "Mods")));
        Assert.Equal("-- old keybinds", File.ReadAllText(Path.Combine(moved.BackupFolder!, "Mods", "Keybinds", "Scripts", "main.lua")));

        Assert.Empty(moved.Undo());

        Assert.Equal("old dll", File.ReadAllText(Path.Combine(win64, "UE4SS.dll")));
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(win64, "UE4SS-settings.ini")));
        Assert.Equal("StorageLink : 1\n", File.ReadAllText(Path.Combine(win64, "Mods", "mods.txt")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(win64, "Mods", "mods.json")));
        Assert.Equal("-- old keybinds", File.ReadAllText(Path.Combine(win64, "Mods", "Keybinds", "Scripts", "main.lua")));
        Assert.Equal("-- my mod", File.ReadAllText(Path.Combine(win64, "Mods", "StorageLink", "Scripts", "main.lua")));
        Assert.False(Directory.Exists(Path.Combine(win64, "ue4ss", "Mods", "StorageLink")));
    }

    /// <summary>A server set up the old way keeps its mods, their on/off state and its settings when the game's UE4SS is copied onto it.</summary>
    [Fact]
    public void InstallUe4ssFromGame_ServerWithAnOldSetup_MovesItsModsAndSettingsIntoTheUe4ssFolder()
    {
        var gameWin64 = Path.Combine(_root, "Game", "Icarus", "Binaries", "Win64");
        Seed(Path.Combine(gameWin64, "dwmapi.dll"), "4.0 dwmapi");
        Seed(Path.Combine(gameWin64, "ue4ss", "UE4SS.dll"), "4.0 dll");
        Seed(Path.Combine(gameWin64, "ue4ss", "UE4SS-settings.ini"), "game settings");
        Seed(Path.Combine(gameWin64, "ue4ss", "Mods", "mods.txt"), "Keybinds : 1\n");
        Seed(Path.Combine(gameWin64, "ue4ss", "Mods", "Keybinds", "Scripts", "main.lua"), "-- 4.0 keybinds");
        Seed(Path.Combine(gameWin64, "ue4ss", Ue4ssInstalledBuild.MarkerFileName), "4.0.0\n30afe5ff\n");
        var server = Path.Combine(_root, "Server");
        var serverWin64 = DedicatedServerLayout.Win64Folder(server);
        SeedOldSetup(serverWin64);

        Assert.True(ServerModFiles.InstallUe4ssFromGame(gameWin64, server, Path.Combine(_root, "ServerBackups")));

        var serverUe4ss = Path.Combine(serverWin64, "ue4ss");
        Assert.True(DedicatedServerLayout.IsUe4ssInstalled(server));
        Assert.Equal("4.0 dll", File.ReadAllText(Path.Combine(serverUe4ss, "UE4SS.dll")));
        Assert.Equal("-- my mod", File.ReadAllText(Path.Combine(serverUe4ss, "Mods", "StorageLink", "Scripts", "main.lua")));
        Assert.Equal("StorageLink : 1\n", File.ReadAllText(Path.Combine(serverUe4ss, "Mods", "mods.txt")));
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(serverUe4ss, "UE4SS-settings.ini")));
        Assert.Equal("-- 4.0 keybinds", File.ReadAllText(Path.Combine(serverUe4ss, "Mods", "Keybinds", "Scripts", "main.lua")));
        Assert.False(File.Exists(Path.Combine(serverWin64, "UE4SS.dll")));
        Assert.Equal(("4.0.0", "30afe5ff"), Ue4ssInstalledBuild.Read(serverUe4ss));
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "ServerBackups", "UE4SS-Loader"), "old-layout_*"));
    }
}
