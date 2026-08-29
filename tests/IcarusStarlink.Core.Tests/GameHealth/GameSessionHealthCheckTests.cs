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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
