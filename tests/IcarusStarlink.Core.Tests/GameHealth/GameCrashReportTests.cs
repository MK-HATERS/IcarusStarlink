using IcarusStarlink.Core.GameHealth;

namespace IcarusStarlink.Core.Tests.GameHealth;

public class GameCrashReportTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private const string MachineId = "289810A14D926B6BC48AE4A426D364FD";
    private const string LoginId = "289810a14d926b6bc48ae4a426d364fd";
    private const string EpicAccountId = "d985d944a36141fb804fdafbfee1839c";

    /// <summary>A crash folder in the shape Icarus writes (cut down from a real one), ids included.</summary>
    private string MakeCrash(string crashId, DateTime createdAtUtc, string errorMessage, string stack)
    {
        var crashesFolder = Path.Combine(_tempDir, "Crashes");
        var crashDir = Path.Combine(crashesFolder, crashId);
        Directory.CreateDirectory(crashDir);
        File.WriteAllText(Path.Combine(crashDir, "CrashContext.runtime-xml"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <FGenericCrashContext>
                <RuntimeProperties>
                    <CrashGUID>{crashId}</CrashGUID>
                    <CrashType>Crash</CrashType>
                    <ErrorMessage>{errorMessage}</ErrorMessage>
                    <SecondsSinceStart>821</SecondsSinceStart>
                    <EngineVersion>4.27.2-0+++UE4+Release-4.27</EngineVersion>
                    <MachineId>{MachineId}</MachineId>
                    <LoginId>{LoginId}</LoginId>
                    <EpicAccountId>{EpicAccountId}</EpicAccountId>
                    <GameStateName>BP_IcarusGameState_C</GameStateName>
                    <PCallStack>{stack}</PCallStack>
                </RuntimeProperties>
            </FGenericCrashContext>
            """);
        Directory.SetCreationTimeUtc(crashDir, createdAtUtc);
        return crashesFolder;
    }

    private const string Ue4ssStack =
        "wintrust 0x000001d611780000 + 124aa7901 UE4SS 0x00007ffbfc300000 + 2c5680 KERNELBASE 0x00007ffd180e0000 + 1427f";

    private static readonly string[] SessionLog =
    [
        "[2026-11-07 12:25:57.5466194] FArchiveState::ArIsCriticalError = 0x29",
        "[2026-11-07 12:25:59.4544488] Starting Lua mod 'Bags'",
        "[2026-11-07 12:25:59.4572756] [Lua] Failed to expose size of 'class RC::Unreal::FObjectPtrProperty', internal value invalid",
        "[2026-11-07 12:25:59.5000000] [Lua] [Bags] hooked BP_ActionableBehaviour",
        "[2026-11-07 12:26:10.0000000] [Lua] [Bags] reload tick failed: attempt to index a nil value",
        "[2026-11-07 12:26:11.0000000] [Lua] [Bags] Error: Scripts/main.lua:40: bad argument",
        "[2026-11-07 12:26:12.0000000] Error: [Lua::call_function] lua_pcall returned LUA_ERRRUN => ...\\Mods\\Wireless\\Scripts\\main.lua:12: attempt to call a nil value",
        "[2026-11-07 12:26:13.0000000] [NativeBags] failed to find the ammo wheel",
        "[2026-11-07 12:26:14.0000000] [RegisterHook] failed to register hook for Function /Script/Engine.PlayerController:ClientRestart",
        "[2026-11-07 12:26:15.0000000] Mod 'Wireless' has enabled.txt, starting mod.",
    ];

    [Fact]
    public void CountUe4ssModErrors_CountsEachModsErrorLines_AndLeavesOutUe4sssOwnLines()
    {
        var counts = GameCrashReport.CountUe4ssModErrors(SessionLog, knownMods: ["Bags", "NativeBags", "Wireless"]);

        Assert.Equal(
            [new Ue4ssModErrorCount("Bags", 2), new Ue4ssModErrorCount("NativeBags", 1), new Ue4ssModErrorCount("Wireless", 1)],
            counts);
    }

    [Fact]
    public void CountUe4ssModErrors_ABracketThatIsntAModFolder_IsNotCountedAsAMod()
    {
        var counts = GameCrashReport.CountUe4ssModErrors(SessionLog, knownMods: ["Bags", "Wireless"]);

        Assert.DoesNotContain(counts, c => c.ModName is "NativeBags" or "RegisterHook");
    }

    private (string ContentPath, string LogPath) MakeGame(IEnumerable<string> logLines, params string[] modFolders)
    {
        var contentPath = Path.Combine(_tempDir, "Icarus", "Content");
        var loaderFolder = Path.Combine(_tempDir, "Icarus", "Binaries", "Win64", "ue4ss");
        Directory.CreateDirectory(contentPath);
        foreach (var mod in modFolders)
        {
            Directory.CreateDirectory(Path.Combine(loaderFolder, "Mods", mod));
        }

        Directory.CreateDirectory(loaderFolder);
        var logPath = Path.Combine(loaderFolder, "UE4SS.log");
        File.WriteAllLines(logPath, logLines);
        return (contentPath, logPath);
    }

    [Fact]
    public void ReadUe4ssModErrors_ReadsTheCrashedSessionsLog_WithTheModsFolderNames()
    {
        var (contentPath, logPath) = MakeGame(SessionLog, "Bags", "NativeBags", "Wireless");
        var crashAt = new DateTimeOffset(2026, 11, 7, 12, 30, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(logPath, crashAt.UtcDateTime.AddSeconds(-5));

        var counts = GameCrashReport.ReadUe4ssModErrors(contentPath, crashAt);

        Assert.Equal(["Bags", "NativeBags", "Wireless"], counts.Select(c => c.ModName));
    }

    /// <summary>UE4SS rewrites its log at each game start: written well after the crash, it belongs to a later session.</summary>
    [Fact]
    public void ReadUe4ssModErrors_LogFromALaterSession_IsLeftOut()
    {
        var (contentPath, logPath) = MakeGame(SessionLog, "Bags");
        var crashAt = new DateTimeOffset(2026, 11, 7, 12, 30, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(logPath, crashAt.UtcDateTime.AddHours(1));

        Assert.Empty(GameCrashReport.ReadUe4ssModErrors(contentPath, crashAt));
    }

    [Fact]
    public void ReadUe4ssModErrors_NoGameFolderOrNoLog_IsEmpty()
    {
        Assert.Empty(GameCrashReport.ReadUe4ssModErrors(null, DateTimeOffset.UtcNow));
        Assert.Empty(GameCrashReport.ReadUe4ssModErrors(Path.Combine(_tempDir, "Nowhere", "Content"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Describe_CrashThroughUe4ss_SaysItWentThroughUe4ss_AndWhichModsLoggedErrors_WithoutAnyIds()
    {
        var crash = GameSessionHealthCheck.ReadLastCrash(MakeCrash("C1", DateTime.UtcNow, "Unhandled Exception: EXCEPTION_ACCESS_VIOLATION reading address 0x0", Ue4ssStack))!;
        var modErrors = new[] { new Ue4ssModErrorCount("Bags", 2), new Ue4ssModErrorCount("Wireless", 1) };

        var notice = GameCrashReport.Describe(crash, modErrors);
        var report = GameCrashReport.BuildReport(crash, modErrors);

        Assert.Contains("went through UE4SS", notice);
        Assert.Contains("Bags (2 errors), Wireless (1 error)", notice);
        Assert.Contains("EXCEPTION_ACCESS_VIOLATION", notice);
        Assert.DoesNotContain("caused", notice);
        Assert.Contains("Went through UE4SS: yes", report);
        Assert.Contains("Bags: 2", report);
        Assert.Contains("C1", report);
        foreach (var text in new[] { notice, report })
        {
            Assert.DoesNotContain(MachineId, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(EpicAccountId, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Describe_CrashWithoutUe4ss_SaysUe4ssWasntPartOfIt()
    {
        var crash = GameSessionHealthCheck.ReadLastCrash(MakeCrash("C2", DateTime.UtcNow, "Assertion failed", "Icarus-Win64-Shipping 0x00007ff6a0000000 + 1427f"))!;

        var notice = GameCrashReport.Describe(crash, []);

        Assert.False(GameCrashReport.IsGpuDriverCrash(crash));
        Assert.Contains("UE4SS wasn't part of the crash", notice);
        Assert.DoesNotContain("went through UE4SS", notice);
    }

    /// <summary>A graphics driver crash has nothing to do with mods: the notice must not point at them, even when UE4SS is in the stack or mods logged errors.</summary>
    [Fact]
    public void Describe_GraphicsDriverCrash_PointsAtTheDriverNotAtMods()
    {
        var crash = GameSessionHealthCheck.ReadLastCrash(MakeCrash("C3", DateTime.UtcNow,
            "Fatal error: [File:D:\\Build\\D3D12Util.cpp] [Line: 829] CommandList-&gt;Close() failed at D3D12CommandList.cpp:384 with error DXGI_ERROR_DEVICE_REMOVED",
            Ue4ssStack))!;

        var notice = GameCrashReport.Describe(crash, [new Ue4ssModErrorCount("Bags", 2)]);

        Assert.True(GameCrashReport.IsGpuDriverCrash(crash));
        Assert.Contains("graphics driver", notice);
        Assert.DoesNotContain("UE4SS", notice);
        Assert.DoesNotContain("Bags", notice);
        Assert.DoesNotContain("mod", notice.Replace("not a mod", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }
}
