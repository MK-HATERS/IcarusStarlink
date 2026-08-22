namespace IcarusStarlink.Catalog.Nexus;

public interface INexusApiClient
{
    /// <summary>
    /// Null specifically means "Nexus rejected this key" (HTTP 401) — the caller can show a clear
    /// "that key isn't valid" message. A genuine network/service failure throws instead, so the
    /// caller can tell those two cases apart ("wrong key" vs. "couldn't reach Nexus right now").
    /// </summary>
    Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>Mods updated within the given period ("1d", "1w", or "1m" — Nexus caches this server-side and only supports those three) for the given game domain (e.g. "icarus").</summary>
    Task<IReadOnlyList<NexusUpdateEntry>> GetUpdatedModsAsync(string apiKey, string gameDomain, string period, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an nxm:// link's modId/fileId into real, one-time CDN download URLs. key/expires
    /// (from the nxm:// link itself) are required for a non-premium account and must be omitted
    /// entirely for a premium one — Nexus's own endpoint only accepts the query string when both
    /// are present, per the official client's own request-building logic.
    /// </summary>
    Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(
        string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default);
}
