using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>
/// Comparing a UE4SS mod's game copy with its server copy (user report 2026-10-23). On the user's
/// real server, files the mods write while running (remembered.txt, logs, caches, .bak) made five
/// identical mods read as "differs"; those are the copy's own, never a difference, and kept on update.
/// </summary>
public sealed class ServerUe4ssSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ServerUe4ssSync", Guid.NewGuid().ToString("N"));

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

    private string Mod(string side, string name, params (string Path, string Text)[] files)
    {
        var folder = Path.Combine(_root, side, name);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(folder, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return folder;
    }

    [Fact]
    public void SameContent_IgnoresConfigAndWhatTheModWritesWhileRunning()
    {
        var game = Mod("game", "Tools", ("Scripts/main.lua", "local VERSION = \"1.0\""), ("config.lua", "return { a = 1 }"),
            ("remembered.txt", "game state"), ("Tools.log", "game log"), ("data.cache", "x"), ("config.lua.bak", "old"));
        var server = Mod("server", "Tools", ("Scripts/main.lua", "local VERSION = \"1.0\""), ("config.lua", "return { a = 9 }"),
            ("remembered.txt", "server state"), ("Tools.log", "server log"), ("enabled.txt", ""));

        Assert.True(ServerUe4ssSync.SameContent(game, server));

        File.WriteAllText(Path.Combine(server, "Scripts", "main.lua"), "local VERSION = \"1.0\" -- patched");
        Assert.False(ServerUe4ssSync.SameContent(game, server));
    }

    [Fact]
    public void Compare_VersionsDecideTheDirection()
    {
        Mod("game", "Older", ("Scripts/main.lua", "local VERSION = \"1.2.0\""));
        Mod("server", "Older", ("Scripts/main.lua", "local VERSION = \"1.10.0\""));
        Mod("game", "Newer", ("Scripts/main.lua", "local VERSION = \"2.0\""));
        Mod("server", "Newer", ("Scripts/main.lua", "local VERSION = \"1.9\""));
        Mod("game", "GameOnly", ("Scripts/main.lua", "x"));
        Mod("server", "ServerOnly", ("Scripts/main.lua", "x"));

        var rows = ServerUe4ssSync.Compare(Path.Combine(_root, "game"), Path.Combine(_root, "server"), new HashSet<string>())
            .ToDictionary(r => r.Name, r => r.State);

        Assert.Equal(ServerModState.ServerNewer, rows["Older"]); // 1.10 > 1.2, not a text compare
        Assert.Equal(ServerModState.ServerOutOfDate, rows["Newer"]);
        Assert.Equal(ServerModState.NotOnServer, rows["GameOnly"]);
        Assert.Equal(ServerModState.OnlyOnServer, rows["ServerOnly"]);
    }

    [Fact]
    public void CopyToServer_KeepsTheServersConfigAndStateFiles_AndBacksUpTheOldCopy()
    {
        var install = Path.Combine(_root, "install");
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(install);
        Mod("game", "Pets", ("Scripts/main.lua", "new code"), ("config.lua", "game config"), ("remembered.txt", "game pets"));
        Directory.CreateDirectory(Path.Combine(serverMods, "Pets", "Scripts"));
        File.WriteAllText(Path.Combine(serverMods, "Pets", "Scripts", "main.lua"), "old code");
        File.WriteAllText(Path.Combine(serverMods, "Pets", "config.lua"), "server config");
        File.WriteAllText(Path.Combine(serverMods, "Pets", "remembered.txt"), "server pets");
        var backups = Path.Combine(_root, "backups");

        ServerUe4ssSync.CopyToServer(Path.Combine(_root, "game"), "Pets", install, backups);

        Assert.Equal("new code", File.ReadAllText(Path.Combine(serverMods, "Pets", "Scripts", "main.lua")));
        Assert.Equal("server config", File.ReadAllText(Path.Combine(serverMods, "Pets", "config.lua")));
        Assert.Equal("server pets", File.ReadAllText(Path.Combine(serverMods, "Pets", "remembered.txt")));
        var saved = Assert.Single(Directory.GetDirectories(Path.Combine(backups, "removed-ue4ss")));
        Assert.Equal("old code", File.ReadAllText(Path.Combine(saved, "Scripts", "main.lua")));
        Assert.Empty(Directory.GetDirectories(serverMods, "*.updating-*"));
    }

    /// <summary>Review 2026-10-23: a file only the server has (state a mod writes there) is the server's: no "differs", kept on update.</summary>
    [Fact]
    public void FilesOnlyOnTheServer_AreNotADifference_AndStayOnUpdate()
    {
        var install = Path.Combine(_root, "install");
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(install);
        Mod("game", "Pets", ("Scripts/main.lua", "code"));
        Directory.CreateDirectory(Path.Combine(serverMods, "Pets", "Scripts"));
        Directory.CreateDirectory(Path.Combine(serverMods, "Pets", "data"));
        File.WriteAllText(Path.Combine(serverMods, "Pets", "Scripts", "main.lua"), "code");
        File.WriteAllText(Path.Combine(serverMods, "Pets", "data", "players.json"), "{\"server\":1}");

        var row = Assert.Single(ServerUe4ssSync.Compare(Path.Combine(_root, "game"), serverMods, new HashSet<string>()));
        Assert.Equal(ServerModState.Same, row.State);

        File.WriteAllText(Path.Combine(_root, "game", "Pets", "Scripts", "main.lua"), "new code");
        ServerUe4ssSync.CopyToServer(Path.Combine(_root, "game"), "Pets", install, Path.Combine(_root, "backups"));

        Assert.Equal("new code", File.ReadAllText(Path.Combine(serverMods, "Pets", "Scripts", "main.lua")));
        Assert.Equal("{\"server\":1}", File.ReadAllText(Path.Combine(serverMods, "Pets", "data", "players.json")));
    }

    /// <summary>A file missing from the server is a difference.</summary>
    [Fact]
    public void AFileMissingFromTheServer_IsADifference()
    {
        Mod("game", "Tools", ("Scripts/main.lua", "code"), ("Scripts/extra.lua", "more"));
        Mod("server", "Tools", ("Scripts/main.lua", "code"));

        var row = Assert.Single(ServerUe4ssSync.Compare(Path.Combine(_root, "game"), Path.Combine(_root, "server"), new HashSet<string>()));
        Assert.Equal(ServerModState.ServerOutOfDate, row.State);
    }
}
