using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One card in the native Nexus catalog — the API's own mod info plus this machine's own local
/// status. Nexus's v1 API exposes no download-history/notification feed to sync the website's own
/// "you have this" badges from, so these are computed locally instead (which also makes them
/// honest about THIS install rather than any device the account ever touched): LocalBadge says the
/// strongest local relationship (In Library &gt; Downloaded &gt; Tracked), HasUpdate compares the
/// card's live version against the Library copy's. IsTracked is watchlist MEMBERSHIP specifically
/// (not the same as LocalBadge=="Tracked" — a mod can be both tracked AND already in the Library,
/// where LocalBadge shows the stronger "In Library" but the Track toggle still needs to know it's
/// tracked), so the Tracked filter and the card's Track/Untrack button both key off this instead.
/// </summary>
/// <summary>
/// DisplayName is the tracked watchlist entry's own stored Name when IsTracked (which
/// RenameTrackedEntry can override), falling back to the live Mod.Name otherwise — distinct from
/// Mod.Name so a rename is actually visible: without this, the card would always show the live
/// Nexus name regardless of any stored override.
/// </summary>
public sealed record NexusCatalogRow(NexusModInfo Mod, string? LocalBadge, bool HasUpdate, bool IsTracked, string DisplayName, string? NewTag = null)
{
    /// <summary>What a screen reader says for the card (the record's own text, the whole mod summary, otherwise).</summary>
    public override string ToString() => DisplayName;
}
