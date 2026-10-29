using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One mod as the review sees it, read from its Library row.</summary>
/// <param name="ReviewCount">Things the last check found that need a look (not sure enough to fix by themselves).</param>
/// <param name="IsTicked">In the merge (or, a UE4SS mod, on): only those can be affected in game.</param>
public sealed record GameUpdateModInfo(
    string FolderName, string Name, bool IsUe4ss, int ReviewCount, bool HasUpdate, string? Version, string? LatestVersion, ModFileRisk? Risk, bool IsTicked);

/// <summary>What the review's buttons do; the app's version reaches the Library and Merge &amp; Install, tests pass a fake.</summary>
public interface IGameUpdateReviewActions
{
    /// <summary>The mod's fix window (the same one as its warning badge).</summary>
    Task OpenFixWindowAsync(string folderName);

    /// <summary>The mod's Update (the Library's own: Database mods in place, Nexus mods through their page).</summary>
    Task GetUpdateAsync(string folderName, bool isUe4ss);

    /// <summary>Unticks the mod and builds and installs the merge without it. Returns what happened, in a line.</summary>
    Task<string> KeepOffAsync(string folderName);

    /// <summary>Puts the snapshot back. Returns what happened, or null when the user said No.</summary>
    Task<string?> RollBackAsync(LastKnownGoodSnapshot snapshot);
}

/// <summary>A step of the run as shown: waiting, working, done, skipped or failed, with a line about it.</summary>
public sealed partial class GameUpdateStepRow(GameUpdateStep step) : ObservableObject
{
    public GameUpdateStep Step { get; } = step;

    public string Title { get; } = GameUpdateOrchestrator.Describe(step);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsFailed), nameof(IsDone))]
    private GameUpdateStepState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    private string _text = "";

    public bool HasText => Text.Length > 0;

    public bool IsFailed => State == GameUpdateStepState.Failed;

    public bool IsDone => State == GameUpdateStepState.Done;

    public string StateLabel => State switch
    {
        GameUpdateStepState.Running => "Working…",
        GameUpdateStepState.Done => "Done",
        GameUpdateStepState.Skipped => "Skipped",
        GameUpdateStepState.Failed => "Didn't work",
        _ => "",
    };
}

/// <summary>One line in a review group: the mod, why it's here, and the one thing to do about it.</summary>
public sealed partial class GameUpdateReviewRow : ObservableObject
{
    public required string FolderName { get; init; }

    public required string Name { get; init; }

    public bool IsUe4ss { get; init; }

    /// <summary>"Out of date", "Changed by the update", "2 sure fixes"… the short tag before the details.</summary>
    public string Tag { get; init; } = "";

    public string Detail { get; init; } = "";

    /// <summary>Which button: the fix window, Update, or Keep off.</summary>
    public GameUpdateRowAction Action { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    private string _actionLabel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    private bool _isActionDone;

    [ObservableProperty]
    private string? _result;

    public bool CanAct => !IsActionDone && ActionLabel.Length > 0;
}

public enum GameUpdateRowAction
{
    OpenFix,
    Update,
    KeepOff,
}

/// <summary>
/// "Icarus updated — here's what changed": the steps of Update everything as they run, then what
/// it did and what's left — Fixed for you (proven fixes, per mod), Needs a look (things to look
/// over, newer versions out) and High risk (mods replacing game files the update changed, with
/// the proof when there is one). Roll back puts the mod setup back as it was before the run.
/// </summary>
public sealed partial class GameUpdateReviewViewModel(IGameUpdateReviewActions actions) : ObservableObject
{
    public string WindowTitle => "Icarus updated — here's what changed";

    public ObservableCollection<GameUpdateStepRow> Steps { get; } =
        [.. Enum.GetValues<GameUpdateStep>().Select(s => new GameUpdateStepRow(s))];

    public ObservableCollection<GameUpdateReviewRow> FixedForYou { get; } = [];

    public ObservableCollection<GameUpdateReviewRow> NeedsALook { get; } = [];

    public ObservableCollection<GameUpdateReviewRow> HighRisk { get; } = [];

