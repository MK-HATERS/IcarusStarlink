using System.IO;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.Tests.Utilities;

public class CrashReportWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    /// <summary>Regression: reports were named to the second and overwritten, so a crash while handling a crash replaced the first (usually the real) report.</summary>
    [Fact]
    public void Write_TwoCrashesInTheSameSecond_KeepsBothReports()
    {
        CrashReportWriter.Write(_tempDir, new InvalidOperationException("first, the real one"), "UI thread", activityLog: null);
        CrashReportWriter.Write(_tempDir, new InvalidOperationException("second, while handling it"), "UI thread", activityLog: null);

        var reports = Directory.GetFiles(_tempDir, "crash-*.txt").Select(File.ReadAllText).ToList();
        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, r => r.Contains("first, the real one"));
    }

    [Fact]
    public void Write_RealException_ProducesAFileWithExceptionDetailsAndSource()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("something real broke");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        CrashReportWriter.Write(_tempDir, thrown, "UI thread (DispatcherUnhandledException)", activityLog: null);

        var reportFile = Assert.Single(Directory.GetFiles(_tempDir, "crash-*.txt"));
        var content = File.ReadAllText(reportFile);
        Assert.Contains("UI thread (DispatcherUnhandledException)", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("something real broke", content);
        // A real ex.ToString() includes the stack trace ("at ...") for an exception that was
        // actually thrown (not just constructed) — confirms the full exception, not just Message,
        // lands in the report.
        Assert.Contains("at ", content);
    }

    [Fact]
    public void Write_ExceptionWithInnerException_IncludesTheInnerExceptionToo()
    {
        var inner = new IOException("disk full");
        var outer = new InvalidOperationException("couldn't save", inner);

        CrashReportWriter.Write(_tempDir, outer, "Startup", activityLog: null);

        var reportFile = Assert.Single(Directory.GetFiles(_tempDir, "crash-*.txt"));
        var content = File.ReadAllText(reportFile);
        Assert.Contains("couldn't save", content);
        Assert.Contains("disk full", content);
        Assert.Contains("IOException", content);
    }

    [Fact]
    public void Write_IncludesAppVersionAndOsInfo()
    {
        CrashReportWriter.Write(_tempDir, new Exception("x"), "Startup", activityLog: null);

        var reportFile = Assert.Single(Directory.GetFiles(_tempDir, "crash-*.txt"));
        var content = File.ReadAllText(reportFile);
        Assert.Contains("App version:", content);
        Assert.Contains("OS:", content);
        Assert.Contains(".NET runtime:", content);
    }

    [Fact]
    public void Write_NullActivityLog_DoesNotThrowAndOmitsTheActivitySection()
    {
        CrashReportWriter.Write(_tempDir, new Exception("x"), "Startup", activityLog: null);

        var reportFile = Assert.Single(Directory.GetFiles(_tempDir, "crash-*.txt"));
        var content = File.ReadAllText(reportFile);
        Assert.DoesNotContain("Recent activity", content);
    }

    [Fact]
    public void Write_ActivityLogWithEntries_IncludesRecentEntriesNewestFirst()
    {
        var activityLog = new ActivityLog();
        activityLog.Log("First thing that happened");
        activityLog.Log("Second thing that happened", ActivityEntryKind.Warning);

        CrashReportWriter.Write(_tempDir, new Exception("x"), "Startup", activityLog);

        var reportFile = Assert.Single(Directory.GetFiles(_tempDir, "crash-*.txt"));
        var content = File.ReadAllText(reportFile);
        Assert.Contains("Recent activity", content);
        Assert.Contains("First thing that happened", content);
        Assert.Contains("Second thing that happened", content);
        // ActivityLog.Log inserts newest-first (see its own doc comment) — the report should
        // preserve that order rather than reversing it.
        Assert.True(content.IndexOf("Second thing that happened", StringComparison.Ordinal)
            < content.IndexOf("First thing that happened", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_ActivityLogWithNoEntries_SaysSoRatherThanShowingAnEmptySection()
    {
        var activityLog = new ActivityLog();

        CrashReportWriter.Write(_tempDir, new Exception("x"), "Startup", activityLog);

        var reportFile = Assert.Single(Directory.GetFiles(_tempDir, "crash-*.txt"));
        var content = File.ReadAllText(reportFile);
        Assert.Contains("none recorded this session", content);
    }

    /// <summary>Regression: crash reports were never deleted, so one failing background task per session added a file forever.</summary>
    [Fact]
    public void Write_KeepsOnlyTheNewestReports()
    {
        Directory.CreateDirectory(_tempDir);
        var oldest = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var day = 1; day <= 25; day++)
        {
            var path = Path.Combine(_tempDir, $"crash-200001{day:00}-000000-000.txt");
            File.WriteAllText(path, $"old report {day}");
            File.SetLastWriteTimeUtc(path, oldest.AddHours(day));
        }

        CrashReportWriter.Write(_tempDir, new Exception("the newest"), "Startup", activityLog: null);

        var reports = Directory.GetFiles(_tempDir, "crash-*.txt");
        Assert.Equal(20, reports.Length);
        Assert.Contains(reports, r => File.ReadAllText(r).Contains("the newest"));
        for (var day = 1; day <= 6; day++)
        {
            Assert.False(File.Exists(Path.Combine(_tempDir, $"crash-200001{day:00}-000000-000.txt")), $"report {day} should be gone");
        }

        Assert.True(File.Exists(Path.Combine(_tempDir, "crash-20000107-000000-000.txt")));
    }

    [Fact]
    public void Write_LogsDirectoryDoesNotExistYet_CreatesItRatherThanThrowing()
    {
        var missingDir = Path.Combine(_tempDir, "DoesNotExistYet");

        CrashReportWriter.Write(missingDir, new Exception("x"), "Startup", activityLog: null);

        Assert.True(Directory.Exists(missingDir));
        Assert.Single(Directory.GetFiles(missingDir, "crash-*.txt"));
    }

    /// <summary>The crash boxes name the report the player can attach, so Write hands back where it went.</summary>
    [Fact]
    public void Write_ReturnsThePathOfTheReportItWrote()
    {
        string? path = CrashReportWriter.Write(_tempDir, new Exception("the one to attach"), "Startup", activityLog: null);

        Assert.NotNull(path);
        Assert.Equal(Directory.GetFiles(_tempDir, "crash-*.txt").Single(), path);
        Assert.Contains("the one to attach", File.ReadAllText(path));
    }

    /// <summary>When no report could be written, Write says so (null) rather than naming a file that isn't there.</summary>
    [Fact]
    public void Write_FolderCannotBeCreated_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        var aFile = Path.Combine(_tempDir, "not-a-folder");
        File.WriteAllText(aFile, "");

        string? path = CrashReportWriter.Write(Path.Combine(aFile, "Logs"), new Exception("x"), "Startup", activityLog: null);

        Assert.Null(path);
    }
}
