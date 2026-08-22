namespace IcarusStarlink.Catalog.Nexus;

/// <summary>One mod as Nexus's v1 API describes it — returned both by the single-mod lookup (GetModInfoAsync) and by the browse lists (GetModListAsync), which share the same underlying JSON shape. PictureUrl is the mod page's own header image CDN URL, null when the author never set one.</summary>
public sealed record NexusModInfo(int ModId, string? Name, string Author, string? Summary, string Version, string? PictureUrl);
