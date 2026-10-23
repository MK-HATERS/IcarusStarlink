namespace IcarusStarlink.Updater;

/// <summary>
/// Orchestrates one Apply attempt and its aftermath — extracted out of Program.cs's own top-level
/// statements so this decision logic (what happens after Apply succeeds, fails-but-rolls-back, or
/// fails worse) is unit-testable the same way UpdateApplier.Apply itself already is, rather than
/// only reachable by actually running the compiled exe as a subprocess.
/// </summary>
public static class UpdateRunner
{
    /// <summary>NotStarted: the app never closed, so nothing was copied (the app reads every outcome but Applied as "not installed").</summary>
    public enum Outcome { Applied, RolledBack, RollbackIncomplete, NotStarted }

    /// <summary>Relaunched reflects whatever the caller's own relaunch callback reported — Run itself never inspects why a relaunch did or didn't happen.</summary>
    public sealed record Result(Outcome Outcome, bool Relaunched, string? BackupDirectory);

    /// <summary>
    /// A real bug found live (a user's own bug report: the app just closed and never came back,
    /// read as "install failed" with no explanation) — relaunch is now called on EVERY outcome,
    /// not just a fully successful update. A rolled-back failure leaves installDirectory exactly as
    /// it was, genuinely safe to relaunch; even the worse RollbackIncomplete case is worth a
    /// best-effort relaunch, since a still-mostly-intact install beats one the user thinks vanished.
    /// relaunch is a caller-supplied callback (real implementation: Process.Start) so this stays
    /// testable without actually launching anything.
    /// </summary>
    /// <param name="recordOutcome">
    /// Called with the outcome and a plain sentence for the user BEFORE the relaunch, so the app
    /// that comes back can say what happened (the updater has no window of its own, and a
    /// rolled-back update used to relaunch the old version without a word).
    /// </param>
    public static Result Run(string installDirectory, string newFilesDirectory, Action<string> log, Func<bool> relaunch,
        Action<Outcome, string>? recordOutcome = null)
    {
        void Record(Outcome outcome, string message) => RecordSafely(recordOutcome, outcome, message, log);

        try
        {
            UpdateApplier.Apply(installDirectory, newFilesDirectory, log);
        }
        catch (UpdateRollbackIncompleteException ex)
        {
            log($"Update FAILED: {ex.Message}");
            Record(Outcome.RollbackIncomplete, $"The update failed and couldn't be fully undone, so some files may be missing. A copy of the replaced files is in {ex.BackupDirectory}. {ex.Message}");
            return new Result(Outcome.RollbackIncomplete, relaunch(), ex.BackupDirectory);
        }
        catch (Exception ex)
        {
            // Rollback fully restored the old install — genuinely safe to use. Actionable guidance
            // for the single most common real cause: an access-denied write into the install
            // directory usually means it's sitting somewhere Windows reserves for admin-only writes
            // (Program Files, Program Files (x86), the Windows folder itself).
            var guidance = ex is UnauthorizedAccessException
                ? " This usually means IcarusStarlink is installed somewhere Windows reserves for administrators only (e.g. Program Files) — move it to a folder you own (Desktop, Documents, a dedicated folder outside Program Files), or run it as Administrator."
                : "";
            log($"Update FAILED (old install restored, relaunching it): {ex.Message}{guidance}");
            Record(Outcome.RolledBack, $"The update didn't install, so your previous version was put back: {ex.Message}{guidance}");
            return new Result(Outcome.RolledBack, relaunch(), null);
        }

        log("Update applied — relaunching.");
        Record(Outcome.Applied, "The update installed.");
        return new Result(Outcome.Applied, relaunch(), null);
    }

    /// <summary>
    /// The app never closed (stuck on shutdown, or a dialog left open), so nothing was copied. The
    /// updater used to just exit here: no note for the app and no relaunch, so a user who'd closed
    /// the stuck window was left with no app and no reason (review 2026-10-23). Now it says why and
    /// starts the app again; when the old one is in fact still open, the new start just brings it
    /// to the front (single instance).
    /// </summary>
    public static Result GiveUpWaiting(TimeSpan waited, Action<string> log, Func<bool> relaunch, Action<Outcome, string>? recordOutcome = null)
    {
        log($"The app didn't close within {waited.TotalMinutes:0.#} minute(s) — not installing, nothing was changed. Relaunching it.");
        RecordSafely(recordOutcome, Outcome.NotStarted,
            "The update didn't install because Icarus Starlink didn't close in time. Nothing was changed; click Update now to try again.", log);
        return new Result(Outcome.NotStarted, relaunch(), null);
    }

    private static void RecordSafely(Action<Outcome, string>? recordOutcome, Outcome outcome, string message, Action<string> log)
    {
        try
        {
            recordOutcome?.Invoke(outcome, message);
        }
        catch (Exception ex)
        {
            // Telling the app is a courtesy; it must never stop the relaunch.
            log($"Couldn't record the update outcome ({ex.Message}).");
        }
    }
}
