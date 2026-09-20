namespace IcarusStarlink.Updater;

/// <summary>
/// Orchestrates one Apply attempt and its aftermath — extracted out of Program.cs's own top-level
/// statements so this decision logic (what happens after Apply succeeds, fails-but-rolls-back, or
/// fails worse) is unit-testable the same way UpdateApplier.Apply itself already is, rather than
/// only reachable by actually running the compiled exe as a subprocess.
/// </summary>
public static class UpdateRunner
{
    public enum Outcome { Applied, RolledBack, RollbackIncomplete }

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
    public static Result Run(string installDirectory, string newFilesDirectory, Action<string> log, Func<bool> relaunch)
    {
        try
        {
            UpdateApplier.Apply(installDirectory, newFilesDirectory, log);
        }
        catch (UpdateRollbackIncompleteException ex)
        {
            log($"Update FAILED: {ex.Message}");
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
            return new Result(Outcome.RolledBack, relaunch(), null);
        }

        log("Update applied — relaunching.");
        return new Result(Outcome.Applied, relaunch(), null);
    }
}
