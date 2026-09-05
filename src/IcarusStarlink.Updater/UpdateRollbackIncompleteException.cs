namespace IcarusStarlink.Updater;

/// <summary>
/// Thrown by UpdateApplier.Apply when its own rollback couldn't fully restore every file it had
/// already overwritten before the update itself failed — the install directory may now be left in a
/// genuine mixed old/new state (some files from the new build, some still the old ones), not the
/// clean "old install fully restored" outcome a normal failed update produces. BackupDirectory names
/// where whatever the rollback DID manage to preserve still lives, for manual recovery. Distinguished
/// from a plain Exception specifically so Program.cs can escalate to a user-visible notification only
/// for this genuinely worse outcome — the updater runs with CreateNoWindow: true (see
/// SettingsViewModel's own launch of it), so with no distinct handling here, this exact failure mode
/// used to leave the user with a broken install and literally nothing but an easy-to-miss
/// updater.log entry explaining what happened.
/// </summary>
public sealed class UpdateRollbackIncompleteException(string message, string backupDirectory, Exception innerException)
    : Exception(message, innerException)
{
    public string BackupDirectory { get; } = backupDirectory;
}
