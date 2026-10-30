using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Update everything's order, its stop at the first failure, and its stop before installing while Icarus runs.</summary>
public sealed class GameUpdateOrchestratorTests
{
    /// <summary>Records each step it's asked for; each can be told to fail.</summary>
    private sealed class FakeSteps : IGameUpdateSteps
    {
        public List<string> Calls { get; } = [];

        public bool GameRunning { get; set; }

        public GameUpdateStepResult Refresh { get; set; } = GameUpdateStepResult.Done();

        public GameUpdateStepResult Rebuild { get; set; } = GameUpdateStepResult.Done();

        public GameUpdateStepResult Install { get; set; } = GameUpdateStepResult.Done();

        public bool CheckThrows { get; set; }

        public Task<LastKnownGoodSnapshot> SnapshotAsync()
        {
            Calls.Add("snapshot");
            return Task.FromResult(new LastKnownGoodSnapshot { GameBuildId = "1" });
        }

        public Task RecordGameFingerprintsAsync()
        {
            Calls.Add("fingerprints");
            return Task.CompletedTask;
        }

        public Task<GameUpdateStepResult> RefreshGameDataAsync()
        {
            Calls.Add("refresh");
            return Task.FromResult(Refresh);
        }

        public GameUpdateStepResult Maps { get; set; } = GameUpdateStepResult.Done();

        public Task<GameUpdateStepResult> RedrawMapsAsync()
        {
            Calls.Add("maps");
            return Task.FromResult(Maps);
        }

        public Task CheckModsAsync()
        {
            Calls.Add("check");
            return CheckThrows ? throw new IOException("A mod folder is locked.") : Task.CompletedTask;
        }

        public Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync()
        {
            Calls.Add("fix");
            return Task.FromResult<IReadOnlyList<GameUpdateFixedMod>>([new("laanp-Canteens", "Canteens", 3)]);
        }

        public Task CheckForModUpdatesAsync()
        {
            Calls.Add("updates");
            return Task.CompletedTask;
        }

        public Task<GameUpdateStepResult> RebuildAsync()
        {
            Calls.Add("rebuild");
            return Task.FromResult(Rebuild);
        }

        public bool IsGameRunning()
        {
            Calls.Add("running?");
            return GameRunning;
        }

        public Task<GameUpdateStepResult> InstallAsync()
        {
            Calls.Add("install");
            return Task.FromResult(Install);
        }
    }

    [Fact]
    public async Task RunsEveryStepInOrder_SnapshotFirst()
    {
        var steps = new FakeSteps();
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add);

        Assert.Equal(["snapshot", "fingerprints", "refresh", "maps", "check", "fix", "updates", "rebuild", "running?", "install"], steps.Calls);
        Assert.True(result.Completed);
        Assert.True(result.Installed);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(3, Assert.Single(result.Fixed).FixCount);

        // Every step reported working, then done.
        foreach (var step in Enum.GetValues<GameUpdateStep>())
        {
            Assert.Equal([GameUpdateStepState.Running, GameUpdateStepState.Done], progress.Where(p => p.Step == step).Select(p => p.State));
        }

