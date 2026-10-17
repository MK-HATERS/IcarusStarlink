namespace IcarusStarlink.App.Services;

/// <summary>The steps of Update everything, in the order they run.</summary>
public enum GameUpdateStep
{
    Snapshot,
    Fingerprints,
    RefreshGameData,
    CheckMods,
    FixSure,
    CheckUpdates,
    Merge,
    Install,
}

public enum GameUpdateStepState
{
    Waiting,
    Running,
    Done,
    Skipped,
    Failed,
}

/// <summary>One step's progress: which, how far along, and a plain line about it.</summary>
public sealed record GameUpdateProgress(GameUpdateStep Step, GameUpdateStepState State, string Text);

/// <summary>A step that can finish, be skipped (nothing to do, or the user said No), or fail, with a plain line about it.</summary>
public sealed record GameUpdateStepResult(GameUpdateStepState State, string Message)
{
    public static GameUpdateStepResult Done(string message = "") => new(GameUpdateStepState.Done, message);

    public static GameUpdateStepResult Skipped(string message) => new(GameUpdateStepState.Skipped, message);

    public static GameUpdateStepResult Failed(string message) => new(GameUpdateStepState.Failed, message);
}

/// <summary>
/// What Update everything needs done, one method per step — the app's version calls the pages'
/// own commands (Settings' Refresh game data, the Library's checks and fixes, Merge &amp; Install's
/// build and install); tests pass fakes.
/// </summary>
public interface IGameUpdateSteps
{
    /// <summary>Saves the last known good snapshot (before anything is changed).</summary>
    Task<LastKnownGoodSnapshot> SnapshotAsync();

    /// <summary>Records the game's own copies of the files mods replace, for the build on disk.</summary>
    Task RecordGameFingerprintsAsync();

    Task<GameUpdateStepResult> RefreshGameDataAsync();

    Task CheckModsAsync();

    /// <summary>Proven fixes only; what was fixed, per mod.</summary>
    Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync();

    Task CheckForModUpdatesAsync();

    Task<GameUpdateStepResult> RebuildAsync();

    bool IsGameRunning();

    Task<GameUpdateStepResult> InstallAsync();
}

/// <summary>How a run ended.</summary>
public sealed class GameUpdateRunResult
{
    /// <summary>Every step ran (Install may still have been left out: see InstallSkippedGameRunning).</summary>
    public bool Completed { get; init; }

    /// <summary>The step that failed; null when none did.</summary>
    public GameUpdateStep? FailedStep { get; init; }

    public string Message { get; init; } = "";

    /// <summary>The snapshot made first; null when even that failed (then nothing was changed).</summary>
    public LastKnownGoodSnapshot? Snapshot { get; init; }

    public IReadOnlyList<GameUpdateFixedMod> Fixed { get; init; } = [];

    /// <summary>Icarus was running, so the new merge was built but not installed.</summary>
    public bool InstallSkippedGameRunning { get; init; }

    public bool Installed { get; init; }
}

/// <summary>
/// Update everything, after a game update: snapshot → record the game's files → Refresh game data
/// → check every mod → fix sure problems → check for mod updates → build the merge → install it
/// (not while Icarus runs). Each step reports its progress; the first failure stops the run with
/// what went wrong. The snapshot is made before anything else, so Roll back is there whatever
/// happens after it.
/// </summary>
public static class GameUpdateOrchestrator
{
    public static string Describe(GameUpdateStep step) => step switch
    {
        GameUpdateStep.Snapshot => "Save your setup as it is now (to roll back to)",
        GameUpdateStep.Fingerprints => "Note the game files mods replace",
        GameUpdateStep.RefreshGameData => "Refresh game data",
        GameUpdateStep.CheckMods => "Check every mod against the new game",
        GameUpdateStep.FixSure => "Fix sure problems",
        GameUpdateStep.CheckUpdates => "Check for mod updates",
        GameUpdateStep.Merge => "Build the merge",
        GameUpdateStep.Install => "Install it in the game",
        _ => step.ToString(),
    };

