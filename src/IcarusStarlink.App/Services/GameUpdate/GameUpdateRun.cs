using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>The steps of Update everything, in the order they run.</summary>
public enum GameUpdateStep
{
    Snapshot,
    Fingerprints,
    RefreshGameData,
    Maps,
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

/// <summary>What Update everything does about the sure problems the check finds.</summary>
public enum GameUpdateFixChoice
{
    /// <summary>Stop after the check and list what would be fixed, mod by mod; nothing changes until Continue.</summary>
    ShowFirst,

    /// <summary>Make every proven fix without asking (each mod backed up first).</summary>
    FixByItself,

    /// <summary>Change no mod.</summary>
    DontFix,
}

/// <summary>
/// What the user ticked before Start. Saving the setup, noting the game files, Refresh game data
/// and checking every mod always run; the rest can be left out.
/// </summary>
public sealed record GameUpdateOptions
{
    public bool RedrawMaps { get; init; } = true;

    public bool CheckForModUpdates { get; init; } = true;

    public bool BuildMerge { get; init; } = true;

    /// <summary>Only with BuildMerge: there is nothing new to install without a build.</summary>
    public bool Install { get; init; } = true;

    public GameUpdateFixChoice Fix { get; init; } = GameUpdateFixChoice.ShowFirst;

    /// <summary>Every step, fixes made without asking: a run with nobody to ask.</summary>
    public static GameUpdateOptions Everything { get; } = new() { Fix = GameUpdateFixChoice.FixByItself };

    /// <summary>What was ticked last time (first time: everything but the maps, fixes shown first).</summary>
    public static GameUpdateOptions FromSettings(AppSettings settings) => new()
    {
        RedrawMaps = settings.RedrawMapsDuringGameUpdate,
        CheckForModUpdates = settings.GameUpdateChecksForModUpdates,
        BuildMerge = settings.GameUpdateBuildsMerge,
        Install = settings.GameUpdateInstalls && settings.GameUpdateBuildsMerge,
        Fix = settings.GameUpdateFix switch
        {
            "fix" => GameUpdateFixChoice.FixByItself,
            "none" => GameUpdateFixChoice.DontFix,
            _ => GameUpdateFixChoice.ShowFirst,
        },
    };

    /// <summary>Writes the ticks into the settings (the caller saves them). The maps tick is the Map page's own setting.</summary>
    public void CopyTo(AppSettings settings)
    {
        settings.RedrawMapsDuringGameUpdate = RedrawMaps;
        settings.GameUpdateChecksForModUpdates = CheckForModUpdates;
        settings.GameUpdateBuildsMerge = BuildMerge;
        settings.GameUpdateInstalls = Install && BuildMerge;
        settings.GameUpdateFix = Fix switch
        {
            GameUpdateFixChoice.FixByItself => "fix",
            GameUpdateFixChoice.DontFix => "none",
            _ => "show",
        };
    }
}

/// <summary>A mod the sure fixes would change, with what they would do in plain lines.</summary>
public sealed record GameUpdateFixCandidate(string FolderName, string Name, int FixCount, IReadOnlyList<string> Lines)
{
    /// <summary>
    /// What Fix sure problems would change in a mod, from its last check: one line per kind of
    /// problem, with how many and in which tables. Null when there is nothing sure to fix.
    /// </summary>
    public static GameUpdateFixCandidate? From(string folderName, string name, ModHealthReport? report)
    {
        var sure = report?.Issues.Where(i => i.IsAutomatic).ToList() ?? [];
        if (sure.Count == 0)
        {
            return null;
        }

        var lines = sure.GroupBy(i => i.Kind).OrderByDescending(g => g.Count()).Select(group =>
        {
            var tables = group.Select(i => System.IO.Path.GetFileNameWithoutExtension(i.CurrentFile))
                .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var where = tables.Count switch
            {
                0 => "",
                <= 3 => $" in {string.Join(", ", tables)}",
                _ => $" in {string.Join(", ", tables.Take(3))} and {tables.Count - 3} more",
            };
            return $"{ViewModels.ModFixViewModel.KindTitle(group.Key)}: {group.Count()}{where}";
        }).ToList();
        return new GameUpdateFixCandidate(folderName, name, sure.Count, lines);
    }
}

public enum GameUpdateFixAnswer
{
    /// <summary>Fix the mods named, then go on.</summary>
    Fix,

