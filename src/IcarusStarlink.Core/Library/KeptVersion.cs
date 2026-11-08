namespace IcarusStarlink.Core.Library;

/// <summary>
/// "Keep this version" on a mod: the player stays on the copy they have (for a server, or a save)
/// and the update it skipped stops being offered. Stored in the mod's sidecar (Library_Meta for a
/// data mod, Ue4ss_Meta for a UE4SS mod); null there means the switch is off.
/// </summary>
public sealed class KeptVersion
{
    public DateTimeOffset KeptAtUtc { get; set; }

    /// <summary>The newest Nexus file in the copy's update chain when the switch went on; null when no check had found one.</summary>
    public int? SkippedFileId { get; set; }

    /// <summary>The newest version known when the switch went on; null when no check had run.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// Whether the switch hides the update a check found. A file other than the skipped one, or a
    /// version newer than the skipped one, shows again. With nothing recorded to compare against
    /// (kept before any check), every update stays hidden until the switch is turned off.
    /// </summary>
    public static bool Hides(KeptVersion? kept, int? latestFileId, string? latestVersion)
    {
        if (kept is null)
        {
            return false;
        }

        if (kept.SkippedFileId is { } skippedFile && latestFileId is { } latestFile)
        {
            return latestFile == skippedFile;
        }

        if (!string.IsNullOrWhiteSpace(kept.SkippedVersion) && !string.IsNullOrWhiteSpace(latestVersion))
        {
            return !UpdateVersionComparer.IsNewer(latestVersion, kept.SkippedVersion);
        }

        return true;
    }
}