        Assert.Contains(progress, p => p.Step == GameUpdateStep.FixSure && p.Text == "Fixed 3 thing(s) in 1 mod(s).");
    }

    [Fact]
    public async Task AFailedStep_StopsTheRun_WithTheReason_AndTheSnapshotForRollBack()
    {
        var steps = new FakeSteps { Refresh = GameUpdateStepResult.Failed("UnrealPak.exe isn't at that path.") };
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add);

        Assert.Equal(["snapshot", "fingerprints", "refresh"], steps.Calls);
        Assert.False(result.Completed);
        Assert.Equal(GameUpdateStep.RefreshGameData, result.FailedStep);
        Assert.NotNull(result.Snapshot);
        Assert.Contains("Refresh game data didn't work: UnrealPak.exe isn't at that path.", result.Message);
        Assert.Contains("Roll back", result.Message);
        Assert.Equal(GameUpdateStepState.Failed, progress[^1].State);
    }

    [Fact]
    public async Task MapsThatCantBeDrawn_DontStopTheRun()
    {
        var steps = new FakeSteps { Maps = GameUpdateStepResult.Failed("The game's paks can't be read.") };
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add);

        Assert.True(result.Completed);
        Assert.Contains("install", steps.Calls);
        Assert.Contains(progress, p => p.Step == GameUpdateStep.Maps && p.State == GameUpdateStepState.Skipped && p.Text == "The game's paks can't be read.");
    }

    [Fact]
    public async Task AStepThatThrows_StopsTheRunToo()
    {
        var steps = new FakeSteps { CheckThrows = true };

        var result = await GameUpdateOrchestrator.RunAsync(steps);

        Assert.Equal(["snapshot", "fingerprints", "refresh", "maps", "check"], steps.Calls);
        Assert.Equal(GameUpdateStep.CheckMods, result.FailedStep);
        Assert.Contains("A mod folder is locked.", result.Message);
    }

    [Fact]
    public async Task WhileIcarusRuns_BuildsButStopsBeforeInstalling()
    {
        var steps = new FakeSteps { GameRunning = true };

        var result = await GameUpdateOrchestrator.RunAsync(steps);

        Assert.DoesNotContain("install", steps.Calls);
        Assert.Contains("rebuild", steps.Calls);
        Assert.True(result.Completed);
        Assert.True(result.InstallSkippedGameRunning);
        Assert.False(result.Installed);
        Assert.Contains("Icarus is running", result.Message);
    }

    [Fact]
    public async Task AFailedBuild_NeverInstalls()
    {
        var steps = new FakeSteps { Rebuild = GameUpdateStepResult.Failed("A queued mod is corrupt.") };

        var result = await GameUpdateOrchestrator.RunAsync(steps);

        Assert.DoesNotContain("install", steps.Calls);
        Assert.Equal(GameUpdateStep.Merge, result.FailedStep);
    }

    [Fact]
    public async Task NothingTicked_SkipsBuildAndInstall_AndStillFinishes()
    {
        var steps = new FakeSteps { Rebuild = GameUpdateStepResult.Skipped("Nothing is ticked to merge, so there's nothing to build.") };

        var result = await GameUpdateOrchestrator.RunAsync(steps);

        Assert.DoesNotContain("install", steps.Calls);
        Assert.True(result.Completed);
        Assert.Contains("Nothing is ticked", result.Message);
    }

    [Fact]
    public async Task ASnapshotThatFails_ChangesNothing()
    {
        var steps = new ThrowingSnapshotSteps();

        var result = await GameUpdateOrchestrator.RunAsync(steps);

        Assert.Null(result.Snapshot);
        Assert.Equal(GameUpdateStep.Snapshot, result.FailedStep);
        Assert.Contains("Nothing was changed.", result.Message);
        Assert.Equal(["snapshot"], steps.Calls);
    }

    private sealed class ThrowingSnapshotSteps : IGameUpdateSteps
    {
        public List<string> Calls { get; } = [];

        public Task<LastKnownGoodSnapshot> SnapshotAsync()
        {
            Calls.Add("snapshot");
            throw new UnauthorizedAccessException("Backups is read-only.");
        }

        public Task RecordGameFingerprintsAsync() => Fail();

        public Task<GameUpdateStepResult> RefreshGameDataAsync() => throw new InvalidOperationException();

        public Task CheckModsAsync() => Fail();

        public Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync() => throw new InvalidOperationException();

        public Task CheckForModUpdatesAsync() => Fail();

        public Task<GameUpdateStepResult> RebuildAsync() => throw new InvalidOperationException();

        public bool IsGameRunning() => throw new InvalidOperationException();

        public Task<GameUpdateStepResult> InstallAsync() => throw new InvalidOperationException();

        private Task Fail()
        {
            Calls.Add("more");
            return Task.CompletedTask;
        }
    }
}
