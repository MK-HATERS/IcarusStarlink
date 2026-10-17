using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The review after a game update: its three groups, Keep off until updated, and Roll back.</summary>
public sealed class GameUpdateReviewViewModelTests
{
    private sealed class FakeActions : IGameUpdateReviewActions
    {
        public List<string> Calls { get; } = [];

        public string? RollBackAnswer { get; set; } = "Rolled back.";

        public Task OpenFixWindowAsync(string folderName)
        {
            Calls.Add($"fix {folderName}");
            return Task.CompletedTask;
        }

        public Task GetUpdateAsync(string folderName, bool isUe4ss)
        {
            Calls.Add($"update {folderName} {isUe4ss}");
            return Task.CompletedTask;
        }

        public Task<string> KeepOffAsync(string folderName)
        {
            Calls.Add($"keep off {folderName}");
            return Task.FromResult($"Turned '{folderName}' off and installed the merge without it.");
        }

        public Task<string?> RollBackAsync(LastKnownGoodSnapshot snapshot)
        {
            Calls.Add("roll back");
            return Task.FromResult(RollBackAnswer);
        }
    }

    private const string Player = "BP/Player/BP_IcarusPlayerCharacterSurvival";

    private static ModFileRisk Risk(string? changed, string? proof) =>
        ReplacedFileRiskRater.Assess([Player], [])! with
        {
            Files = [ReplacedFileRiskRater.Rate(Player) with { ChangedByGame = changed, OutOfDateProof = proof }],
        };

    private static GameUpdateModInfo Mod(string name, int review = 0, bool update = false, ModFileRisk? risk = null, bool ticked = true, bool ue4ss = false) =>
        new(name, name, ue4ss, review, update, "1.0", update ? "2.0" : null, risk, ticked);

    [Fact]
    public void Load_SortsModsIntoTheThreeGroups()
    {
        var review = new GameUpdateReviewViewModel(new FakeActions());

        review.Load(
            [new("Canteens", "Canteens", 4)],
            [
                Mod("Homestead", review: 2),
                Mod("Buyable Beasts", update: true),
                Mod("Lua Thing", update: true, ue4ss: true),
                Mod("Pickup and Move", risk: Risk("the game changed Drink in this update", "out of date: the game changed Drink in this update; this mod still has its own older copy")),
                Mod("Fly Mode", risk: Risk("the game changed Drink in this update", null)),
                Mod("Unticked Mover", risk: Risk("the game changed Drink in this update", "out of date: x"), ticked: false),
                Mod("Plain", risk: Risk(null, null)),
            ]);

        var fixedRow = Assert.Single(review.FixedForYou);
        Assert.Equal("4 proven fixes", fixedRow.Tag);
        Assert.Equal("Open fix window", fixedRow.ActionLabel);

        Assert.Equal(["Buyable Beasts", "Homestead", "Lua Thing"], review.NeedsALook.Select(r => r.Name));
        Assert.Equal("Update", review.NeedsALook[0].ActionLabel);
        Assert.Contains("2.0 is out", review.NeedsALook[0].Detail);
        Assert.Equal("Open on Nexus", review.NeedsALook[2].ActionLabel);

        // Only ticked mods replacing a file the update changed; the proven one first, its proof shown.
        Assert.Equal(["Pickup and Move", "Fly Mode"], review.HighRisk.Select(r => r.Name));
        Assert.Equal("Out of date", review.HighRisk[0].Tag);
        Assert.Contains("this mod still has its own older copy", review.HighRisk[0].Detail);
        Assert.Equal("Changed by the update", review.HighRisk[1].Tag);
        Assert.Equal("Keep off until updated", review.HighRisk[1].ActionLabel);
        Assert.False(review.IsAllClear);
    }

    [Fact]
    public void NothingFoundAnywhere_IsAllClear()
    {
        var review = new GameUpdateReviewViewModel(new FakeActions());

        review.Load([], [Mod("Plain"), Mod("Risky but unchanged", risk: Risk(null, null))]);

        Assert.True(review.IsAllClear);
        Assert.Empty(review.HighRisk);
    }

