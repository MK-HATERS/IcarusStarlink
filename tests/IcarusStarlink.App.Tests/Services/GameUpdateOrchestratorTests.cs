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

        /// <summary>The extracted game data already matches the game (not recorded in Calls).</summary>
        public bool DataCurrent { get; set; }

        public Task<bool> IsGameDataCurrentAsync() => Task.FromResult(DataCurrent);

        public GameUpdateStepResult Maps { get; set; } = GameUpdateStepResult.Done();

        public bool MapsThrow { get; set; }

        public Task<GameUpdateStepResult> RedrawMapsAsync()
        {
            Calls.Add("maps");
            return MapsThrow ? throw new IOException("The map cache is locked.") : Task.FromResult(Maps);
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

        /// <summary>What Show me first lists.</summary>
        public List<GameUpdateFixCandidate> Candidates { get; } =
        [
            new("laanp-Canteens", "Canteens", 3, ["Old copies of game data: 3 in D_Itemable"]),
            new("Bows", "Better Bows", 1, ["Rows the game no longer has: 1 in D_ItemsStatic"]),
        ];

        public Task<IReadOnlyList<GameUpdateFixCandidate>> ListSureFixesAsync()
        {
            Calls.Add("list fixes");
            return Task.FromResult<IReadOnlyList<GameUpdateFixCandidate>>(Candidates);
        }

        public Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync(IReadOnlyCollection<string> folders)
        {
            Calls.Add($"fix {string.Join("+", folders)}");
            return Task.FromResult<IReadOnlyList<GameUpdateFixedMod>>([.. folders.Select(f => new GameUpdateFixedMod(f, f, 1))]);
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

    /// <summary>
    /// Regression: after Settings' Refresh game data had already run on this version, Update everything
    /// extracted again, found no changes, and that empty report became the one Weekly Changes opened on.
    /// </summary>
    [Fact]
    public async Task GameDataAlreadyCurrent_SkipsTheRefresh()
    {
        var steps = new FakeSteps { DataCurrent = true };
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add);

        Assert.DoesNotContain("refresh", steps.Calls);
        Assert.True(result.Completed);
        Assert.Contains(progress, p => p.Step == GameUpdateStep.RefreshGameData && p.State == GameUpdateStepState.Done
            && p.Text == "Game data already matches this version of Icarus.");
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
    public async Task MapsThatThrow_DontStopTheRunEither()
    {
        var steps = new FakeSteps { MapsThrow = true };
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add);

        Assert.True(result.Completed);
        Assert.Contains("install", steps.Calls);
        Assert.Contains(progress, p => p.Step == GameUpdateStep.Maps && p.State == GameUpdateStepState.Skipped && p.Text.Contains("The map cache is locked.", StringComparison.Ordinal));
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

    /// <summary>Regression: a run that built the merge but couldn't install it (Icarus running) cleared the Library's "Icarus updated" bar, though the game still had the old merge.</summary>
    [Fact]
    public async Task WhileIcarusRuns_TheGameUpdateIsNotMarkedHandled()
    {
        var result = await GameUpdateOrchestrator.RunAsync(new FakeSteps { GameRunning = true });

        Assert.False(result.ReadyForUpdatedGame);
        Assert.Contains("click Update everything again", result.Message);
        Assert.Contains("the Library's bar stays until it's in the game", result.Message);
        Assert.True((await GameUpdateOrchestrator.RunAsync(new FakeSteps())).ReadyForUpdatedGame);
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

    // --- The steps chosen before Start ---

    private static GameUpdateOptions AllOn(GameUpdateFixChoice fix = GameUpdateFixChoice.FixByItself) => new() { Fix = fix };

    private static GameUpdateProgress Last(List<GameUpdateProgress> progress, GameUpdateStep step) => progress.Last(p => p.Step == step);

    [Fact]
    public async Task UntickedSteps_AreLeftOut_AndShownAsSkipped()
    {
        var steps = new FakeSteps();
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(
            steps, progress.Add, AllOn() with { RedrawMaps = false, CheckForModUpdates = false });

        Assert.Equal(["snapshot", "fingerprints", "refresh", "check", "fix", "rebuild", "running?", "install"], steps.Calls);
        Assert.True(result.Completed);
        Assert.True(result.ReadyForUpdatedGame);
        Assert.Equal(GameUpdateStepState.Skipped, Last(progress, GameUpdateStep.Maps).State);
        Assert.Contains("Map page", Last(progress, GameUpdateStep.Maps).Text);
        Assert.Equal(GameUpdateStepState.Skipped, Last(progress, GameUpdateStep.CheckUpdates).State);
        Assert.Contains("unticked", Last(progress, GameUpdateStep.CheckUpdates).Text);
    }

    [Fact]
    public async Task BuildUnticked_NeitherBuildsNorInstalls_AndTheLibraryBarStays()
    {
        var steps = new FakeSteps();
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add, AllOn() with { BuildMerge = false, Install = false });

        Assert.DoesNotContain("rebuild", steps.Calls);
        Assert.DoesNotContain("install", steps.Calls);
        Assert.True(result.Completed);
        Assert.False(result.Installed);
        // The game still has the merge from before the update.
        Assert.False(result.ReadyForUpdatedGame);
        Assert.Contains("wasn't built", result.Message);
        Assert.Equal(GameUpdateStepState.Skipped, Last(progress, GameUpdateStep.Merge).State);
        Assert.Equal(GameUpdateStepState.Skipped, Last(progress, GameUpdateStep.Install).State);
    }

    [Fact]
    public async Task InstallUnticked_BuildsButLeavesTheGameAlone()
    {
        var steps = new FakeSteps();
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add, AllOn() with { Install = false });

        Assert.Contains("rebuild", steps.Calls);
        Assert.DoesNotContain("install", steps.Calls);
        // Not even asked whether the game runs: nothing is going into it.
        Assert.DoesNotContain("running?", steps.Calls);
        Assert.True(result.Completed);
        Assert.False(result.Installed);
        Assert.False(result.ReadyForUpdatedGame);
        Assert.Contains("Copy to game", result.Message);
        Assert.Equal(GameUpdateStepState.Skipped, Last(progress, GameUpdateStep.Install).State);
    }

    [Fact]
    public async Task DontFix_ChangesNoMod_AndGoesOn()
    {
        var steps = new FakeSteps();
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add, AllOn(GameUpdateFixChoice.DontFix));

        Assert.DoesNotContain(steps.Calls, c => c.StartsWith("fix", StringComparison.Ordinal));
        Assert.DoesNotContain("list fixes", steps.Calls);
        Assert.Empty(result.Fixed);
        Assert.True(result.Installed);
        Assert.Equal(GameUpdateStepState.Skipped, Last(progress, GameUpdateStep.FixSure).State);
        Assert.Contains("No mod was changed", Last(progress, GameUpdateStep.FixSure).Text);
    }

    [Fact]
    public async Task ShowMeFirst_FixesNothingUntilAnswered_ThenOnlyTheTickedMods()
    {
        var steps = new FakeSteps();
        var progress = new List<GameUpdateProgress>();
        IReadOnlyList<GameUpdateFixCandidate>? shown = null;
        List<string>? callsWhenAsked = null;

        var result = await GameUpdateOrchestrator.RunAsync(steps, progress.Add, AllOn(GameUpdateFixChoice.ShowFirst), candidates =>
        {
            shown = candidates;
            callsWhenAsked = [.. steps.Calls];
            return Task.FromResult(GameUpdateFixDecision.Fix(["Bows"]));
        });

        Assert.Equal(2, shown!.Count);
        // Nothing was fixed (or built) before the answer.
        Assert.Equal(["snapshot", "fingerprints", "refresh", "maps", "check", "list fixes"], callsWhenAsked);
        Assert.Equal(["snapshot", "fingerprints", "refresh", "maps", "check", "list fixes", "fix Bows", "updates", "rebuild", "running?", "install"], steps.Calls);
        Assert.Equal(["Bows"], result.Fixed.Select(m => m.FolderName));
        Assert.True(result.Installed);
        Assert.Contains(progress, p => p.Step == GameUpdateStep.FixSure && p.State == GameUpdateStepState.Running && p.Text.Contains("Waiting for you", StringComparison.Ordinal));
        Assert.Equal(GameUpdateStepState.Done, Last(progress, GameUpdateStep.FixSure).State);
    }

    [Fact]
    public async Task ShowMeFirst_SkipFixes_GoesOnWithoutFixing()
    {
        var steps = new FakeSteps();
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(
            steps, progress.Add, AllOn(GameUpdateFixChoice.ShowFirst), _ => Task.FromResult(GameUpdateFixDecision.Skip));

        Assert.DoesNotContain(steps.Calls, c => c.StartsWith("fix", StringComparison.Ordinal));
        Assert.Contains("install", steps.Calls);
        Assert.Empty(result.Fixed);
        Assert.True(result.ReadyForUpdatedGame);
        Assert.Equal(GameUpdateStepState.Skipped, Last(progress, GameUpdateStep.FixSure).State);
    }

    [Fact]
    public async Task ShowMeFirst_Stop_EndsTheRun_AndTheLaterStepsAreReportedAsNotRun()
    {
        var steps = new FakeSteps();
        var progress = new List<GameUpdateProgress>();

        var result = await GameUpdateOrchestrator.RunAsync(
            steps, progress.Add, AllOn(GameUpdateFixChoice.ShowFirst), _ => Task.FromResult(GameUpdateFixDecision.Stop));

        Assert.Equal(["snapshot", "fingerprints", "refresh", "maps", "check", "list fixes"], steps.Calls);
        Assert.True(result.Stopped);
        Assert.False(result.Completed);
        Assert.False(result.ReadyForUpdatedGame);
        Assert.Null(result.FailedStep);
        Assert.NotNull(result.Snapshot);
        Assert.Contains("no mod was changed", result.Message);
        Assert.Contains("Build the merge", result.Message);
        foreach (var step in new[] { GameUpdateStep.CheckUpdates, GameUpdateStep.Merge, GameUpdateStep.Install })
        {
            Assert.Equal(GameUpdateStepState.Skipped, Last(progress, step).State);
            Assert.Equal("Not run: you stopped.", Last(progress, step).Text);
        }
    }

    [Fact]
    public async Task ShowMeFirst_NothingToFix_DoesNotStopToAsk()
    {
        var steps = new FakeSteps();
        steps.Candidates.Clear();
        var asked = false;

        var result = await GameUpdateOrchestrator.RunAsync(steps, null, AllOn(GameUpdateFixChoice.ShowFirst), _ =>
        {
            asked = true;
            return Task.FromResult(GameUpdateFixDecision.Stop);
        });

        Assert.False(asked);
        Assert.True(result.Installed);
    }

    [Fact]
    public void Options_AreRememberedInSettings_ShowMeFirstByDefault()
    {
        var settings = new IcarusStarlink.Core.Settings.AppSettings();

        var first = GameUpdateOptions.FromSettings(settings);
        Assert.Equal(GameUpdateFixChoice.ShowFirst, first.Fix);
        Assert.True(first.CheckForModUpdates);
        Assert.True(first.BuildMerge);
        Assert.True(first.Install);
        // The maps tick is the Map page's own box (off until ticked there or here).
        Assert.False(first.RedrawMaps);

        new GameUpdateOptions { RedrawMaps = true, CheckForModUpdates = false, BuildMerge = true, Install = false, Fix = GameUpdateFixChoice.DontFix }.CopyTo(settings);

        Assert.True(settings.RedrawMapsDuringGameUpdate);
        var again = GameUpdateOptions.FromSettings(settings);
        Assert.Equal(new GameUpdateOptions { RedrawMaps = true, CheckForModUpdates = false, BuildMerge = true, Install = false, Fix = GameUpdateFixChoice.DontFix }, again);

        // Install never comes back ticked without the build.
        settings.GameUpdateBuildsMerge = false;
        settings.GameUpdateInstalls = true;
        Assert.False(GameUpdateOptions.FromSettings(settings).Install);
    }

    /// <summary>What Show me first says about a mod: the sure fixes only, by kind, with how many and in which tables.</summary>
    [Fact]
    public void FixCandidate_ListsOnlyTheSureFixes_ByKindAndTable()
    {
        static IcarusStarlink.PakIO.Exmod.ModIssue Issue(IcarusStarlink.PakIO.Exmod.ModIssueKind kind, string file, bool sure) =>
            new(kind, file, "Row", "Field", null, null, null, null, IcarusStarlink.PakIO.Exmod.ModIssueFix.UseGame, sure, "why");

        var report = new IcarusStarlink.PakIO.Exmod.ModHealthReport(
        [
            Issue(IcarusStarlink.PakIO.Exmod.ModIssueKind.OldCopy, "Traits-D_Itemable.json", sure: true),
            Issue(IcarusStarlink.PakIO.Exmod.ModIssueKind.OldCopy, "Items-D_ItemsStatic.json", sure: true),
            Issue(IcarusStarlink.PakIO.Exmod.ModIssueKind.BrokenLink, "Traits-D_Itemable.json", sure: true),
            // Not sure enough to fix by itself: for the fix window, not this list.
            Issue(IcarusStarlink.PakIO.Exmod.ModIssueKind.LikelyOld, "Traits-D_Durable.json", sure: false),
        ], AuthorChangeCount: 4);

        var candidate = GameUpdateFixCandidate.From("Bows", "Better Bows", report);

        Assert.Equal(3, candidate!.FixCount);
        Assert.Equal(
            ["Old copies of game data: 2 in Traits-D_Itemable, Items-D_ItemsStatic", "Links to things the game doesn't have: 1 in Traits-D_Itemable"],
            candidate.Lines);
        Assert.Null(GameUpdateFixCandidate.From("Bows", "Better Bows", new IcarusStarlink.PakIO.Exmod.ModHealthReport([], 0)));
        Assert.Null(GameUpdateFixCandidate.From("Bows", "Better Bows", null));
    }
}
