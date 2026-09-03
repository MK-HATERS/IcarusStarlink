namespace IcarusStarlink.Core.Catalog;

/// <summary>
/// One mod the user is tracking from Nexus — Name/Author/Version are captured from the live Nexus
/// API result at the moment it's tracked (see NexusCatalogViewModel.TrackMod), then kept as this
/// store's own local copy: Name can later be overridden in place via UpdateName, independent of
/// whatever Nexus itself currently calls the mod, while Author/Version are a point-in-time record
/// rather than a live value. Whether it's already downloaded is computed against the Library at
/// display time, not stored here.
/// </summary>
public sealed class NexusWatchlistEntry
{
    public required int NexusId { get; set; }
    public required string Url { get; set; }
    public required string Name { get; set; }
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
}
