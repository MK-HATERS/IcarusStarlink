using IcarusStarlink.Catalog;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One row in the IMM Database table — a CatalogEntry plus whatever the Library already knows
/// about it. Cross-referenced by (Name, Author) since catalog entries don't carry a FolderName
/// (the same imperfect-but-good-enough matching approach DaedalusCatalogClient already uses for
/// its own tag cross-reference) — a mod imported under a different name than the catalog lists
/// won't be detected as already-downloaded.
/// </summary>
public sealed class CatalogEntryViewModel(CatalogEntry entry, string? installedVersion)
{
    public CatalogEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Author => Entry.Author;
    public string Version => Entry.Version;
    public string InstalledVersion => installedVersion ?? "";
    public string CompatibilityDisplay => string.IsNullOrEmpty(Entry.CompatibilityRaw) ? "?" : Entry.CompatibilityRaw;
    public string CategoryDisplay => string.Join(", ", Entry.Categories);
    public string SourceDisplay => Entry.Source == CatalogSource.Daedalus ? "IMM DB" : "Jimk72";

    public bool IsDownloaded => installedVersion is not null;
    public bool IsOutdated => installedVersion is not null && installedVersion != Entry.Version;
    public string Status => IsOutdated ? "Outdated" : IsDownloaded ? "Downloaded" : "Available";
    public bool HasDownloadableFile => Entry.ExmodzUrl is not null || Entry.PakUrl is not null;
}
