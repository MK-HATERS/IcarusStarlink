using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>The note the updater leaves for the relaunched app (written by IcarusStarlink.Updater's UpdateOutcomeFile: outcome name, then one line).</summary>
public sealed class AppUpdateOutcomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public AppUpdateOutcomeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ARolledBackUpdate_IsReportedOnce_WithTheUpdatersOwnWords()
    {
        var path = Path.Combine(_root, AppUpdateOutcome.FileName);
        File.WriteAllText(path, "RolledBack\nThe update didn't install, so your previous version was put back: Access denied. Details: C:\\x\\updater.log\n");

        var outcome = AppUpdateOutcome.TakeLast(path, Path.Combine(_root, "missing.txt"));

        Assert.NotNull(outcome);
        Assert.False(outcome.Installed);
        Assert.StartsWith("The update didn't install", outcome.Message);
        Assert.False(File.Exists(path), "read once: the next start says nothing");
        Assert.Null(AppUpdateOutcome.TakeLast(path));
    }

    [Fact]
    public void AnAppliedUpdate_CountsAsInstalled_AndTheNewestNoteWins()
    {
        var logs = Path.Combine(_root, AppUpdateOutcome.FileName);
        var temp = Path.Combine(_root, AppUpdateOutcome.TempFileName);
        File.WriteAllText(temp, "RolledBack\nold failure\n");
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(logs, "Applied\nThe update installed.\n");

        var outcome = AppUpdateOutcome.TakeLast(logs, temp);

        Assert.True(outcome!.Installed);
        Assert.False(File.Exists(temp));
    }
}
