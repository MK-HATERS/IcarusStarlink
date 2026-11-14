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

/// <summary>What Keep off did: Done when nothing is left for the button to do, and a line about it.</summary>
public sealed record KeepOffResult(bool Done, string Message);

/// <summary>What the review's buttons do; the app's version reaches the Library and Merge &amp; Install, tests pass a fake.</summary>
public interface IGameUpdateReviewActions
{
    /// <summary>The mod's fix window (the same one as its warning badge).</summary>
    Task OpenFixWindowAsync(string folderName);

    /// <summary>The mod's Update (the Library's own: Database mods in place, Nexus mods through their page).</summary>
    Task GetUpdateAsync(string folderName, bool isUe4ss);

    /// <summary>Unticks the mod and builds and installs the merge without it. Returns whether that is done, and what happened in a line.</summary>
    Task<KeepOffResult> KeepOffAsync(string folderName);

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
    private string _text = "";

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

/// <summary>A mod in the Show me first list: what the sure fixes would change in it, and a tick to fix it or leave it.</summary>
public sealed partial class GameUpdateFixRow(GameUpdateFixCandidate candidate) : ObservableObject
{
    public string FolderName { get; } = candidate.FolderName;

    public string Name { get; } = candidate.Name;

    public string Tag { get; } = candidate.FixCount == 1 ? "1 sure fix" : $"{candidate.FixCount} sure fixes";

    public string Detail { get; } = string.Join("\n", candidate.Lines);

    /// <summary>Ticked: fixed on Continue. Unticked: left as it is.</summary>
    [ObservableProperty]
    private bool _isTicked = true;
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
    [NotifyPropertyChangedFor(nameof(CanRollBack), nameof(IsAllClear), nameof(WhyCantClose))]
    [NotifyCanExecuteChangedFor(nameof(RollBackCommand), nameof(ActCommand))]
    private bool _isRunning;

    /// <summary>The groups have been filled in (Load ran) at least once.</summary>
    private bool _loaded;

    /// <summary>The run on screen stopped before its last step: the groups weren't checked against all of it.</summary>
    private bool _runIncomplete;

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

    // --- Before Start: which steps to run ---

    /// <summary>The window is waiting for the ticks and Start (ChooseAsync): nothing has run yet.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isChoosing;

    /// <summary>Redraw the maps that changed: the same setting as the Map page's box.</summary>
    [ObservableProperty]
    private bool _redrawMaps;

