namespace IcarusStarlink.Core.Server;

/// <summary>
/// A restore that didn't finish. <see cref="SavedChanged"/> says whether any file in Saved was
/// already replaced; <see cref="SafetyBackupPath"/> is the before-restore zip that puts it all back.
/// The cause is the inner exception.
/// </summary>
public sealed class ServerRestoreException(string message, string safetyBackupPath, bool savedChanged, Exception inner)
    : Exception(message, inner)
{
    /// <summary>The backup of the state before the restore.</summary>
    public string SafetyBackupPath { get; } = safetyBackupPath;

    /// <summary>Some files in Saved were already replaced when it stopped.</summary>
    public bool SavedChanged { get; } = savedChanged;
}
