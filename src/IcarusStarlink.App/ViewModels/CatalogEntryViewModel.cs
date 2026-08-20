using IcarusStarlink.Catalog;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One row in the IMM Database table — a CatalogEntry plus whatever the Library already knows
/// about it. Cross-referenced by (Name, Author) since catalog entries don't carry a FolderName
/// (the same imperfect-but-good-enough matching approach DaedalusCatalogClient already uses for
/// its own tag cross-reference) — a mod imported under a different name than the catalog lists
/// won't be detected as already-downloaded.
///
/// lastUpdated is likewise a derived/cross-referenced value, not something CatalogEntry itself
/// carries — neither Daedalus's nor Jimk72's catalog JSON has any timestamp field at all, so this
/// comes from a separate GitHub repo-info lookup (IGitHubRepoDateClient) keyed off the entry's own
/// download URL, resolved by DownloadsViewModel and passed in the same way installedVersion is.
/// </summary>
public sealed class CatalogEntryViewModel(CatalogEntry entry, string? installedVersion, DateTimeOffset? lastUpdated)
{
    public CatalogEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Author => Entry.Author;
    public string Version => Entry.Version;
    public string InstalledVersion => installedVersion ?? "";
    public string CompatibilityDisplay => string.IsNullOrEmpty(Entry.CompatibilityRaw) ? "?" : Entry.CompatibilityRaw;
    public string CategoryDisplay => string.Join(", ", Entry.Categories);
    public string SourceDisplay => Entry.Source == CatalogSource.Daedalus ? "IMM DB" : "Jimk72";

    // ISO 8601 (yyyy-MM-dd) so the DataGrid's default text-column sort still sorts chronologically
    // within the real dates. Verified live: WPF's default column sort uses culture-aware string
    // comparison, not ordinal, and that collation treats "—" (em dash) as sorting before
    // alphanumerics — so mods with no resolvable date land at the top of an ascending sort (and
    // the bottom of a descending one), not the reverse a codepoint comparison would suggest.
    public string LastUpdatedDisplay => lastUpdated?.ToString("yyyy-MM-dd") ?? "—";

    public bool IsDownloaded => installedVersion is not null;
    public bool IsOutdated => installedVersion is not null && installedVersion != Entry.Version;
    public string Status => IsOutdated ? "Outdated" : IsDownloaded ? "Downloaded" : "Available";
    public bool HasDownloadableFile => Entry.ExmodzUrl is not null || Entry.PakUrl is not null;
}
