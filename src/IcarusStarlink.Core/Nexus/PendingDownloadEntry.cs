namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// One file downloaded via a real nxm:// "Mod Manager Download" (or the manual paste-a-link
/// fallback) that hasn't been imported into the Library yet — the "Pending Downloads" staging
/// list the user asked for, kept deliberately separate from the existing Nexus Mods watchlist
/// (a different, manually-curated list of mods being tracked, not necessarily downloaded).
/// </summary>
public sealed class PendingDownloadEntry
{
    public required int ModId { get; set; }
    public required int FileId { get; set; }
    public required string FileName { get; set; }

    /// <summary>Absolute path under this app's own Pending_Downloads folder.</summary>
    public required string LocalFilePath { get; set; }

    public DateTimeOffset DownloadedAtUtc { get; set; }
}
