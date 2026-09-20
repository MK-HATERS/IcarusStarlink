using IcarusStarlink.Updater;

namespace IcarusStarlink.Updater.Tests;

public sealed class UpdateRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"UpdateRunner_{Guid.NewGuid():N}");
    private readonly string _installDirectory;
    private readonly string _newFilesDirectory;

    public UpdateRunnerTests()
    {
        _installDirectory = Path.Combine(_root, "install");
        _newFilesDirectory = Path.Combine(_root, "new");
        Directory.CreateDirectory(_installDirectory);
        Directory.CreateDirectory(_newFilesDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteInstall(string relativePath, string content)
    {
        var path = Path.Combine(_installDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteNew(string relativePath, string content)
    {
        var path = Path.Combine(_newFilesDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Run_SuccessfulApply_RelaunchesAndReturnsAppliedOutcome()
    {
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteNew("IcarusStarlink.App.exe", "new exe");
        var relaunchCallCount = 0;

        var result = UpdateRunner.Run(_installDirectory, _newFilesDirectory, _ => { }, () => { relaunchCallCount++; return true; });

        Assert.Equal(UpdateRunner.Outcome.Applied, result.Outcome);
        Assert.True(result.Relaunched);
        Assert.Equal(1, relaunchCallCount);
    }

    /// <summary>
    /// Regression test: a real bug found live (a user's own bug report — the app just closed and
    /// never came back after a failed update, read as "install failed" with no explanation) — a
    /// failed-but-rolled-back update used to never relaunch anything at all. The old install is
    /// genuinely intact and safe to use here (rollback fully restored it), so relaunch must still
    /// be called even though Apply itself threw.
    /// </summary>
    [Fact]
    public void Run_ApplyFailsButRollsBackSuccessfully_StillRelaunches()
    {
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteInstall("LockedFile.dll", "old locked");
        WriteNew("IcarusStarlink.App.exe", "new exe");
        WriteNew("LockedFile.dll", "new locked");
        var lockedPath = Path.Combine(_installDirectory, "LockedFile.dll");
        var relaunchCallCount = 0;

        UpdateRunner.Result result;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = UpdateRunner.Run(_installDirectory, _newFilesDirectory, _ => { }, () => { relaunchCallCount++; return true; });
        }

        Assert.Equal(UpdateRunner.Outcome.RolledBack, result.Outcome);
        Assert.True(result.Relaunched);
        Assert.Equal(1, relaunchCallCount);
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(_installDirectory, "IcarusStarlink.App.exe")));
    }

    [Fact]
    public void Run_RollbackItselfIncomplete_StillAttemptsRelaunchAndReportsBackupDirectory()
    {
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteInstall("LockedFile.dll", "old locked");
        WriteNew("IcarusStarlink.App.exe", "new exe");
        WriteNew("LockedFile.dll", "new locked");
        var lockedPath = Path.Combine(_installDirectory, "LockedFile.dll");
        var relaunchCallCount = 0;

        UpdateRunner.Result result;
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            result = UpdateRunner.Run(_installDirectory, _newFilesDirectory, _ => { }, () => { relaunchCallCount++; return true; });
        }

        Assert.Equal(UpdateRunner.Outcome.RollbackIncomplete, result.Outcome);
        Assert.True(result.Relaunched);
        Assert.Equal(1, relaunchCallCount);
        Assert.NotNull(result.BackupDirectory);
    }

    [Fact]
    public void Run_RelaunchCallbackReturnsFalse_ResultReflectsThat()
    {
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteNew("IcarusStarlink.App.exe", "new exe");

        var result = UpdateRunner.Run(_installDirectory, _newFilesDirectory, _ => { }, () => false);

        Assert.Equal(UpdateRunner.Outcome.Applied, result.Outcome);
        Assert.False(result.Relaunched);
    }
}