    public static async Task<GameUpdateRunResult> RunAsync(IGameUpdateSteps steps, Action<GameUpdateProgress>? report = null)
    {
        LastKnownGoodSnapshot? snapshot = null;
        IReadOnlyList<GameUpdateFixedMod> fixedMods = [];
        var current = GameUpdateStep.Snapshot;

        void Report(GameUpdateStepState state, string text = "") => report?.Invoke(new GameUpdateProgress(current, state, text));

        GameUpdateRunResult Stop(string message)
        {
            Report(GameUpdateStepState.Failed, message);
            return new GameUpdateRunResult
            {
                FailedStep = current,
                Message = snapshot is null
                    ? $"{Describe(current)} didn't work: {message} Nothing was changed."
                    : $"{Describe(current)} didn't work: {message} The steps before it are done. Roll back puts your setup back as it was before the update run.",
                Snapshot = snapshot,
                Fixed = fixedMods,
            };
        }

        try
        {
            Report(GameUpdateStepState.Running);
            snapshot = await steps.SnapshotAsync();
            Report(GameUpdateStepState.Done);

            current = GameUpdateStep.Fingerprints;
            Report(GameUpdateStepState.Running);
            await steps.RecordGameFingerprintsAsync();
            Report(GameUpdateStepState.Done);

            current = GameUpdateStep.RefreshGameData;
            Report(GameUpdateStepState.Running);
            var refreshed = await steps.RefreshGameDataAsync();
            if (refreshed.State == GameUpdateStepState.Failed)
            {
                return Stop(refreshed.Message);
            }

            Report(GameUpdateStepState.Done, refreshed.Message);

            current = GameUpdateStep.CheckMods;
            Report(GameUpdateStepState.Running);
            await steps.CheckModsAsync();
            Report(GameUpdateStepState.Done);

            current = GameUpdateStep.FixSure;
            Report(GameUpdateStepState.Running);
            fixedMods = await steps.FixSureProblemsAsync();
            var fixCount = fixedMods.Sum(m => m.FixCount);
            Report(GameUpdateStepState.Done, fixCount == 0 ? "Nothing sure to fix." : $"Fixed {fixCount} thing(s) in {fixedMods.Count} mod(s).");

            current = GameUpdateStep.CheckUpdates;
            Report(GameUpdateStepState.Running);
            await steps.CheckForModUpdatesAsync();
            Report(GameUpdateStepState.Done);

            current = GameUpdateStep.Merge;
            Report(GameUpdateStepState.Running);
            var built = await steps.RebuildAsync();
            if (built.State == GameUpdateStepState.Failed)
            {
                return Stop(built.Message);
            }

            Report(built.State, built.Message);
            current = GameUpdateStep.Install;
            if (built.State == GameUpdateStepState.Skipped)
            {
                Report(GameUpdateStepState.Skipped, "Nothing was built.");
                return Finished("Done. " + built.Message);
            }

            if (steps.IsGameRunning())
            {
                const string running = "Icarus is running, so the new merge was built but not installed. Close the game, then click Copy to game on Merge & Install.";
                Report(GameUpdateStepState.Skipped, running);
                return Finished(running, installSkippedGameRunning: true);
            }

            Report(GameUpdateStepState.Running);
            var installed = await steps.InstallAsync();
            if (installed.State == GameUpdateStepState.Failed)
            {
                return Stop(installed.Message);
            }

            Report(installed.State, installed.Message);
            return installed.State == GameUpdateStepState.Done
                ? Finished("Done. Your mods are ready for the updated game.", installed: true)
                : Finished("Done, but the new merge wasn't installed: " + installed.Message);
        }
        catch (Exception ex)
        {
            return Stop(ex.Message);
        }

        GameUpdateRunResult Finished(string message, bool installSkippedGameRunning = false, bool installed = false) => new()
        {
            Completed = true,
            Message = message,
            Snapshot = snapshot,
            Fixed = fixedMods,
            InstallSkippedGameRunning = installSkippedGameRunning,
            Installed = installed,
        };
    }
}
