namespace IcarusStarlink.Core.Catalog;

/// <summary>
/// One mod the user is manually tracking from Nexus — there's no in-app Nexus API for v1 (the
/// spec itself defers "Premium in-app download" to a later update), so this is purely local
/// bookkeeping: a URL the user pasted in, a NexusId parsed out of it, and a name the user
/// supplies (Nexus's mod name isn't fetchable without the API or scraping the page, so it isn't
/// guessed at). Whether it's already downloaded is computed against the Library at display time,
/// not stored here.
/// </summary>
public sealed class NexusWatchlistEntry
{
    public required int NexusId { get; set; }
    public required string Url { get; set; }
    public required string Name { get; set; }
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
}
