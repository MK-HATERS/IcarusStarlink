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

    [Fact]
    public void Write_LogsDirectoryDoesNotExistYet_CreatesItRatherThanThrowing()
    {
        var missingDir = Path.Combine(_tempDir, "DoesNotExistYet");

        CrashReportWriter.Write(missingDir, new Exception("x"), "Startup", activityLog: null);

        Assert.True(Directory.Exists(missingDir));
        Assert.Single(Directory.GetFiles(missingDir, "crash-*.txt"));
    }
}
