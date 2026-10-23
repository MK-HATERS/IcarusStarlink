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
    public const string NexusSource = "Nexus";
    public const string DatabaseSource = "Database";

    /// <summary>Nexus mod ID for a Nexus download; 0 for an IMM Database (catalog) download, which has no Nexus identity.</summary>
    public required int ModId { get; set; }

    /// <summary>Nexus file ID for a Nexus download; for a catalog download, a stable hash of CatalogEntryId (see CatalogFileId) so the (ModId, FileId) key the store uses stays unique.</summary>
    public required int FileId { get; set; }
    public required string FileName { get; set; }

    /// <summary>"Nexus" or "Database" — where this file came from. Null in records written before catalog downloads landed here, which were always Nexus.</summary>
    public string? Source { get; set; }

    /// <summary>The IMM Database catalog entry this was downloaded from; null for a Nexus download.</summary>
    public string? CatalogEntryId { get; set; }

    /// <summary>The mod's name as the source listed it (a catalog entry's Name) — what the Downloads page shows; null means derive it from FileName.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The author as the source listed it (a catalog entry's Author), so a pak installed from this file isn't "Unknown"; null for Nexus downloads and older records.</summary>
    public string? Author { get; set; }

    public bool IsCatalog => string.Equals(Source, DatabaseSource, StringComparison.OrdinalIgnoreCase);

    /// <summary>A deterministic positive int from a catalog entry ID (FNV-1a), used as FileId so catalog downloads key into the same store as Nexus ones.</summary>
    public static int CatalogFileId(string catalogEntryId)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in catalogEntryId)
            {
                hash = (hash ^ ch) * 16777619u;
            }

            return (int)(hash & 0x7FFFFFFF);
        }
    }

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
