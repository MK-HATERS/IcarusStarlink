using System.IO.Compression;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>SteamCMD parsing, mods.txt editing, log events, backups and server mod files — inputs copied from a real Icarus dedicated server install (2026-08/10).</summary>
public sealed class ServerPhase23Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- Saving on stop ----

    [Fact]
    public void ShutdownNotes_SayWhatAStopDoesToTheWorld()
    {
        Assert.Equal("It saves the world as it shuts down.", ServerShutdownNotes.DescribeSaving(new DedicatedServerSettings { SaveGameOnExit = true }));
        Assert.Contains("is lost", ServerShutdownNotes.DescribeSaving(new DedicatedServerSettings { SaveGameOnExit = false, GameSaveFrequency = 3 }));
        Assert.Contains("game's default", ServerShutdownNotes.DescribeSaving(new DedicatedServerSettings()));
        Assert.Contains("game's default", ServerShutdownNotes.DescribeSaving(null));

        var restarts = new DedicatedServerProfile { AutoUpdate = true };
        Assert.NotNull(ServerShutdownNotes.AutomationWarning(restarts, new DedicatedServerSettings { SaveGameOnExit = false }));
        Assert.Null(ServerShutdownNotes.AutomationWarning(new DedicatedServerProfile(), new DedicatedServerSettings { SaveGameOnExit = false }));
        Assert.Null(ServerShutdownNotes.AutomationWarning(restarts, new DedicatedServerSettings { SaveGameOnExit = true }));
    }

    // ---- Backup folders and retention ----

    /// <summary>The user's real folder is "Server1_dc7ea824" for "Server #1": renaming the profile must find it by the id and carry it over.</summary>
    [Fact]
    public void BackupFolder_IsFoundByProfileId_AndRenamedToTheCurrentName()
    {
        var root = Path.Combine(_root, "Server");
        var profile = new DedicatedServerProfile { Id = Guid.Parse("dc7ea824-fb24-470b-9951-2536eaf85d38"), Name = "Server #1" };
        Assert.Equal(Path.Combine(root, "Server1_dc7ea824"), ServerBackups.ResolveFolder(root, profile));
        Directory.CreateDirectory(Path.Combine(root, "Server1_dc7ea824"));
        File.WriteAllText(Path.Combine(root, "Server1_dc7ea824", "2026-10-16_171804_manual.zip"), "");
        Directory.CreateDirectory(Path.Combine(root, "Other_00000000"));

        profile.Name = "Friends";
        var folder = ServerBackups.ResolveFolder(root, profile);

        Assert.Equal(Path.Combine(root, "Friends_dc7ea824"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "2026-10-16_171804_manual.zip")));
        Assert.False(Directory.Exists(Path.Combine(root, "Server1_dc7ea824")));
        Assert.True(Directory.Exists(Path.Combine(root, "Other_00000000")));
    }

    [Theory]
    [InlineData("2026-10-16_171804_manual", "manual")]
    [InlineData("2026-10-16_171804_before-update_2", "before-update")]
    [InlineData("2026-10-16_171804_daily", "daily")]
    [InlineData("my own copy", null)]
    public void ReasonOf_ReadsTheKindFromTheName(string name, string? reason) => Assert.Equal(reason, ServerBackups.ReasonOf(name));

    [Fact]
    public void Prune_KeepsTheNewestOfEachKind_AndWhatItIsToldToKeep()
    {
        Directory.CreateDirectory(_root);
        string Make(string name, int minutesAgo)
        {
            var path = Path.Combine(_root, name + ".zip");
            File.WriteAllText(path, "");
            File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-minutesAgo));
            return path;
        }

        var oldManual = Make("2026-10-01_000000_manual", 50);
        Make("2026-10-02_000000_manual", 40);
        Make("2026-10-03_000000_manual", 30);
        var restoredFrom = Make("2026-09-01_000000_daily", 90);
        Make("2026-10-03_000000_daily", 20);
        Make("2026-10-04_000000_daily", 10);
        var onlyRestore = Make("2026-09-01_000000_before-restore", 100);
        var foreign = Make("hand made", 200);

        var deleted = ServerBackups.Prune(_root, keepPerReason: 2, neverDelete: [restoredFrom]);

        Assert.Equal([oldManual], deleted);
        Assert.True(File.Exists(onlyRestore));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(restoredFrom));
        Assert.Empty(ServerBackups.Prune(_root, keepPerReason: 0));
    }

    // ---- SteamCMD ----

    [Fact]
    public void InstallArguments_AlwaysPassTheBranchAndPutTheFolderBeforeLogin()
    {
        Assert.Equal("+force_install_dir \"G:\\Icarus Server\" +login anonymous +app_update 2089300 -beta public validate +quit",
            SteamCmd.BuildInstallArguments(@"G:\Icarus Server\", "public"));
        Assert.Contains("-beta experimental", SteamCmd.BuildInstallArguments(@"G:\S", "Experimental"));
        Assert.Contains("-beta public", SteamCmd.BuildInstallArguments(@"G:\S", "nonsense"));
    }

    [Theory]
    [InlineData(" Update state (0x61) downloading, progress: 39.84 (3912345600 / 9820000000)", 39.84, "downloading")]
    [InlineData(" Update state (0x81) verifying update, progress: 100.00 (10443211060 / 10443211060)", 100.0, "verifying update")]
    public void ProgressLines_GivePercentAndPhase(string line, double pct, string phase)
    {
        Assert.Equal(pct, SteamCmd.ParseProgressPercent(line));
        Assert.Equal(phase, SteamCmd.ParseProgressPhase(line));
    }

    /// <summary>SteamCMD's own logs\console_log.txt (a real line) — the source of progress when its stdout is held back.</summary>
    [Fact]
    public void ConsoleLogLines_LoseTheirTimestampAndStillParse()
    {
        var text = SteamCmd.StripLogTimestamp("[2026-10-15 20:17:40]  Update state (0x61) downloading, progress: 40.08 (4191739133 / 10457140040)");

        Assert.Equal(40.08, SteamCmd.ParseProgressPercent(text));
        Assert.StartsWith("Update state", text.Trim());
        Assert.Equal("Loading Steam API...OK", SteamCmd.StripLogTimestamp("Loading Steam API...OK"));
    }

    [Fact]
    public void SuccessAndErrorLines_AreRecognised()
    {
        Assert.True(SteamCmd.IsSuccessLine("Success! App '2089300' fully installed."));
        Assert.True(SteamCmd.IsErrorLine("ERROR! Failed to install app '2089300' (No subscription)"));
        Assert.True(SteamCmd.IsErrorLine("Error! App '2089300' state is 0x202 after update job."));
        Assert.False(SteamCmd.IsErrorLine("Loading Steam API...OK"));
        Assert.Null(SteamCmd.ParseProgressPercent("Loading Steam API...OK"));
    }

    /// <summary>The real manifest from the user's server: buildid 25621760, empty UserConfig (= public).</summary>
    [Fact]
    public void AppManifest_GivesTheInstalledBuildAndBranch()
    {
        const string realAcf = "\"AppState\"\n{\n\t\"appid\"\t\t\"2089300\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"buildid\"\t\t\"25621760\"\n\t\"TargetBuildID\"\t\t\"25621760\"\n\t\"UserConfig\"\n\t{\n\t}\n}\n";
        Assert.Equal((25621760, "public"), SteamCmd.ReadAppManifest(realAcf));

        var experimental = realAcf.Replace("\"UserConfig\"\n\t{\n\t}", "\"UserConfig\"\n\t{\n\t\t\"BetaKey\"\t\t\"experimental\"\n\t}");
        Assert.Equal((25621760, "experimental"), SteamCmd.ReadAppManifest(experimental));
    }

    /// <summary>The real manifest line: VDF doubles every backslash.</summary>
    [Fact]
    public void LauncherPath_IsReadAndUnescaped()
    {
        const string realLine = "\"AppState\"\n{\n\t\"LauncherPath\"\t\t\"C:\\\\Personal\\\\steamcmd\\\\steamcmd.exe\"\n}";
        Assert.Equal(@"C:\Personal\steamcmd\steamcmd.exe", SteamCmd.ReadLauncherPath(realLine));
        Assert.Null(SteamCmd.ReadLauncherPath("\"AppState\" { }"));
    }

    [Fact]
    public void AppInfo_GivesTheLatestBuildPerBranch()
    {
        const string output = """
            "2089300"
            {
                "depots"
                {
                    "branches"
                    {
                        "public"
                        {
                            "buildid"		"25700000"
                            "timeupdated"		"1790000000"
                        }
                        "experimental"
                        {
                            "buildid"		"25711111"
                            "description"		"Experimental"
                        }
                    }
                }
            }
            """;
        Assert.Equal(25700000, SteamCmd.ParseLatestBuildId(output, "public"));
        Assert.Equal(25711111, SteamCmd.ParseLatestBuildId(output, "experimental"));
        Assert.Null(SteamCmd.ParseLatestBuildId("no branches here", "public"));
    }

    // ---- ini: the game's own key spelling ----

    [Fact]
    public void Ini_ReadsAndWritesLargeStonesRespawnTheWayTheGameDoes()
    {
        var ini = ServerSettingsIni.Parse("[/Script/Icarus.DedicatedServerSettings]\r\nFiberFoliageRespawn=False\r\nLargeStonesRespawn=False\r\nGameSaveFrequency=3.000000\r\nSaveGameOnExit=True\r\n");
        var s = ini.Read();
        Assert.False(s.LargeStonesRespawn);
        Assert.Equal(3, s.GameSaveFrequency);

        s.LargeStonesRespawn = true;
        ini.Write(s);
        Assert.Contains("LargeStonesRespawn=True", ini.ToString());
        Assert.DoesNotContain("LargeStoneRespawn=", ini.ToString());

        Assert.True(ServerSettingsIni.Parse("[/Script/Icarus.DedicatedServerSettings]\nLargeStoneRespawn=True\n").Read().LargeStonesRespawn);
    }

    // ---- mods.txt ----

    /// <summary>The stock UE4SS 3.0.1 mods.txt shape from the real server: a new mod goes above the keybinds block, which must stay last.</summary>
    [Fact]
    public void ModsTxt_InsertsAboveTheKeybindsBlockAndEditsInPlace()
    {
        var txt = Ue4ssModsTxt.Parse("CheatManagerEnablerMod : 1\r\nLineTraceMod : 0\r\n\r\n\r\n; Built-in keybinds, do not move up!\r\nKeybinds : 1\r\n");

        txt.Set("StorageLink", true);
        txt.Set("LineTraceMod", true);

        Assert.Equal("CheatManagerEnablerMod : 1\r\nLineTraceMod : 1\r\nStorageLink : 1\r\n\r\n\r\n; Built-in keybinds, do not move up!\r\nKeybinds : 1\r\n", txt.ToString());
        Assert.True(txt.IsEnabled("storagelink"));
        Assert.Null(txt.IsEnabled("Nope"));

        txt.Remove("StorageLink");
        Assert.Null(txt.IsEnabled("StorageLink"));
        Assert.Equal(3, txt.Entries.Count);
    }

    // ---- log events ----

    private const string Login = "[2026.08.12-17.31.50:975][102]LogNet: Login request: ?password=x?Name=MK-HATERS userId: Steam:UNKNOWN [0x1100001351D72E3] platform: Steam";
    private const string Join = "[2026.08.12-17.31.51:477][117]LogNet: Join succeeded: MK-HATERS";
    private const string Close = "[2026.08.12-17.32.04:482][428]LogNet: UNetConnection::Close: [UNetConnection] RemoteAddr: 76561198851388131:17777, Name: SteamNetConnection_2147481943, Driver: GameNetDriver SteamNetDriver_2147481984, IsServer: YES, PC: BP_IcarusPlayerControllerServer_C_2147481936, Owner: BP_IcarusPlayerControllerServer_C_2147481936, UniqueId: Steam:UNKNOWN [0x1100001351D72E3], Channels: 7, Time: 2026.08.12-17.32.04";
    private const string TravelLogin = "[2026.08.12-17.32.20:715][550]LogNet: Login request: ?password=x?game=/Game/BP/Systems/BP_IcarusGameMode.BP_IcarusGameMode_C?Name=MK-HATERS?prospectId=Hard Elysium JULY userId: Steam:UNKNOWN [0x1100001351D72E3] platform: Steam";
    private const string TravelJoin = "[2026.08.12-17.32.23:280][627]LogNet: Join succeeded: MK-HATERS";
    private const string Prospect = "[2026.08.12-17.32.01:410][337]LogLocalSessionSubsystem: Display: SetActiveProspectInfo - ProspectID: Hard Elysium JULY | ProspectDTKey: OpenWorld_Elysium";

    [Fact]
    public void LogEvents_JoinProspectAndATravelThatIsNotALeave()
    {
        var events = new ServerLogEvents();
        Assert.Empty(events.Feed(Login));
        Assert.Equal(ServerLogEventKind.PlayerJoined, Assert.Single(events.Feed(Join)).Kind);
        Assert.Equal(new DateTime(2026, 8, 12, 17, 31, 51, DateTimeKind.Utc), Assert.Single(events.Online).Since);

        var loaded = Assert.Single(events.Feed(Prospect));
        Assert.Equal("Prospect loaded: Hard Elysium JULY (Elysium)", loaded.Text);
        Assert.Equal("Hard Elysium JULY", events.CurrentProspect);

        // Moving from the lobby into the prospect: the lobby connection closes, a new one joins
        // 19 s later. A real server log reported this as "disconnected" every time before the
        // grace period — now nothing is reported and the player never leaves the online list.
        Assert.Empty(events.Feed(Close));
        Assert.Single(events.Online);
        Assert.Empty(events.Feed(TravelLogin));
        Assert.Empty(events.Feed(TravelJoin));
        Assert.Empty(events.ExpireStale(new DateTime(2026, 8, 12, 18, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("MK-HATERS", Assert.Single(events.Online).Name);
    }

    /// <summary>A real leave: no rejoin within the grace period — reported once, at the time the connection closed.</summary>
    [Fact]
    public void LogEvents_AClosedConnectionWithNoRejoinBecomesALeaveAfterTheGrace()
    {
        var events = new ServerLogEvents();
        events.Feed(Login);
        events.Feed(Join);
        events.Feed(Close);

        Assert.Empty(events.ExpireStale(new DateTime(2026, 8, 12, 17, 32, 30, DateTimeKind.Utc)));
        var left = Assert.Single(events.ExpireStale(new DateTime(2026, 8, 12, 17, 33, 30, DateTimeKind.Utc)));
        Assert.Equal(ServerLogEventKind.PlayerLeft, left.Kind);
        Assert.Equal("MK-HATERS disconnected", left.Text);
        Assert.Equal(new DateTime(2026, 8, 12, 17, 32, 4, DateTimeKind.Utc), left.Time);
        Assert.Empty(events.Online);

        // A later log line is enough on its own to expire it too (no timer needed while the log moves).
        var again = new ServerLogEvents();
        again.Feed(Login);
        again.Feed(Join);
        again.Feed(Close);
        var produced = again.Feed("[2026.08.12-17.40.00:000][  0]LogTemp: Display: later line");
        Assert.Equal(ServerLogEventKind.PlayerLeft, Assert.Single(produced).Kind);
    }

    [Fact]
    public void LogEvents_LobbyReturnShutdownAndModPaks()
    {
        var events = new ServerLogEvents();
        events.Feed(Login);
        events.Feed(Join);
        events.Feed(Prospect);

        Assert.Equal(ServerLogEventKind.ReturnedToLobby,
            Assert.Single(events.Feed("[2026.08.12-17.34.20:507][ 30]LogLocalSessionSubsystem: Display: ResetActiveProspect - ProspectID: Hard Elysium JULY | ProspectDTKey: OpenWorld_Elysium")).Kind);
        Assert.Null(events.CurrentProspect);
        Assert.Empty(events.Feed("[2026.08.12-17.34.21:757][  0]LogLocalSessionSubsystem: Display: ResetActiveProspect - ProspectID: NULL | ProspectDTKey: NULL"));

        Assert.Equal("Mod pak loaded: IMM_Merged_Mod_P.pak",
            Assert.Single(events.Feed("LogPakFile: Display: Mounting pak file ../../../Icarus/Content/Paks/mods/IMM_Merged_Mod_P.pak.")).Text);

        var shutdown = Assert.Single(events.Feed("[2026.08.12-17.35.00:235][741]LogCore: Engine exit requested (reason: ConsoleCtrl RequestExit)"));
        Assert.Equal(ServerLogEventKind.ShutdownRequested, shutdown.Kind);
        Assert.Empty(events.Online);
    }

    [Theory]
    [InlineData("LogNet: Login request: ?password=4543?Name=MK-HATERS userId: x", "LogNet: Login request: ?password=***?Name=MK-HATERS userId: x")]
    [InlineData("password=secret at the end", "password=*** at the end")]
    [InlineData("nothing to hide", "nothing to hide")]
    public void Redact_MasksTheJoinPassword(string line, string expected) =>
        Assert.Equal(expected, ServerLogFilter.Redact(line));

    // ---- backups and server mod files ----

    [Fact]
    public void Backup_ZipsPlayerDataSettingsAndListsAndRestoreNeverWritesOutsideSaved()
    {
        var install = Path.Combine(_root, "Server");
        var prospects = DedicatedServerLayout.ProspectsFolder(install, null);
        Directory.CreateDirectory(prospects);
        File.WriteAllText(Path.Combine(prospects, "W.json"), "{}");
        Directory.CreateDirectory(Path.GetDirectoryName(DedicatedServerLayout.SettingsIniPath(install, null))!);
        File.WriteAllText(DedicatedServerLayout.SettingsIniPath(install, null), "[x]");
        File.WriteAllText(DedicatedServerLayout.BanListPath(install, null), "someone");
        var backups = Path.Combine(_root, "Backups");

        var zip = ServerBackups.CreateBackup(install, null, backups, "manual");

        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal(
                ["Config/WindowsServer/ServerSettings.ini", "PlayerBanList.txt", "PlayerData/DedicatedServer/Prospects/W.json"],
                archive.Entries.Select(e => e.FullName).Order().ToList());
        }

        var evil = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("../../escaped.txt").Open());
            w.Write("x");
        }

        ServerBackups.Restore(evil, install, null, backups);
        Assert.False(File.Exists(Path.Combine(install, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.Equal(2, ServerBackups.List(backups).Count);
    }

    [Fact]
    public void RemovePak_MovesItIntoTheBackupsFolder()
    {
        var install = Path.Combine(_root, "Server");
        var mods = DedicatedServerLayout.PakModsFolder(install);
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "IMM_Merged_Mod_P.pak"), [1]);
        var backups = Path.Combine(_root, "Backups");

        var movedTo = ServerModFiles.RemovePak(install, "IMM_Merged_Mod_P.pak", backups);

        Assert.Empty(ServerModFiles.ListPaks(install));
        Assert.True(File.Exists(movedTo));
        Assert.StartsWith(Path.Combine(backups, "removed-paks"), movedTo);
        Assert.Throws<ArgumentException>(() => ServerModFiles.RemovePak(install, "..\\x.pak", backups));
    }

    /// <summary>"." and ".." pass the invalid-character check; removing or copying "the mod" named so would delete the Mods folder or its parent.</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" ")]
    public void Ue4ssModFolderNames_ThatNameAFolderAboveTheMod_AreRefused(string name)
    {
        var install = Path.Combine(_root, "Server");
        var mods = DedicatedServerLayout.Ue4ssModsFolder(install);
        Directory.CreateDirectory(Path.Combine(mods, "KeepMe"));

        Assert.Throws<ArgumentException>(() => ServerModFiles.RemoveUe4ssMod(install, name, Path.Combine(_root, "Backups")));
        Assert.Throws<ArgumentException>(() => ServerModFiles.CopyUe4ssModToServer(Path.Combine(_root, "GameMods"), name, install));

        Assert.True(Directory.Exists(Path.Combine(mods, "KeepMe")));
    }

    /// <summary>Copying a mod the game no longer has used to delete the server's own copy first, then fail.</summary>
    [Fact]
    public void CopyUe4ssModToServer_ModMissingFromTheGame_LeavesTheServersCopyAlone()
    {
        var install = Path.Combine(_root, "Server");
        var serverMod = Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(install), "BetterStacks");
        Directory.CreateDirectory(serverMod);
        File.WriteAllText(Path.Combine(serverMod, "main.lua"), "-- server copy");
        var gameMods = Path.Combine(_root, "GameMods");
        Directory.CreateDirectory(gameMods);

        Assert.Throws<DirectoryNotFoundException>(() => ServerModFiles.CopyUe4ssModToServer(gameMods, "BetterStacks", install));

        Assert.Equal("-- server copy", File.ReadAllText(Path.Combine(serverMod, "main.lua")));
    }

    /// <summary>A blank reason files under "manual" — for the first zip and for a same-second second one alike.</summary>
    [Fact]
    public void CreateBackup_BlankReasonTwiceInOneSecond_BothFileUnderManual()
    {
        var install = Path.Combine(_root, "Server");
        Directory.CreateDirectory(DedicatedServerLayout.SavedFolder(install, null));
        var backups = Path.Combine(_root, "Backups");

        var first = ServerBackups.CreateBackup(install, null, backups, "!!");
        var second = ServerBackups.CreateBackup(install, null, backups, "!!");

        Assert.Equal("manual", ServerBackups.ReasonOf(Path.GetFileNameWithoutExtension(first)));
        Assert.Equal("manual", ServerBackups.ReasonOf(Path.GetFileNameWithoutExtension(second)));
    }

    /// <summary>The merged pak under an earlier name is moved into the backups folder, not deleted, when one is given.</summary>
    [Fact]
    public void InstallPak_WithABackupsFolder_MovesTheLegacyNamedMergedPakThere()
    {
        var install = Path.Combine(_root, "Server");
        var mods = DedicatedServerLayout.PakModsFolder(install);
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "ISL-Merged_P.pak"), [1]);
        var source = Path.Combine(_root, "Staged", "zz_zz_ISL-Merged_9999_P.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [2]);
        var backups = Path.Combine(_root, "Backups");

        ServerModFiles.InstallPak(source, install, manifestFileName: null, backupsFolder: backups);

        Assert.Equal(["zz_zz_ISL-Merged_9999_P.pak"], ServerModFiles.ListPaks(install).Select(p => p.FileName));
        Assert.Single(Directory.GetFiles(Path.Combine(backups, "removed-paks"), "*_ISL-Merged_P.pak"));
    }
}
