namespace IcarusStarlink.Core.Nexus;

/// <summary>Which repository ActivatedFolderName's folder lives in — Library and UE4SS staging are separate folder namespaces, so this disambiguates which one to check/delete against.</summary>
public enum PendingDownloadActivationKind
{
    Library,
    Ue4ssMod,
}

/// <summary>
/// One file downloaded via a real nxm:// "Mod Manager Download" (or the manual paste-a-link
/// fallback) — MO2-style, this entry is never removed by a successful Activate (only Discard
/// removes it); Activate is instead a re-runnable action whose label/behavior depends on whether
/// ActivatedFolderName's own folder is still present.
/// </summary>
public sealed class PendingDownloadEntry
{
    public required int ModId { get; set; }
    public required int FileId { get; set; }
    public required string FileName { get; set; }

    /// <summary>Absolute path under this app's own Pending_Downloads folder.</summary>
    public required string LocalFilePath { get; set; }

    public DateTimeOffset DownloadedAtUtc { get; set; }

    /// <summary>Set by a successful Activate — the folder name it produced. Null until first activated.</summary>
    public string? ActivatedFolderName { get; set; }

    /// <summary>Set together with ActivatedFolderName; null exactly when ActivatedFolderName is null.</summary>
    public PendingDownloadActivationKind? ActivatedKind { get; set; }
}