    [Fact]
    public async Task KeepOffUntilUpdated_TurnsTheModOff_AndTheRowSaysSo()
    {
        var actions = new FakeActions();
        var review = new GameUpdateReviewViewModel(actions);
        review.Load([], [Mod("Pickup and Move", risk: Risk("the game changed Drink in this update", null))]);
        var row = review.HighRisk[0];

        await review.ActCommand.ExecuteAsync(row);

        Assert.Equal(["keep off Pickup and Move"], actions.Calls);
        Assert.True(row.IsActionDone);
        Assert.False(row.CanAct);
        Assert.Equal("Kept off", row.ActionLabel);
        Assert.Equal("Turned 'Pickup and Move' off and installed the merge without it.", review.StatusMessage);
        Assert.False(review.IsRunning);

        // Done once: a second click does nothing.
        await review.ActCommand.ExecuteAsync(row);
        Assert.Single(actions.Calls);
    }

    [Fact]
    public async Task OtherButtons_ReachTheirActions()
    {
        var actions = new FakeActions();
        var review = new GameUpdateReviewViewModel(actions);
        review.Load([new("Canteens", "Canteens", 1)], [Mod("Beasts", update: true)]);

        await review.ActCommand.ExecuteAsync(review.FixedForYou[0]);
        await review.ActCommand.ExecuteAsync(review.NeedsALook[0]);

        Assert.Equal(["fix Canteens", "update Beasts False"], actions.Calls);
    }

    [Fact]
    public async Task RollBack_NeedsASnapshot_AndReportsWhatHappened()
    {
        var actions = new FakeActions();
        var review = new GameUpdateReviewViewModel(actions);
        Assert.False(review.CanRollBack);
        Assert.False(review.RollBackCommand.CanExecute(null));
        Assert.Contains("Nothing to roll back to", review.RollBackText);

        review.Snapshot = new LastKnownGoodSnapshot { CreatedAtUtc = DateTimeOffset.UtcNow };
        Assert.True(review.RollBackCommand.CanExecute(null));
        Assert.Contains("The game itself can't be rolled back", review.RollBackText);

        await review.RollBackCommand.ExecuteAsync(null);

        Assert.Equal(["roll back"], actions.Calls);
        Assert.Equal("Rolled back.", review.StatusMessage);
    }

    [Fact]
    public async Task RunAsync_ShowsEachStep_AndKeepsTheSnapshot()
    {
        var review = new GameUpdateReviewViewModel(new FakeActions());
        var steps = new FailingRefreshSteps();

        var result = await review.RunAsync(steps);

        Assert.False(result.Completed);
        Assert.True(review.ShowSteps);
        Assert.NotNull(review.Snapshot);
        Assert.True(review.CanRollBack);
        Assert.Equal(GameUpdateStepState.Done, review.Steps.Single(s => s.Step == GameUpdateStep.Snapshot).State);
        var refresh = review.Steps.Single(s => s.Step == GameUpdateStep.RefreshGameData);
        Assert.Equal(GameUpdateStepState.Failed, refresh.State);
        Assert.Equal("No game folder set.", refresh.Text);
        Assert.Equal(GameUpdateStepState.Waiting, review.Steps.Single(s => s.Step == GameUpdateStep.Install).State);
        Assert.Equal(result.Message, review.Summary);
    }

    private sealed class FailingRefreshSteps : IGameUpdateSteps
    {
        public Task<LastKnownGoodSnapshot> SnapshotAsync() => Task.FromResult(new LastKnownGoodSnapshot { CreatedAtUtc = DateTimeOffset.UtcNow });

        public Task RecordGameFingerprintsAsync() => Task.CompletedTask;

        public Task<GameUpdateStepResult> RefreshGameDataAsync() => Task.FromResult(GameUpdateStepResult.Failed("No game folder set."));

        public Task CheckModsAsync() => throw new InvalidOperationException("not reached");

        public Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync() => throw new InvalidOperationException("not reached");

        public Task CheckForModUpdatesAsync() => throw new InvalidOperationException("not reached");

        public Task<GameUpdateStepResult> RebuildAsync() => throw new InvalidOperationException("not reached");

        public bool IsGameRunning() => throw new InvalidOperationException("not reached");

        public Task<GameUpdateStepResult> InstallAsync() => throw new InvalidOperationException("not reached");
    }
}