    /// <summary>Steps are listed while a run is on screen (not when the review is opened again later).</summary>
    [ObservableProperty]
    private bool _showSteps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRollBack), nameof(IsNotRunning))]
    [NotifyCanExecuteChangedFor(nameof(RollBackCommand))]
    private bool _isRunning;

    public bool IsNotRunning => !IsRunning;

    /// <summary>The run's outcome, or what the review is of.</summary>
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>A note shown above the groups (the game files are from before the update until Starlink restarts, say).</summary>
    [ObservableProperty]
    private string? _note;

    /// <summary>What Roll back puts back; null: there's nothing to roll back to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRollBack), nameof(RollBackText))]
    [NotifyCanExecuteChangedFor(nameof(RollBackCommand))]
    private LastKnownGoodSnapshot? _snapshot;

    public bool CanRollBack => Snapshot is not null && !IsRunning;

    public string RollBackText => Snapshot is { } s
        ? $"Roll back puts your mod setup back as it was on {s.CreatedAtUtc.ToLocalTime():g}: the merged pak in the game, what's ticked and in what order, merge options, conflict picks and which UE4SS mods are on. The game itself can't be rolled back, only your mod setup."
        : "Nothing to roll back to yet: Starlink saves your setup each time you start Update everything.";

    public bool HasFixedForYou => FixedForYou.Count > 0;

    public bool HasNeedsALook => NeedsALook.Count > 0;

    public bool HasHighRisk => HighRisk.Count > 0;

    /// <summary>Set by the window.</summary>
    public Action? Close { get; set; }

    /// <summary>One step's progress from the run.</summary>
    public void OnProgress(GameUpdateProgress progress)
    {
        var row = Steps.First(s => s.Step == progress.Step);
        row.State = progress.State;
        if (progress.Text.Length > 0 || progress.State == GameUpdateStepState.Running)
        {
            row.Text = progress.Text;
        }
    }

    /// <summary>Runs Update everything with these steps, showing each one as it goes.</summary>
    public async Task<GameUpdateRunResult> RunAsync(IGameUpdateSteps steps)
    {
        ShowSteps = true;
        IsRunning = true;
        Summary = "Getting your mods ready for the updated game…";
        try
        {
            var result = await GameUpdateOrchestrator.RunAsync(steps, OnProgress);
            Snapshot = result.Snapshot;
            Summary = result.Message;
            return result;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Fills the three groups from what the run fixed and the mods as they are now.</summary>
    public void Load(IReadOnlyList<GameUpdateFixedMod> fixedMods, IReadOnlyList<GameUpdateModInfo> mods)
    {
        FixedForYou.Clear();
        NeedsALook.Clear();
        HighRisk.Clear();

        foreach (var mod in fixedMods.Where(m => m.FixCount > 0).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            FixedForYou.Add(new GameUpdateReviewRow
            {
                FolderName = mod.FolderName,
                Name = mod.Name,
                Tag = mod.FixCount == 1 ? "1 proven fix" : $"{mod.FixCount} proven fixes",
                Detail = "Old copies of game data and broken links put right, backed up first.",
                Action = GameUpdateRowAction.OpenFix,
                ActionLabel = "Open fix window",
            });
        }

        foreach (var mod in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (mod.ReviewCount > 0 && !mod.IsUe4ss)
            {
                NeedsALook.Add(new GameUpdateReviewRow
                {
                    FolderName = mod.FolderName,
                    Name = mod.Name,
                    Tag = mod.ReviewCount == 1 ? "1 thing to look over" : $"{mod.ReviewCount} things to look over",
                    Detail = "Not sure enough to change by itself: the fix window shows the mod's value next to the game's.",
                    Action = GameUpdateRowAction.OpenFix,
                    ActionLabel = "Open fix window",
                });
            }

            if (mod.HasUpdate)
            {
                NeedsALook.Add(new GameUpdateReviewRow
                {
                    FolderName = mod.FolderName,
                    Name = mod.Name,
                    IsUe4ss = mod.IsUe4ss,
                    Tag = "Newer version out",
                    Detail = mod.LatestVersion is { Length: > 0 } latest
                        ? $"You have {(string.IsNullOrEmpty(mod.Version) ? "an older version" : mod.Version)}; {latest} is out. A version made after the update is the surest fix."
                        : "A newer version is out. A version made after the update is the surest fix.",
                    Action = GameUpdateRowAction.Update,
                    ActionLabel = mod.IsUe4ss ? "Open on Nexus" : "Update",
                });
            }

            // Only what's in the game can break it; a proven case first.
            if (mod.IsTicked && !mod.IsUe4ss && mod.Risk is { IsChangedByGameUpdate: true } risk)
            {
                HighRisk.Add(new GameUpdateReviewRow
                {
                    FolderName = mod.FolderName,
                    Name = mod.Name,
                    Tag = risk.IsOutOfDate ? "Out of date" : "Changed by the update",
                    Detail = string.Join("\n", risk.GameUpdateLines)
                        + (risk.IsOutOfDate ? "" : "\nThis mod replaces that file with its own copy, so the game's change may not reach your game while it's on."),
                    Action = GameUpdateRowAction.KeepOff,
                    ActionLabel = "Keep off until updated",
                });
            }
        }

        // Proven cases at the top of High risk.
        var ordered = HighRisk.OrderByDescending(r => r.Tag == "Out of date").ToList();
        HighRisk.Clear();
        foreach (var row in ordered)
        {
            HighRisk.Add(row);
        }

        OnPropertyChanged(nameof(HasFixedForYou));
        OnPropertyChanged(nameof(HasNeedsALook));
        OnPropertyChanged(nameof(HasHighRisk));
        OnPropertyChanged(nameof(IsAllClear));
    }

    /// <summary>Nothing fixed, nothing to look at, nothing at risk.</summary>
    public bool IsAllClear => !HasFixedForYou && !HasNeedsALook && !HasHighRisk;

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ActAsync(GameUpdateReviewRow? row)
    {
        if (row is null || !row.CanAct || IsRunning)
        {
            return;
        }

        try
        {
            switch (row.Action)
            {
                case GameUpdateRowAction.OpenFix:
                    await actions.OpenFixWindowAsync(row.FolderName);
                    break;
                case GameUpdateRowAction.Update:
                    await actions.GetUpdateAsync(row.FolderName, row.IsUe4ss);
                    break;
                case GameUpdateRowAction.KeepOff:
                    await KeepOffAsync(row);
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"That didn't work: {ex.Message}";
        }
    }

    /// <summary>Unticks the mod and rebuilds without it; the row says it's off.</summary>
    private async Task KeepOffAsync(GameUpdateReviewRow row)
    {
        IsRunning = true;
        try
        {
            StatusMessage = $"Turning '{row.Name}' off and building the merge without it…";
            var result = await actions.KeepOffAsync(row.FolderName);
            row.IsActionDone = true;
            row.ActionLabel = "Kept off";
            row.Result = result;
            StatusMessage = result;
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRollBack), FlowExceptionsToTaskScheduler = true)]
    private async Task RollBackAsync()
    {
        if (Snapshot is not { } snapshot)
        {
            return;
        }

        IsRunning = true;
        try
        {
            if (await actions.RollBackAsync(snapshot) is { } result)
            {
                StatusMessage = result;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Roll back didn't work: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void CloseWindow() => Close?.Invoke();
}
