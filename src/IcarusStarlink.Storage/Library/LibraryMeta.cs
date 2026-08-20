namespace IcarusStarlink.Storage.Library;

/// <summary>The sidecar (.immmeta.json) content — library-only metadata not already present in the mod's own EXMOD file.</summary>
internal sealed class LibraryMeta
{
    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset ImportedAtUtc { get; set; }
}
