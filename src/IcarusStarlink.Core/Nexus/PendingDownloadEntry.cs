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

    /// <summary>Everything this activation produced (see PendingDownloadActivatedItem) — ActivatedFolderName/Kind above stay as the "primary" item for older records and simple consumers.</summary>
    public List<PendingDownloadActivatedItem> ActivatedItems { get; set; } = [];

    /// <summary>Requirement lines parsed from the download's own README at activation (see ModReadmeRequirements); empty when it has none.</summary>
    public List<string> Requirements { get; set; } = [];

    /// <summary>The activated items, falling back to the single legacy pair for records written before ActivatedItems existed.</summary>
    public IReadOnlyList<PendingDownloadActivatedItem> AllActivatedItems =>
        ActivatedItems.Count > 0
            ? ActivatedItems
            : ActivatedFolderName is not null && ActivatedKind is { } kind ? [new PendingDownloadActivatedItem(ActivatedFolderName, kind)] : [];
}
