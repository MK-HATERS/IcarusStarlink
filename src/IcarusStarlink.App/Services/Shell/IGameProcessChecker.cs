namespace IcarusStarlink.App.Services;

/// <summary>
/// Thin seam around "is Icarus itself currently running right now" — SavesViewModel's only guard
/// against writing over a live save file the game still has open and will overwrite on exit (see
/// SavesViewModel's own class-level doc comment). A direct Process.GetProcessesByName call has no
/// way to be faked from a test — going through this interface instead lets a test substitute a
/// canned answer, including a DIFFERENT answer on a second call within the same save/restore pass,
/// which is exactly what's needed to exercise the late, immediately-before-the-write re-check
/// SaveChanges/RestoreBackupAsync each run. GameProcessChecker is the real, production
/// implementation; production DI registers it as a singleton.
/// </summary>
public interface IGameProcessChecker
{
    bool IsRunning();
}
