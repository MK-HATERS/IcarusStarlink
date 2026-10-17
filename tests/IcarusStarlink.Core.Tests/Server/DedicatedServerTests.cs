using System.Text;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

public sealed class DedicatedServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CommandLine_UsesOnlyDocumentedParametersAndOmitsTheEmptyOnes()
    {
        var profile = new DedicatedServerProfile { InstallFolder = @"F:\IcarusServer", SteamServerName = "Friends \"best\" server", Port = 17777, QueryPort = 27015 };

        Assert.Equal(["-log", "-SteamServerName=\"Friends best server\"", "-PORT=17777", "-QueryPort=27015"], DedicatedServerCommandLine.BuildArguments(profile));

        profile.SavedDirSuffix = "_2";
        profile.LogFileName = "Friends.log";
        profile.Multihome = "192.168.1.20";
        Assert.Equal(
            "\"F:\\IcarusServer\\IcarusServer.exe\" -log -SteamServerName=\"Friends best server\" -PORT=17777 -QueryPort=27015 -MULTIHOME=192.168.1.20 -saveddirsuffix=_2 -LOG=Friends.log",
            DedicatedServerCommandLine.BuildDisplayLine(profile));
    }

    /// <summary>Two profiles on one install are told apart by the suffix on each process's own command line.</summary>
    [Fact]
    public void RunningProcess_IsMatchedToTheProfileBySavedDirSuffix()
    {
        var plain = new DedicatedServerProfile { InstallFolder = @"F:\IcarusServer" };
        var second = new DedicatedServerProfile { InstallFolder = @"F:\IcarusServer", SavedDirSuffix = "_2" };
        const string First = "\"F:\\IcarusServer\\Icarus\\Binaries\\Win64\\IcarusServer-Win64-Shipping.exe\" Icarus -log -SteamServerName=\"A\" -PORT=17777 -QueryPort=27015";
        const string Second = "\"F:\\IcarusServer\\Icarus\\Binaries\\Win64\\IcarusServer-Win64-Shipping.exe\" Icarus -log -PORT=17778 -QueryPort=27016 -SavedDirSuffix=_2 -LOG=B.log";

        Assert.Equal("", DedicatedServerCommandLine.ReadSavedDirSuffix(First));
        Assert.Equal("_2", DedicatedServerCommandLine.ReadSavedDirSuffix(Second));
        Assert.Equal("my world", DedicatedServerCommandLine.ReadSavedDirSuffix("x.exe -saveddirsuffix=\"my world\" -log"));

        Assert.True(DedicatedServerCommandLine.IsThisProfilesProcess(First, plain));
        Assert.False(DedicatedServerCommandLine.IsThisProfilesProcess(Second, plain));
        Assert.True(DedicatedServerCommandLine.IsThisProfilesProcess(Second, second));
        Assert.False(DedicatedServerCommandLine.IsThisProfilesProcess(First, second));
        Assert.True(DedicatedServerCommandLine.IsThisProfilesProcess(null, second), "an unreadable command line can't rule it out");

        // What Start passes is what is read back.
        Assert.True(DedicatedServerCommandLine.IsThisProfilesProcess(DedicatedServerCommandLine.BuildDisplayLine(second), second));
    }

    [Fact]
    public void Sanitize_CapsTheNameAt64Characters() =>
        Assert.Equal(64, DedicatedServerCommandLine.Sanitize(new string('x', 100)).Length);

    [Fact]
    public void Layout_PathsFollowTheOfficialFolderStructure()
    {
        Assert.Equal(@"F:\S\Icarus\Saved\Config\WindowsServer\ServerSettings.ini", DedicatedServerLayout.SettingsIniPath(@"F:\S", null));
        Assert.Equal(@"F:\S\Icarus\Saved_2\Logs\Icarus.log", DedicatedServerLayout.LogPath(@"F:\S", "_2", ""));
        Assert.Equal(@"F:\S\Icarus\Saved\Logs\Mine.log", DedicatedServerLayout.LogPath(@"F:\S", null, "Mine.log"));
        Assert.Equal(@"F:\S\Icarus\Saved\PlayerData\DedicatedServer\Prospects", DedicatedServerLayout.ProspectsFolder(@"F:\S", ""));
        Assert.Equal(@"F:\S\Icarus\Content\Paks\mods", DedicatedServerLayout.PakModsFolder(@"F:\S"));
    }

    /// <summary>People pick the Icarus or Binaries\Win64 subfolder as often as the root.</summary>
    [Fact]
    public void FindInstallRoot_WalksUpToTheFolderWithIcarusServerExe()
    {
        var win64 = Path.Combine(_root, "Icarus", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        File.WriteAllText(Path.Combine(_root, "IcarusServer.exe"), "");

        Assert.Equal(_root, DedicatedServerLayout.FindInstallRoot(win64));
        Assert.Equal(_root, DedicatedServerLayout.FindInstallRoot(_root));
        Assert.Null(DedicatedServerLayout.FindInstallRoot(Path.GetTempPath()));
    }

    [Fact]
    public void ListProspects_NewestFirstWithoutExtension()
    {
        var prospects = DedicatedServerLayout.ProspectsFolder(_root, null);
        Directory.CreateDirectory(prospects);
        File.WriteAllText(Path.Combine(prospects, "Old.json"), "{}");
        File.SetLastWriteTimeUtc(Path.Combine(prospects, "Old.json"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(prospects, "New.json"), "{}");
        File.WriteAllText(Path.Combine(prospects, "New.json.backup"), "{}");

        Assert.Equal(["New", "Old"], DedicatedServerLayout.ListProspects(_root, null));
        Assert.Empty(DedicatedServerLayout.ListProspects(_root, "_nope"));
    }

    [Fact]
    public void LogTailer_ReturnsOnlyNewCompleteLinesAndStartsOverWhenTheFileIsReplaced()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Icarus.log");
        var tailer = new LogTailer(path);
        Assert.Empty(tailer.Poll());

        File.WriteAllText(path, "line one\r\nline two\r\npart");
        Assert.Equal(["line one", "line two"], tailer.Poll());
        Assert.Empty(tailer.Poll());

        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write(Encoding.UTF8.GetBytes("ial line\r\nthree\r\n"));
        }

        Assert.Equal(["partial line", "three"], tailer.Poll());

        // A new server run: Unreal moves the old log aside and starts a fresh, shorter file.
        File.WriteAllText(path, "new run\r\n");
        Assert.Equal(["new run"], tailer.Poll());
    }

    /// <summary>A new run that has already written MORE than the old one by the next poll: the file never shrank, but it starts differently, so it is read from the top.</summary>
    [Fact]
    public void LogTailer_NewRunThatGrewPastTheOldPosition_IsReadFromTheTop()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Icarus.log");
        var tailer = new LogTailer(path);
        File.WriteAllText(path, "Log file open, 10/16/26 17:18:04\r\nold run\r\n");
        Assert.Equal(["Log file open, 10/16/26 17:18:04", "old run"], tailer.Poll());

        File.WriteAllText(path, "Log file open, 10/16/26 21:35:10\r\nnew run line one\r\nnew run line two\r\nnew run line three\r\n");

        Assert.Equal(["Log file open, 10/16/26 21:35:10", "new run line one", "new run line two", "new run line three"], tailer.Poll());

        // Appending to the same run is still just the new lines.
        File.AppendAllText(path, "four\r\n");
        Assert.Equal(["four"], tailer.Poll());
    }

    [Fact]
    public void LogTailer_SeekToEnd_SkipsTheBacklogAndTheMaxBytesCapKeepsTheNewestPart()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Icarus.log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, 100).Select(i => $"old {i}\n")));

        var attached = new LogTailer(path);
        attached.SeekToEnd();
        Assert.Empty(attached.Poll());

        var capped = new LogTailer(path);
        var tail = capped.Poll(maxBytes: 30);
        Assert.Contains("old 99", tail);
        Assert.DoesNotContain("old 0", tail);
    }

    [Theory]
    [InlineData("[2026.10.15-20.11.19:000][  0]LogStreaming: Display: Flushing async loaders.", true)]
    [InlineData("[2026.10.15-20.11.19:000][  0]LogTexture: Display: Building textures", true)]
    [InlineData("[2026.10.15-20.11.41:000][  0]LogNet: Join succeeded: James", false)]
    [InlineData("[2026.10.15-20.11.19:000][  0]LogPakFile: Display: Mounted pak ../Paks/mods/ISL-Merged_P.pak", false)]
    [InlineData("LogIcarus: Error: something broke", false)]
    public void LogFilter_HidesEngineChatterAndKeepsWhatMatters(string line, bool noise) =>
        Assert.Equal(noise, ServerLogFilter.IsNoise(line));
}
