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
}
