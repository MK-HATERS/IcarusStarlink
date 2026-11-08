using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class Ue4ssLogStartsTests
{
    // Lines captured from a real UE4SS.log (F: backup of 2026-10-18): the C++ pass first, then the
    // Lua pass, with a Lua print glued onto the front of a start line (line 1024 there).
    private static readonly string[] RealLog =
    [
        @"[2026-10-18 12:12:32.9890220] Starting mods (from mods.txt (E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\ue4ss\Mods\mods.txt) load order)...",
        @"[2026-10-18 12:12:32.9894847] Starting mods (from enabled.txt (E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\ue4ss\Mods), no defined load order)...",
        "[2026-10-18 12:12:32.9898037] Mod 'IcarusStutterFix' has enabled.txt, starting mod.",
        "[2026-10-18 12:12:32.9907801] Mod 'NoStability' has enabled.txt, starting mod.",
        "[2026-10-18 12:12:37.2993202] Starting callback garbage collector![2026-10-18 12:12:37.3011989] [UE4SS.StaticConstructObject.ObjectSearcherPoolHook] Added posthook with Id '0X9000000000000001'!",
        @"[2026-10-18 12:12:39.2662197] Starting mods (from mods.txt (E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\ue4ss\Mods\mods.txt) load order)...",
        "[2026-10-18 12:12:39.2668006] Starting Lua mod 'CheatManagerEnablerMod'",
        "[2026-10-18 12:12:39.2740198] Mod 'SplitScreenMod' disabled in mods.txt.",
        "[2026-10-18 12:12:39.2912627] Starting Lua mod 'Keybinds'",
        @"[2026-10-18 12:12:39.2921025] Starting mods (from enabled.txt (E:\SteamLibrary\steamapps\common\Icarus\Icarus\Binaries\Win64\ue4ss\Mods), no defined load order)...",
        "[2026-10-18 12:12:39.2925798] Mod 'ArcFissionLibrary' has enabled.txt, starting mod.",
        "[2026-10-18 12:12:39.3176684] [Lua] [MiniMapa] v2.0 carregado. F7 minimapa, F9 icones no mapa, F10 gira o mapa.[2026-10-18 12:12:39.3178100] Mod 'MkHatersAmmoBag' has enabled.txt, starting mod.",
        "[2026-10-18 12:12:39.5051068] Event loop start",
    ];

    private static readonly DateTime LuaPassAt = new(2026, 10, 18, 12, 12, 39, 266, DateTimeKind.Local);

    [Theory]
    [InlineData("IcarusStutterFix")] // C++ pass only
    [InlineData("CheatManagerEnablerMod")] // mods.txt, Lua pass
    [InlineData("arcfissionlibrary")] // enabled.txt, any case
    [InlineData("MkHatersAmmoBag")] // glued onto a Lua print
    public void Parse_ModsTheLogStarted_AreStarted(string name)
    {
        var log = Ue4ssLogStarts.Parse(RealLog);

        Assert.Equal(Ue4ssLastRun.Started, log.StateFor(name, LuaPassAt.AddMinutes(-5)));
    }

    [Theory]
    [InlineData("SplitScreenMod")] // disabled in mods.txt
    [InlineData("NotMentioned")] // both passes ran and it isn't there
    public void Parse_ModsTheLogDidNotStart_AreNotStarted(string name)
    {
        var log = Ue4ssLogStarts.Parse(RealLog);

        Assert.Equal(Ue4ssLastRun.NotStarted, log.StateFor(name, LuaPassAt.AddMinutes(-5)));
    }

    [Fact]
    public void Parse_TheLastWordWins_AcrossPasses()
    {
        var log = Ue4ssLogStarts.Parse(
        [
            "[2026-10-18 12:00:00.0000000] Starting mods (from mods.txt (x) load order)...",
            "[2026-10-18 12:00:00.1000000] Mod 'Both' disabled in mods.txt.",
            "[2026-10-18 12:00:05.0000000] Starting mods (from mods.txt (x) load order)...",
            "[2026-10-18 12:00:05.1000000] Starting Lua mod 'Both'",
        ]);

        Assert.Equal(Ue4ssLastRun.Started, log.StateFor("Both", new DateTime(2026, 10, 18, 11, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void Parse_OnlyTheFirstPass_DoesNotCallAMissingModNotStarted()
    {
        // The game stopped (or is still starting) before the Lua pass: a Lua mod missing from the
        // log may yet start.
        var log = Ue4ssLogStarts.Parse(RealLog.Take(4));

        Assert.Equal(Ue4ssLastRun.Unknown, log.StateFor("Keybinds", LuaPassAt.AddMinutes(-5)));
        Assert.Equal(Ue4ssLastRun.Started, log.StateFor("IcarusStutterFix", LuaPassAt.AddMinutes(-5)));
    }

    [Fact]
    public void StateFor_AModChangedAfterTheLastRun_IsNotRunSince_NeverDidntStart()
    {
        var log = Ue4ssLogStarts.Parse(RealLog);

        Assert.Equal(Ue4ssLastRun.NotRunSince, log.StateFor("NotMentioned", LuaPassAt.AddMinutes(1)));
        Assert.Equal(Ue4ssLastRun.NotRunSince, log.StateFor("Keybinds", LuaPassAt.AddMinutes(1)));
    }

    [Fact]
    public void NoLog_IsNotRunSince()
    {
        Assert.Equal(Ue4ssLastRun.NotRunSince, Ue4ssLogStarts.NoLog.StateFor("Keybinds", DateTime.MinValue));
    }

    [Fact]
    public void Read_AMissingFile_IsNoLog_AndAFileIsReadWhileItIsOpenForWriting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "UE4SS.log");
            Assert.Same(Ue4ssLogStarts.NoLog, Ue4ssLogStarts.Read(path));

            // UE4SS keeps the log open for writing while the game runs.
            using (var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)))
            {
                writer.Write(string.Join(Environment.NewLine, RealLog));
                writer.Flush();
                Assert.Equal(Ue4ssLastRun.Started, Ue4ssLogStarts.Read(path).StateFor("Keybinds", LuaPassAt.AddMinutes(-5)));
            }

            // Cached by write time: a changed file is read again.
            File.WriteAllLines(path, RealLog.Take(4));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(Ue4ssLastRun.Unknown, Ue4ssLogStarts.Read(path).StateFor("Keybinds", LuaPassAt.AddMinutes(-5)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void LastChanged_IsTheNewestOfTheFolder_ItsEnabledTxt_AndModsTxt()
    {
        var mods = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        var modFolder = Path.Combine(mods, "Keybinds");
        Directory.CreateDirectory(modFolder);
        try
        {
            var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var enabled = Path.Combine(modFolder, "enabled.txt");
            File.WriteAllText(enabled, "");
            File.WriteAllText(Path.Combine(mods, "mods.txt"), "Keybinds : 1");
            foreach (var file in new[] { enabled, Path.Combine(mods, "mods.txt") })
            {
                File.SetCreationTime(file, old);
                File.SetLastWriteTime(file, old);
            }

            Directory.SetCreationTime(modFolder, old);
            Directory.SetLastWriteTime(modFolder, old);
            Assert.Equal(old, Ue4ssLogStarts.LastChanged(mods, "Keybinds"));

            var later = old.AddDays(1);
            File.SetLastWriteTime(enabled, later);
            Assert.Equal(later, Ue4ssLogStarts.LastChanged(mods, "Keybinds"));

            var latest = old.AddDays(2);
            File.SetLastWriteTime(Path.Combine(mods, "mods.txt"), latest);
            Assert.Equal(latest, Ue4ssLogStarts.LastChanged(mods, "Keybinds"));
        }
        finally
        {
            Directory.Delete(mods, recursive: true);
        }
    }
}
