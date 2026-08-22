namespace IcarusStarlink.Catalog.Nexus;

/// <summary>One CDN mirror for a file download — confirmed against Nexus's own official node-nexus-api client source (IDownloadURL) during Phase 8.3b planning.</summary>
public sealed record NexusDownloadLink(string Uri, string ServerName, string ServerShortName);