    [ObservableProperty]
    private bool _checkForModUpdates = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTickInstall))]
    private bool _buildMerge = true;

    [ObservableProperty]
    private bool _install = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixShowFirst), nameof(FixByItself), nameof(FixDontFix))]
    private GameUpdateFixChoice _fixChoice = GameUpdateFixChoice.ShowFirst;

    /// <summary>Install needs a build: its box is off while Build the merge is unticked.</summary>
    public bool CanTickInstall => BuildMerge;

    public bool FixShowFirst
    {
        get => FixChoice == GameUpdateFixChoice.ShowFirst;
        set => PickFix(value, GameUpdateFixChoice.ShowFirst);
    }

    public bool FixByItself
    {
        get => FixChoice == GameUpdateFixChoice.FixByItself;
        set => PickFix(value, GameUpdateFixChoice.FixByItself);
    }

    public bool FixDontFix
    {
        get => FixChoice == GameUpdateFixChoice.DontFix;
        set => PickFix(value, GameUpdateFixChoice.DontFix);
    }

    private void PickFix(bool picked, GameUpdateFixChoice choice)
    {
        if (picked)
        {
            FixChoice = choice;
        }
    }

    /// <summary>What else Install would take out of the game's mods folder, listed under its box; null: nothing else.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallNote))]
    private string? _installNote;

    public bool HasInstallNote => !string.IsNullOrEmpty(InstallNote);

    /// <summary>
    /// Asked when Start is clicked, before anything runs: false keeps the window on the ticks (the
    /// app's version asks about other files Install would remove). Null: nothing to ask.
    /// </summary>
    public Func<GameUpdateOptions, bool>? ConfirmStart { get; set; }

    private TaskCompletionSource<GameUpdateOptions?>? _choice;

    partial void OnBuildMergeChanged(bool value)
    {
        if (!value)
        {
            Install = false;
        }
    }

    partial void OnInstallChanged(bool value)
    {
        // Nothing new to install without a build.
        if (value && !BuildMerge)
        {
            Install = false;
        }
    }

    /// <summary>The ticks as they are now.</summary>
    public GameUpdateOptions Options => new()
    {
        RedrawMaps = RedrawMaps,
        CheckForModUpdates = CheckForModUpdates,
        BuildMerge = BuildMerge,
        Install = Install && BuildMerge,
        Fix = FixChoice,
    };

    /// <summary>Sets the ticks (from what was chosen last time).</summary>
    public void LoadOptions(GameUpdateOptions options)
    {
        RedrawMaps = options.RedrawMaps;
        CheckForModUpdates = options.CheckForModUpdates;
        BuildMerge = options.BuildMerge;
        Install = options.Install && options.BuildMerge;
        FixChoice = options.Fix;
    }

    /// <summary>Shows the ticks and waits for Start. Null when the window is closed first: nothing runs.</summary>
    public Task<GameUpdateOptions?> ChooseAsync()
    {
        _choice = new TaskCompletionSource<GameUpdateOptions?>();
        Summary = "Choose what Update everything does, then click Start. Nothing is changed before that.";
        IsChoosing = true;
        return _choice.Task;
    }

    [RelayCommand(CanExecute = nameof(IsChoosing))]
    private void Start()
    {
        if (!IsChoosing)
        {
            return;
        }

        var options = Options;
        if (ConfirmStart is { } confirm && !confirm(options))
        {
            StatusMessage = "Not started. Untick Install it in the game to run without it, or click Start again.";
            return;
        }

        StatusMessage = null;
        IsChoosing = false;
        _choice?.TrySetResult(options);
    }

    /// <summary>The window was closed: a choice still open is cancelled (nothing runs).</summary>
    public void WindowClosed()
    {
        IsChoosing = false;
        _choice?.TrySetResult(null);
    }

    // --- Show me first: the fixes, before any is made ---

    /// <summary>The run is waiting on the list of fixes: nothing is changed until Continue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhyCantClose))]
    private bool _isReviewingFixes;

    public ObservableCollection<GameUpdateFixRow> FixesToReview { get; } = [];

    private TaskCompletionSource<GameUpdateFixDecision>? _fixDecision;

    /// <summary>Lists what would be fixed and waits for Continue, Skip fixes or Stop.</summary>
    public Task<GameUpdateFixDecision> ReviewFixesAsync(IReadOnlyList<GameUpdateFixCandidate> candidates)
    {
        FixesToReview.Clear();
        foreach (var candidate in candidates.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            FixesToReview.Add(new GameUpdateFixRow(candidate));
        }

        _fixDecision = new TaskCompletionSource<GameUpdateFixDecision>();
        Summary = "The check is done. Below is what the sure fixes would change. Nothing is changed until you click Continue.";
        IsReviewingFixes = true;
        return _fixDecision.Task;
    }

    private void AnswerFixes(GameUpdateFixDecision decision)
    {
        if (!IsReviewingFixes)
        {
            return;
        }

        IsReviewingFixes = false;
        FixesToReview.Clear();
        Summary = decision.Answer == GameUpdateFixAnswer.Stop ? "Stopping…" : "Getting your mods ready for the updated game…";
        _fixDecision?.TrySetResult(decision);
    }

    /// <summary>Fixes the ticked mods (each backed up first), then goes on. None ticked: the same as Skip fixes.</summary>
    [RelayCommand]
    private void ContinueWithFixes() => AnswerFixes(GameUpdateFixDecision.Fix(FixesToReview.Where(r => r.IsTicked).Select(r => r.FolderName)));

    [RelayCommand]
    private void SkipFixes() => AnswerFixes(GameUpdateFixDecision.Skip);

    [RelayCommand]
    private void StopRun() => AnswerFixes(GameUpdateFixDecision.Stop);

    /// <summary>Why the window can't be closed right now; null when it can.</summary>
    public string? WhyCantClose => !IsRunning
        ? null
        : IsReviewingFixes
            ? "Waiting for you: click Continue, Skip fixes or Stop first."
            : "Still working: close this once the steps are done.";

    /// <summary>Runs Update everything with these steps, showing each one as it goes. Options null: the ticks as they are now.</summary>
    public async Task<GameUpdateRunResult> RunAsync(IGameUpdateSteps steps, GameUpdateOptions? options = null)
    {
        IsChoosing = false;
        ShowSteps = true;
        IsRunning = true;
        Summary = "Getting your mods ready for the updated game…";
        try
        {
            var result = await GameUpdateOrchestrator.RunAsync(steps, OnProgress, options ?? Options, ReviewFixesAsync);
            _runIncomplete = !result.Completed;
            Snapshot = result.Snapshot;
            Summary = result.Message;
            return result;
        }
        finally
        {
            IsReviewingFixes = false;
            IsRunning = false;
        }
    }

    /// <summary>Fills the three groups from what the run fixed and the mods as they are now.</summary>
    public void Load(IReadOnlyList<GameUpdateFixedMod> fixedMods, IReadOnlyList<GameUpdateModInfo> mods)
    {
        _loaded = true;
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

    /// <summary>
    /// Nothing fixed, nothing to look at, nothing at risk, once the groups are filled in and any run
    /// on screen finished. False before then, so the window never claims all clear while the steps
    /// run, when the check couldn't be done, or after a run that stopped early.
    /// </summary>
    public bool IsAllClear => _loaded && !IsRunning && !_runIncomplete && !HasFixedForYou && !HasNeedsALook && !HasHighRisk;

    /// <summary>
    /// Off while Roll back runs: a click used to do nothing at all. The row's own CanAct is also
    /// bound to the button, since its changes don't re-query this.
    /// </summary>
    private bool CanActOn(GameUpdateReviewRow? row) => row is { CanAct: true } && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanActOn), FlowExceptionsToTaskScheduler = true)]
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

    /// <summary>
    /// Unticks the mod and rebuilds without it. The row says "Kept off" only when that finished;
    /// otherwise (Icarus running, a failed build, not installed) it says what happened and the
    /// button stays, to try again. It used to be marked done whatever happened.
    /// </summary>
    private async Task KeepOffAsync(GameUpdateReviewRow row)
    {
        IsRunning = true;
        try
        {
            StatusMessage = $"Turning '{row.Name}' off and building the merge without it…";
            var result = await actions.KeepOffAsync(row.FolderName);
            if (result.Done)
            {
                row.IsActionDone = true;
                row.ActionLabel = "Kept off";
            }

            row.Result = result.Message;
            StatusMessage = result.Message;
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