    /// <summary>Fix nothing, go on with the later steps.</summary>
    Skip,

    /// <summary>Fix nothing and end the run here.</summary>
    Stop,
}

/// <summary>What the user chose after seeing the fixes: which mods to fix (Fix), or none.</summary>
public sealed record GameUpdateFixDecision(GameUpdateFixAnswer Answer, IReadOnlyCollection<string> Folders)
{
    public static GameUpdateFixDecision Fix(IEnumerable<string> folders) => new(GameUpdateFixAnswer.Fix, [.. folders]);

    public static GameUpdateFixDecision Skip { get; } = new(GameUpdateFixAnswer.Skip, []);

    public static GameUpdateFixDecision Stop { get; } = new(GameUpdateFixAnswer.Stop, []);
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

    /// <summary>True when the extracted game data already matches the game's data.pak (Refresh game data ran on this version), so the refresh step has nothing to do. False when it doesn't or can't be told.</summary>
    Task<bool> IsGameDataCurrentAsync() => Task.FromResult(false);

    Task<GameUpdateStepResult> RefreshGameDataAsync();

    /// <summary>The Map page's Reload (when its Redraw during Update everything box is ticked): redraws only the maps the update changed. Never fails the run (Skipped with why).</summary>
    Task<GameUpdateStepResult> RedrawMapsAsync() => Task.FromResult(new GameUpdateStepResult(GameUpdateStepState.Skipped, ""));

    Task CheckModsAsync();

    /// <summary>Proven fixes only; what was fixed, per mod.</summary>
    Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync();

    /// <summary>The mods the proven fixes would change and what they would do, without changing anything (Show me first).</summary>
    Task<IReadOnlyList<GameUpdateFixCandidate>> ListSureFixesAsync() => Task.FromResult<IReadOnlyList<GameUpdateFixCandidate>>([]);

    /// <summary>Proven fixes in these mods only (the ones left ticked after Show me first). Fixes nothing unless implemented.</summary>
    Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync(IReadOnlyCollection<string> folders) => Task.FromResult<IReadOnlyList<GameUpdateFixedMod>>([]);

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

    /// <summary>The user clicked Stop when shown the fixes: nothing was fixed and the later steps didn't run.</summary>
    public bool Stopped { get; init; }

    /// <summary>Build the merge or Install was unticked, so the game still has the merge from before.</summary>
    public bool MergeLeftOut { get; init; }

    public string Message { get; init; } = "";

    /// <summary>The snapshot made first; null when even that failed (then nothing was changed).</summary>
    public LastKnownGoodSnapshot? Snapshot { get; init; }

    public IReadOnlyList<GameUpdateFixedMod> Fixed { get; init; } = [];

    /// <summary>Icarus was running, so the new merge was built but not installed.</summary>
    public bool InstallSkippedGameRunning { get; init; }

    public bool Installed { get; init; }

    /// <summary>
    /// The game update is handled, so the Library's "Icarus updated" bar can go: every step ran and
    /// the new merge wasn't left out of the game because Icarus was running (the game would keep
    /// the old merge, with whole tables from before the update) or because the build or the install
    /// was unticked.
    /// </summary>
    public bool ReadyForUpdatedGame => Completed && !InstallSkippedGameRunning && !MergeLeftOut;
}

/// <summary>
/// Update everything, after a game update: snapshot → record the game's files → Refresh game data
/// → check every mod → fix sure problems → check for mod updates → build the merge → install it
/// (not while Icarus runs). Each step reports its progress; the first failure stops the run with
/// what went wrong. The snapshot is made before anything else, so Roll back is there whatever
/// happens after it. Refresh game data is skipped when the extracted data already matches the
/// game's data.pak: extracting again would only save an empty Weekly Changes report.
/// The steps the user unticked (GameUpdateOptions) are reported Skipped. The run itself asks no
/// question in a dialog: with Show me first it waits on reviewFixes (the window lists the fixes)
/// and changes no mod until it answers, then fixes only the mods whose fixes are still the ones
/// listed. The steps are the pages' own commands; the build's and the install's questions are
/// settled before Start (GameUpdateCoordinator).
/// </summary>
public static class GameUpdateOrchestrator
{
    public static string Describe(GameUpdateStep step) => step switch
    {
        GameUpdateStep.Snapshot => "Save your setup as it is now (to roll back to)",
        GameUpdateStep.Fingerprints => "Note the game files mods replace",
        GameUpdateStep.RefreshGameData => "Refresh game data",
        GameUpdateStep.Maps => "Redraw the maps that changed",
        GameUpdateStep.CheckMods => "Check every mod against the new game",
        GameUpdateStep.FixSure => "Fix sure problems",
        GameUpdateStep.CheckUpdates => "Check for mod updates",
        GameUpdateStep.Merge => "Build the merge",
        GameUpdateStep.Install => "Install it in the game",
        _ => step.ToString(),
    };

