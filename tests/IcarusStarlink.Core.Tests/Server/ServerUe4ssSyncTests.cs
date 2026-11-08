using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>
/// Comparing a UE4SS mod's game copy with its server copy. On a real
/// server, files the mods write while running (remembered.txt, logs, caches, .bak) made five
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

    /// <summary>A file only the server has (state a mod writes there) is the server's: no "differs", kept on update.</summary>
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

    /// <summary>
    /// ArcFission Library's state.lua, status.lua and registry.txt (and core_*.txt) are the game's
    /// own session, written while it runs. They used to be copied onto the server, where a stale
    /// state.lua names a data pak the server never built. Never copied, the
    /// server's own kept, never a difference; the library's code and core_off.txt still go.
    /// </summary>
    [Fact]
    public void CopyToServer_ArcFissionLibrarysRuntimeState_IsntCopied_AndTheServersOwnStays()
    {
        var install = Path.Combine(_root, "install");
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(install);
        var gameMods = Path.Combine(_root, "game");
        Mod("game", "ArcFissionLibrary", ("Scripts/main.lua", "local LIBRARY_VERSION = \"1.3.0\""), ("state.lua", "return { gen = 7 }"),
            ("status.lua", "return { state = \"ok\" }"), ("registry.txt", "WirelessPower|0.9.5|E:/game|1|1"),
            ("core_queue.txt", "session|1"), ("core_running.txt", "WirelessPower|1|ok"), ("core_off.txt", ""));

        // New to the server: none of the game's runtime state goes along.
        ServerUe4ssSync.CopyToServer(gameMods, "ArcFissionLibrary", install, Path.Combine(_root, "backups"));
        var library = Path.Combine(serverMods, "ArcFissionLibrary");
        Assert.Equal(["core_off.txt", Path.Combine("Scripts", "main.lua")], Directory.GetFiles(library, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(library, f)).Where(f => f != "enabled.txt").Order(StringComparer.OrdinalIgnoreCase));

        // An update: the server's own state stays as it was, and it isn't a difference.
        File.WriteAllText(Path.Combine(library, "state.lua"), "return { gen = 2 }");
        File.WriteAllText(Path.Combine(library, "status.lua"), "return { state = \"restart\" }");
        Assert.Equal(ServerModState.Same, ServerUe4ssSync.Compare(gameMods, serverMods, new HashSet<string>()).Single().State);
        File.WriteAllText(Path.Combine(gameMods, "ArcFissionLibrary", "Scripts", "main.lua"), "local LIBRARY_VERSION = \"1.3.1\"");

        ServerUe4ssSync.CopyToServer(gameMods, "ArcFissionLibrary", install, Path.Combine(_root, "backups"));

        Assert.Equal("local LIBRARY_VERSION = \"1.3.1\"", File.ReadAllText(Path.Combine(library, "Scripts", "main.lua")));
        Assert.Equal("return { gen = 2 }", File.ReadAllText(Path.Combine(library, "state.lua")));
        Assert.Equal("return { state = \"restart\" }", File.ReadAllText(Path.Combine(library, "status.lua")));
        Assert.False(File.Exists(Path.Combine(library, "registry.txt")));
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

    /// <summary>The table used to say "Same as your game" while the server ran other settings. The shipped defaults aren't a setting; line endings don't count.</summary>
    [Fact]
    public void Compare_SameFiles_DifferentConfig_NamesTheConfig()
    {
        Mod("game", "Tools", ("Scripts/main.lua", "code"), ("config.lua", "return { a = 1 }\r\n"), ("config.default.lua", "return { a = 0 }"));
        Mod("server", "Tools", ("Scripts/main.lua", "code"), ("config.lua", "return { a = 9 }\n"), ("config.default.lua", "return { a = 5 }"));
        var game = Path.Combine(_root, "game");
        var server = Path.Combine(_root, "server");

        var row = Assert.Single(ServerUe4ssSync.Compare(game, server, new HashSet<string>()));
        Assert.Equal(ServerModState.Same, row.State);
        Assert.Equal(["config.lua"], row.DifferingConfigs);

        File.WriteAllText(Path.Combine(server, "Tools", "config.lua"), "return { a = 1 }\n");
        Assert.False(ServerUe4ssSync.Compare(game, server, new HashSet<string>()).Single().ConfigDiffers);
    }

    /// <summary>
    /// Seen live: Pet Commander writes Invulnerable.ini (the world's tames) beside its
    /// config on the server, so the row read "different settings" and Match settings had nothing to
    /// copy. A config-looking file only the server has is the server's own; a config the game has
    /// and the server lacks is a difference.
    /// </summary>
    [Fact]
    public void Compare_AConfigFileOnlyTheServerHas_IsTheServersOwn_NotADifference()
    {
        Mod("game", "Pets", ("Scripts/main.lua", "code"), ("config.lua", "return { a = 1 }"));
        Mod("server", "Pets", ("Scripts/main.lua", "code"), ("config.lua", "return { a = 1 }"), ("Invulnerable.ini", "338769 = off ; MOA 'Maui'"));
        var game = Path.Combine(_root, "game");
        var server = Path.Combine(_root, "server");

        Assert.False(ServerUe4ssSync.Compare(game, server, new HashSet<string>()).Single().ConfigDiffers);

        Directory.CreateDirectory(Path.Combine(game, "Pets", "config"));
        File.WriteAllText(Path.Combine(game, "Pets", "config", "extra.ini"), "[A]\nX = 1\n");
        Assert.Equal([Path.Combine("config", "extra.ini")], ServerUe4ssSync.Compare(game, server, new HashSet<string>()).Single().DifferingConfigs);
    }

    /// <summary>A config the running server holds open used to make Compare throw and the whole table empty; that one file reads as differing instead.</summary>
    [Fact]
    public void Compare_AConfigHeldOpenByTheServer_ReadsAsDiffering_NotAsAnError()
    {
        Mod("game", "Pets", ("Scripts/main.lua", "code"), ("config.lua", "return { a = 1 }"));
        Mod("server", "Pets", ("Scripts/main.lua", "code"), ("config.lua", "return { a = 1 }"));
        using var held = new FileStream(Path.Combine(_root, "server", "Pets", "config.lua"), FileMode.Open, FileAccess.Read, FileShare.None);

        var row = Assert.Single(ServerUe4ssSync.Compare(Path.Combine(_root, "game"), Path.Combine(_root, "server"), new HashSet<string>()));

        Assert.Equal(["config.lua"], row.DifferingConfigs);
    }

    [Fact]
    public void CopyConfigs_EitherWay_BacksUpTheReplacedFile_AndLeavesTheRestAlone()
    {
        var game = Mod("game", "Tools", ("Scripts/main.lua", "code"), ("config.lua", "game"), ("config.default.lua", "defaults"));
        var server = Mod("server", "Tools", ("Scripts/main.lua", "server code"), ("config.lua", "server"), ("config/extra.ini", "[S]\nX=1\n"));
        var backups = Path.Combine(_root, "backups");

        var written = ServerUe4ssSync.CopyConfigs(game, server, backups);

        Assert.Equal(["config.lua"], written);
        Assert.Equal("game", File.ReadAllText(Path.Combine(server, "config.lua")));
        Assert.Equal("server code", File.ReadAllText(Path.Combine(server, "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(server, "config", "extra.ini")));
        Assert.False(File.Exists(Path.Combine(server, "config.default.lua")));
        var saved = Assert.Single(Directory.GetFiles(Path.Combine(backups, "Tools"), "config.lua", SearchOption.AllDirectories));
        Assert.Equal("server", File.ReadAllText(saved));

        // The other way: config.lua is the same now (not written again); the server-only ini comes over.
        Assert.Equal([Path.Combine("config", "extra.ini")], ServerUe4ssSync.CopyConfigs(server, game, backups));
    }

    /// <summary>
    /// Regression: switching a server mod on wrote only its mods.txt line. UE4SS removes every
    /// space from that line, so a folder named "My Mod" switched off (enabled.txt deleted) and on
    /// again showed On but never started. enabled.txt is looked up by folder name and does start it.
    /// </summary>
    [Fact]
    public void SetEnabled_OffThenOn_WritesEnabledTxt()
    {
        var install = Path.Combine(_root, "install");
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(install);
        Directory.CreateDirectory(Path.Combine(serverMods, "My Mod", "Scripts"));
        File.WriteAllText(Path.Combine(serverMods, "My Mod", "Scripts", "main.lua"), "code");
        File.WriteAllText(Path.Combine(serverMods, "My Mod", "enabled.txt"), "");

        ServerUe4ssSync.SetEnabled(install, "My Mod", false);
        Assert.False(File.Exists(Path.Combine(serverMods, "My Mod", "enabled.txt")));
        ServerUe4ssSync.SetEnabled(install, "My Mod", true);

        Assert.True(File.Exists(Path.Combine(serverMods, "My Mod", "enabled.txt")));
    }

    /// <summary>A mod new to the server, with a space in its folder name, is switched on in a way UE4SS reads: enabled.txt, not only a mods.txt line.</summary>
    [Fact]
    public void CopyToServer_NewModWithASpaceInItsName_GetsEnabledTxt()
    {
        var install = Path.Combine(_root, "install");
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(install);
        Mod("game", "Minimap and Map Icons", ("Scripts/main.lua", "code"));

        ServerUe4ssSync.CopyToServer(Path.Combine(_root, "game"), "Minimap and Map Icons", install, Path.Combine(_root, "backups"));

        Assert.True(File.Exists(Path.Combine(serverMods, "Minimap and Map Icons", "enabled.txt")));
        Assert.True(Ue4ssModsTxt.Load(Path.Combine(serverMods, "mods.txt")).IsEnabled("Minimap and Map Icons"));
    }

    /// <summary>
    /// Regression: the old copy was moved aside INSIDE ue4ss\Mods, and the backup step (copy it to
    /// the backups, then delete it) ran before the mod's on/off state was put back. When that step
    /// threw (backups drive full, a file locked), the update had happened but the page said it
    /// failed, a mod switched off on the server was on (through the game copy's enabled.txt), and
    /// "Pets.updating-…" stayed in Mods where UE4SS could start it as a second copy.
    /// </summary>
    [Fact]
    public void CopyToServer_BackupStepFails_KeepsOffState_AndLeavesNoStartableLeftoverInMods()
    {
        var install = Path.Combine(_root, "install");
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(install);
        Mod("game", "Pets", ("Scripts/main.lua", "new"), ("enabled.txt", ""));
        Directory.CreateDirectory(Path.Combine(serverMods, "Pets", "Scripts"));
        File.WriteAllText(Path.Combine(serverMods, "Pets", "Scripts", "main.lua"), "old");
        File.WriteAllText(Path.Combine(serverMods, "mods.txt"), "Pets : 0\r\n");
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "removed-ue4ss"), "a file where the backups folder should be");

        string? note = null;
        var error = Record.Exception(() => note = ServerUe4ssSync.CopyToServer(Path.Combine(_root, "game"), "Pets", install, backups));

        Assert.Null(error);
        Assert.NotNull(note);
        Assert.Contains("Pets was updated", note);
        Assert.Equal("new", File.ReadAllText(Path.Combine(serverMods, "Pets", "Scripts", "main.lua")));
        Assert.False(IcarusStarlink.Core.Ue4ss.Ue4ssModStart.Starts(serverMods, "Pets", IcarusStarlink.Core.Ue4ss.Ue4ssModStart.ReadList(serverMods)));
        Assert.Empty(Directory.GetDirectories(serverMods, "*.updating-*"));
        var aside = Assert.Single(Directory.GetDirectories(Path.Combine(Path.GetDirectoryName(serverMods)!, "Starlink-updating"), "Pets.updating-*"));
        Assert.Equal("old", File.ReadAllText(Path.Combine(aside, "Scripts", "main.lua")));
    }

    /// <summary>The game's own log at a mod's top level stays home, like the FTP sync leaves it.</summary>
    [Fact]
    public void CopyToServer_NewMod_LeavesTheGamesLogAtHome()
    {
        var install = Path.Combine(_root, "install");
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(install);
        Mod("game", "Pets", ("Scripts/main.lua", "code"), ("Pets.log", "what it logged in the game"));

        ServerUe4ssSync.CopyToServer(Path.Combine(_root, "game"), "Pets", install, Path.Combine(_root, "backups"));

        Assert.True(File.Exists(Path.Combine(serverMods, "Pets", "Scripts", "main.lua")));
        Assert.False(File.Exists(Path.Combine(serverMods, "Pets", "Pets.log")));
    }
}
