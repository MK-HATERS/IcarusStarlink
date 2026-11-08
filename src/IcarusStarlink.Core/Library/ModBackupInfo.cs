namespace IcarusStarlink.Core.Library;

/// <summary>
/// One kept copy of a Library mod (a folder under Mod_Backups), for the mod's version history.
/// Reason, Version and NexusFileId come from the label written beside the backup; they are null
/// when the backup has no label file.
/// </summary>
public sealed record ModBackupInfo(
    string Path,
    DateTime? TakenAtUtc,
    string? Reason,
    string? Version,
    int? NexusFileId);

/// <summary>Why a backup was made, as the version history shows it.</summary>
public static class ModBackupReasons
{
    public const string MadeByYou = "Made by you";

    public const string BeforeReplace = "Before an update or reinstall";

    public const string BeforeRestore = "Before putting back an older version";

    public const string BeforeFix = "Before a fix";
}

/// <summary>The label file kept beside a mod backup ("&lt;backup folder&gt;.label.json").</summary>
public sealed class ModBackupLabel
{
    public string? Reason { get; set; }

    public string? Version { get; set; }

    public int? NexusFileId { get; set; }

    public string? NexusFileVersion { get; set; }
}