    private const string LeftOut = "Left out: you unticked it.";

    /// <param name="options">What to run; null: every step, fixes made without asking.</param>
    /// <param name="reviewFixes">Show me first: shown the fixes, answers which mods to fix, to skip them, or to stop. Null: nothing can be shown, so nothing is fixed.</param>
    public static async Task<GameUpdateRunResult> RunAsync(
        IGameUpdateSteps steps, Action<GameUpdateProgress>? report = null, GameUpdateOptions? options = null,
        Func<IReadOnlyList<GameUpdateFixCandidate>, Task<GameUpdateFixDecision>>? reviewFixes = null)
    {
        options ??= GameUpdateOptions.Everything;
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
            // Refresh game data already ran on this version (Settings' "Icarus has been updated" notice,
            // or an earlier run): extracting again finds no changes, and that empty Weekly Changes
            // report would be the one the page opens on.
            if (await steps.IsGameDataCurrentAsync())
            {
                Report(GameUpdateStepState.Done, "Game data already matches this version of Icarus.");
            }
            else
            {
                var refreshed = await steps.RefreshGameDataAsync();
                if (refreshed.State == GameUpdateStepState.Failed)
                {
                    return Stop(refreshed.Message);
                }

                Report(GameUpdateStepState.Done, refreshed.Message);
            }

            // The maps are only for looking at: a map that can't be drawn never stops the run.
            current = GameUpdateStep.Maps;
            Report(GameUpdateStepState.Running);
            GameUpdateStepResult maps;
            try
            {
                maps = options.RedrawMaps
                    ? await steps.RedrawMapsAsync()
                    : GameUpdateStepResult.Skipped("Left out: the Map page says which maps changed, and its Reload redraws them.");
            }
            catch (Exception ex)
            {
                // Thrown, not returned as Failed: still only the maps, so the run goes on.
                maps = GameUpdateStepResult.Failed($"The maps couldn't be redrawn: {ex.Message}");
            }

            Report(maps.State == GameUpdateStepState.Failed ? GameUpdateStepState.Skipped : maps.State, maps.Message);

            current = GameUpdateStep.CheckMods;
            Report(GameUpdateStepState.Running);
            await steps.CheckModsAsync();
            Report(GameUpdateStepState.Done);

            current = GameUpdateStep.FixSure;
            Report(GameUpdateStepState.Running);
            if (options.Fix == GameUpdateFixChoice.DontFix)
            {
                Report(GameUpdateStepState.Skipped, "Left out: you chose Don't fix. No mod was changed.");
            }
            else if (options.Fix == GameUpdateFixChoice.FixByItself)
            {
                fixedMods = await steps.FixSureProblemsAsync();
                ReportFixed();
            }
            else
            {
                // Show me first: nothing is changed until the user has seen the list and answered.
                var candidates = await steps.ListSureFixesAsync();
                if (candidates.Count == 0)
                {
                    Report(GameUpdateStepState.Done, "Nothing sure to fix.");
                }
                else if (reviewFixes is null)
                {
                    Report(GameUpdateStepState.Skipped, "Not fixed: the fixes couldn't be shown first. No mod was changed.");
                }
                else
                {
                    Report(GameUpdateStepState.Running, $"Waiting for you: {Mods(candidates.Count)} with sure fixes. Nothing is changed until you click Continue.");
                    var decision = await reviewFixes(candidates);
                    if (decision.Answer == GameUpdateFixAnswer.Stop)
                    {
                        Report(GameUpdateStepState.Skipped, "You stopped here. No mod was changed.");
                        var notRun = new List<string>();
                        foreach (var later in new[] { GameUpdateStep.CheckUpdates, GameUpdateStep.Merge, GameUpdateStep.Install })
                        {
                            current = later;
                            Report(GameUpdateStepState.Skipped, "Not run: you stopped.");
                            notRun.Add(Describe(later));
                        }

                        return new GameUpdateRunResult
                        {
                            Stopped = true,
                            Message = $"Stopped before fixing: no mod was changed. Not run: {string.Join(", ", notRun)}. Click Update everything again when you're ready.",
                            Snapshot = snapshot,
                        };
                    }

                    if (decision.Answer == GameUpdateFixAnswer.Skip || decision.Folders.Count == 0)
                    {
                        Report(GameUpdateStepState.Skipped, "Not fixed: you chose Skip fixes. No mod was changed.");
                    }
                    else
                    {
                        // The main window stayed usable while the list was shown: a mod edited,
                        // checked or converted again meanwhile has other fixes than the ones the
                        // user saw. Only mods whose fixes are still the ones listed are fixed.
                        var now = (await steps.ListSureFixesAsync()).ToDictionary(c => c.FolderName, StringComparer.OrdinalIgnoreCase);
                        var same = new List<string>();
                        var changed = new List<string>();
                        foreach (var listed in candidates.Where(c => decision.Folders.Contains(c.FolderName, StringComparer.OrdinalIgnoreCase)))
                        {
                            if (now.TryGetValue(listed.FolderName, out var asNow) && asNow.FixCount == listed.FixCount && asNow.Lines.SequenceEqual(listed.Lines))
                            {
                                same.Add(listed.FolderName);
                            }
                            else
                            {
                                changed.Add(listed.Name);
                            }
                        }

                        var notFixed = changed.Count > 0 ? $"Not fixed, changed while you were looking: {string.Join(", ", changed)}." : "";
                        if (same.Count == 0)
                        {
                            Report(GameUpdateStepState.Skipped, $"{notFixed} No mod was changed.");
                        }
                        else
                        {
                            Report(GameUpdateStepState.Running, $"Fixing {Mods(same.Count)}…");
                            fixedMods = await steps.FixSureProblemsAsync(same);
                            ReportFixed(notFixed);
                        }
                    }
                }
            }

            current = GameUpdateStep.CheckUpdates;
            if (options.CheckForModUpdates)
            {
                Report(GameUpdateStepState.Running);
                await steps.CheckForModUpdatesAsync();
                Report(GameUpdateStepState.Done);
            }
            else
            {
                Report(GameUpdateStepState.Skipped, LeftOut);
            }

            current = GameUpdateStep.Merge;
            if (!options.BuildMerge)
            {
                Report(GameUpdateStepState.Skipped, LeftOut);
                current = GameUpdateStep.Install;
                Report(GameUpdateStepState.Skipped, "Nothing was built.");
                return Finished(
                    "Done, but the merge wasn't built: you left that step out. The game still has the merge from before. Click Install on Merge & Install when you're ready.",
                    mergeLeftOut: true);
            }

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

            if (!options.Install)
            {
                Report(GameUpdateStepState.Skipped, LeftOut);
                return Finished(
                    "Done. The new merge is built but not in the game: you left Install out. Copy to game on Merge & Install (under Advanced actions) puts it in.",
                    mergeLeftOut: true);
            }

            if (steps.IsGameRunning())
            {
                const string running = "Icarus is running, so the new merge was built but not installed. Close the game, then click Update everything again to install it (the Library's bar stays until it's in the game).";
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

        void ReportFixed(string notFixed = "")
        {
            var fixCount = fixedMods.Sum(m => m.FixCount);
            var done = fixCount == 0 ? "Nothing sure to fix." : $"Fixed {fixCount} thing(s) in {fixedMods.Count} mod(s).";
            Report(GameUpdateStepState.Done, notFixed.Length > 0 ? $"{done} {notFixed}" : done);
        }

        static string Mods(int count) => count == 1 ? "1 mod" : $"{count} mods";

        GameUpdateRunResult Finished(string message, bool installSkippedGameRunning = false, bool installed = false, bool mergeLeftOut = false) => new()
        {
            Completed = true,
            Message = message,
            Snapshot = snapshot,
            Fixed = fixedMods,
            InstallSkippedGameRunning = installSkippedGameRunning,
            Installed = installed,
            MergeLeftOut = mergeLeftOut,
        };
    }
}
