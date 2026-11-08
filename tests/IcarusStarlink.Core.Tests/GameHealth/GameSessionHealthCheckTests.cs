using IcarusStarlink.Core.GameHealth;

namespace IcarusStarlink.Core.Tests.GameHealth;

public class GameSessionHealthCheckTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private string MakeCrashFolder(string crashId, DateTime createdAtUtc, string? errorMessageXml = null)
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
                    {(errorMessageXml is null ? "" : $"<ErrorMessage>{errorMessageXml}</ErrorMessage>")}
                </RuntimeProperties>
            </FGenericCrashContext>
            """);
        Directory.SetCreationTimeUtc(crashDir, createdAtUtc);
        return crashesFolder;
    }

    [Fact]
    public void FindCrashesSince_NoCrashesFolderAtAll_ReturnsEmpty()
    {
        var crashes = GameSessionHealthCheck.FindCrashesSince(DateTimeOffset.UtcNow, Path.Combine(_tempDir, "DoesNotExist"));

        Assert.Empty(crashes);
    }

    [Fact]
    public void FindCrashesSince_CrashCreatedBeforeSince_IsExcluded()
    {
        var crashesFolder = MakeCrashFolder("OldCrash", DateTime.UtcNow.AddDays(-1), "Old crash message");

        var crashes = GameSessionHealthCheck.FindCrashesSince(DateTimeOffset.UtcNow, crashesFolder);

        Assert.Empty(crashes);
    }

    [Fact]
    public void FindCrashesSince_CrashCreatedAfterSince_IsIncludedWithParsedErrorMessage()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        var crashesFolder = MakeCrashFolder("NewCrash", DateTime.UtcNow, "LowLevelFatalError something broke");

        var crashes = GameSessionHealthCheck.FindCrashesSince(since, crashesFolder);

        var crash = Assert.Single(crashes);
        Assert.Equal("NewCrash", crash.CrashId);
        Assert.Equal("LowLevelFatalError something broke", crash.ErrorMessage);
    }

    [Fact]
    public void FindCrashesSince_CrashContextHasNoErrorMessage_FallsBackToPlaceholderInsteadOfThrowing()
    {
        var crashesFolder = MakeCrashFolder("NoMessageCrash", DateTime.UtcNow, errorMessageXml: null);

        var crashes = GameSessionHealthCheck.FindCrashesSince(DateTimeOffset.UtcNow.AddMinutes(-1), crashesFolder);

        var crash = Assert.Single(crashes);
        Assert.Equal("(no error message recorded in this crash report)", crash.ErrorMessage);
    }

    [Fact]
    public void FindCrashesSince_MalformedXml_SkipsGracefullyInsteadOfThrowing()
    {
        var crashesFolder = Path.Combine(_tempDir, "Crashes");
        var crashDir = Path.Combine(crashesFolder, "CorruptCrash");
        Directory.CreateDirectory(crashDir);
        File.WriteAllText(Path.Combine(crashDir, "CrashContext.runtime-xml"), "<not valid xml");
        Directory.SetCreationTimeUtc(crashDir, DateTime.UtcNow);

        var crashes = GameSessionHealthCheck.FindCrashesSince(DateTimeOffset.UtcNow.AddMinutes(-1), crashesFolder);

        var crash = Assert.Single(crashes);
        Assert.Equal("(no error message recorded in this crash report)", crash.ErrorMessage);
    }

    [Fact]
    public void FindCrashesSince_MultipleCrashes_OrderedNewestFirst()
    {
        var crashesFolder = MakeCrashFolder("Crash1", DateTime.UtcNow.AddMinutes(-10), "First");
        MakeCrashFolder("Crash2", DateTime.UtcNow.AddMinutes(-2), "Second");

        var crashes = GameSessionHealthCheck.FindCrashesSince(DateTimeOffset.UtcNow.AddMinutes(-15), crashesFolder);

        Assert.Equal(["Crash2", "Crash1"], crashes.Select(c => c.CrashId));
    }

    [Fact]
    public void ReadUe4ssLogTail_LogDoesNotExist_ReturnsEmpty()
    {
        var contentPath = Path.Combine(_tempDir, "Icarus", "Content");
        Directory.CreateDirectory(contentPath);

        var lines = GameSessionHealthCheck.ReadUe4ssLogTail(contentPath);

        Assert.Empty(lines);
    }

    [Fact]
    public void ReadUe4ssLogTail_LogExists_ReturnsOnlyTheLastMaxLines()
    {
        var contentPath = Path.Combine(_tempDir, "Icarus", "Content");
        var loaderFolder = Path.Combine(_tempDir, "Icarus", "Binaries", "Win64", "ue4ss");
        Directory.CreateDirectory(contentPath);
        Directory.CreateDirectory(loaderFolder);
        File.WriteAllLines(Path.Combine(loaderFolder, "UE4SS.log"), Enumerable.Range(1, 100).Select(i => $"line {i}"));

        var lines = GameSessionHealthCheck.ReadUe4ssLogTail(contentPath, maxLines: 5);

        Assert.Equal(["line 96", "line 97", "line 98", "line 99", "line 100"], lines);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(0)]
    public void ReadUe4ssLogTail_ALongLog_ReturnsItsLastLinesInOrder(int maxLines)
    {
        var contentPath = Path.Combine(_tempDir, "Icarus", "Content");
        var loaderFolder = Path.Combine(_tempDir, "Icarus", "Binaries", "Win64", "ue4ss");
        Directory.CreateDirectory(contentPath);
        Directory.CreateDirectory(loaderFolder);
        File.WriteAllLines(Path.Combine(loaderFolder, "UE4SS.log"), Enumerable.Range(1, 10_000).Select(i => $"line {i}"));

        var lines = GameSessionHealthCheck.ReadUe4ssLogTail(contentPath, maxLines);

        Assert.Equal(Enumerable.Range(10_001 - maxLines, maxLines).Select(i => $"line {i}"), lines);
    }

    /// <summary>UE4SS holds its log open for writing while the game runs; the tail used to come back empty then.</summary>
    [Fact]
    public void ReadUe4ssLogTail_LogHeldOpenForWriting_StillReadsIt()
    {
        var contentPath = Path.Combine(_tempDir, "Icarus", "Content");
        var loaderFolder = Path.Combine(_tempDir, "Icarus", "Binaries", "Win64", "ue4ss");
        Directory.CreateDirectory(contentPath);
        Directory.CreateDirectory(loaderFolder);
        var logPath = Path.Combine(loaderFolder, "UE4SS.log");
        File.WriteAllLines(logPath, ["first", "last"]);

        using (new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Equal(["first", "last"], GameSessionHealthCheck.ReadUe4ssLogTail(contentPath));
        }
    }

    /// <summary>The CrashContext shape Icarus writes (cut down from a real one), with the ids a bug report must never carry.</summary>
    private string MakeFullCrashFolder(string crashId, DateTime createdAtUtc, string crashedStack)
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
                    <ErrorMessage>Unhandled Exception: EXCEPTION_ACCESS_VIOLATION 0x000001d736227901</ErrorMessage>
                    <SecondsSinceStart>821</SecondsSinceStart>
                    <EngineVersion>4.27.2-0+++UE4+Release-4.27</EngineVersion>
                    <MachineId>289810A14D926B6BC48AE4A426D364FD</MachineId>
                    <LoginId>289810a14d926b6bc48ae4a426d364fd</LoginId>
                    <EpicAccountId>d985d944a36141fb804fdafbfee1839c</EpicAccountId>
                    <GameStateName>BP_IcarusGameState_C</GameStateName>
                    <PCallStack>{crashedStack}</PCallStack>
                </RuntimeProperties>
            </FGenericCrashContext>
            """);
        Directory.SetCreationTimeUtc(crashDir, createdAtUtc);
        return crashesFolder;
    }

    [Fact]
    public void ReadLastCrash_TakesTheNewestCrash_WithItsMessageTypeEngineAndModules()
    {
        MakeFullCrashFolder("Older", DateTime.UtcNow.AddDays(-2), "Icarus-Win64-Shipping 0x00007ff6a0000000 + 1427f");
        var crashesFolder = MakeFullCrashFolder("Newest", DateTime.UtcNow.AddMinutes(-3),
            "wintrust 0x000001d611780000 + 124aa7901 UE4SS 0x00007ffbfc300000 + 2c5680 UE4SS 0x00007ffbfc300000 + 2c5700 KERNELBASE 0x00007ffd180e0000 + 1427f");

        var crash = GameSessionHealthCheck.ReadLastCrash(crashesFolder);

        Assert.NotNull(crash);
        Assert.Equal("Newest", crash.CrashId);
        Assert.Equal("Crash", crash.CrashType);
        Assert.Equal("Unhandled Exception: EXCEPTION_ACCESS_VIOLATION 0x000001d736227901", crash.ErrorMessage);
        Assert.Equal("4.27.2-0+++UE4+Release-4.27", crash.EngineVersion);
        Assert.Equal(["wintrust", "UE4SS", "KERNELBASE"], crash.Modules);
        Assert.True(crash.Ue4ssInStack);
    }

    [Fact]
    public void ReadLastCrash_StackWithoutUe4ss_SaysSo()
    {
        var crashesFolder = MakeFullCrashFolder("NoUe4ss", DateTime.UtcNow, "Icarus-Win64-Shipping 0x00007ff6a0000000 + 1427f");

        var crash = GameSessionHealthCheck.ReadLastCrash(crashesFolder);

        Assert.NotNull(crash);
        Assert.False(crash.Ue4ssInStack);
    }

    [Fact]
    public void ReadLastCrash_NoCrashesFolder_ReturnsNull() =>
        Assert.Null(GameSessionHealthCheck.ReadLastCrash(Path.Combine(_tempDir, "DoesNotExist")));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
