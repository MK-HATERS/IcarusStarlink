namespace IcarusStarlink.Updater;

/// <summary>
/// Thrown by UpdateApplier.Apply when its rollback couldn't restore every file it had already
/// overwritten: the install may now be a mix of old and new files. BackupDirectory is where the
/// replaced files are kept, for manual recovery. A type of its own so Program.cs shows a message
/// box for this outcome only.
/// </summary>
public sealed class UpdateRollbackIncompleteException(string message, string backupDirectory, Exception innerException)
    : Exception(message, innerException)
{
    public string BackupDirectory { get; } = backupDirectory;
}
