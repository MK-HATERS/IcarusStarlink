namespace IcarusStarlink.Catalog.Nexus;

public interface INexusApiClient
{
    /// <summary>
    /// Null specifically means "Nexus rejected this key" (HTTP 401 or 403) — the caller can show a clear
    /// "that key isn't valid" message. A genuine network/service failure throws instead, so the
    /// caller can tell those two cases apart ("wrong key" vs. "couldn't reach Nexus right now").
    /// </summary>
    Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an nxm:// link's modId/fileId into real, one-time CDN download URLs. key/expires
    /// (from the nxm:// link itself) are required for a non-premium account and must be omitted
    /// entirely for a premium one — Nexus's own endpoint only accepts the query string when both
    /// are present, per the official client's own request-building logic.
    /// </summary>
    Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(
        string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a single mod's real name/author/summary by ID — for enriching a Library entry that
    /// otherwise has no metadata of its own (an opaque .pak Activated from a real Nexus download).
    /// Null if Nexus rejected the key (same 401/403 convention as ValidateKeyAsync). A mod under
    /// moderation still comes back, but with a null Name (Nexus leaves its "name" field out).
    /// </summary>
    Task<NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One of Nexus's three v1 browse lists (trending / latest added / latest updated — each a
    /// small server-curated set, roughly 10 mods; v1 has no general search endpoint, that's the
    /// newer GraphQL API's territory). A rejected key (401/403) throws InvalidOperationException,
    /// since a caller reaching this already claims to have a key. Endpoint paths confirmed against
    /// Nexus's own official node-nexus-api client source, same as every other path in this client.
    /// </summary>
    Task<IReadOnlyList<NexusModInfo>> GetModListAsync(string apiKey, string gameDomain, NexusModList list, CancellationToken cancellationToken = default);

    /// <summary>
    /// A mod's downloadable files — the file ID is the missing half a direct download needs
    /// (GetDownloadLinksAsync takes modId + fileId; the browse lists only carry modId). Endpoint
    /// confirmed against the official client's own getModFiles. Same 401/403-throws convention as
    /// the other authenticated calls.
    /// </summary>
    Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same files plus Nexus's file_updates (which file replaced which) — what per-file update
    /// checks follow (see NexusFileLineage). The default serves clients that only know the file
    /// list: no update chain, so a file is only ever its own newest version.
    /// </summary>
    async Task<NexusModFileSet> GetModFileSetAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
        new(await GetModFilesAsync(apiKey, gameDomain, modId, cancellationToken), []);

    /// <summary>
    /// Name search over a game's whole mod catalog — the one thing v1 can't do at all, served by
    /// Nexus's newer v2 GraphQL endpoint (shape confirmed by live probing during planning).
    /// apiKey is genuinely optional here: the endpoint answers unauthenticated (confirmed live),
    /// so search works even before a key is configured — pass the key when there is one.
    /// </summary>
    Task<IReadOnlyList<NexusModInfo>> SearchModsAsync(string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same cards as SearchModsAsync, looked up by mod ID instead of name — what Library's
    /// Update / Find on Nexus use, since a Library mod's name often isn't its Nexus title. Same v2
    /// GraphQL endpoint (legacyModsByDomain, confirmed live 2026-10-23; works without a key). An ID
    /// Nexus doesn't know is simply missing from the result. The default serves clients without
    /// the lookup: nothing found.
    /// </summary>
    Task<IReadOnlyList<NexusModInfo>> GetModsByIdAsync(string? apiKey, string gameDomain, IReadOnlyList<int> modIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NexusModInfo>>([]);

    /// <summary>
    /// A page of the game's WHOLE mod catalog, most-endorsed first — the "browse everything" view
    /// the three curated v1 lists can't provide. Served by the same v2 GraphQL endpoint as search
    /// (offset/count paging and endorsement sort both confirmed by live probing), so it also
    /// answers unauthenticated — pass the key when there is one.
    /// </summary>
    Task<NexusModPage> ListAllModsAsync(string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// The game's newest mods, most recently published first (the Database page's "new since your
    /// last visit"). Same v2 GraphQL query as ListAllModsAsync with a createdAt-DESC sort (checked
    /// live 2026-10-28; a createdAt filter is NOT accepted by the endpoint, so the caller counts).
    /// Answers unauthenticated. The default (fakes) returns nothing.
    /// </summary>
    Task<NexusModPage> ListNewestModsAsync(string? apiKey, string gameDomain, int count, CancellationToken cancellationToken = default) =>
        Task.FromResult(new NexusModPage([], 0));

    /// <summary>
    /// A mod's own changelog history — version number to its list of change lines (e.g.
    /// {"1.1": ["PAK-file updated 26th of August 2026"]}), matching Nexus's own real
    /// per-version changelog display on a mod's page. Endpoint confirmed against the official
    /// client's own getChangelogs. Same 401/403-throws convention as the other authenticated calls.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The account's own WHOLE endorsement history — every mod endorsed or abstained, across every
    /// game Nexus hosts, in one call (confirmed against the official client's own getEndorsements;
    /// there's no per-game filter on the endpoint itself, so callers filter by DomainName). A mod
    /// this account has never touched simply isn't in the list at all — Undecided is the implicit
    /// default, not a value Nexus itself ever seems to return here.
    /// </summary>
    Task<IReadOnlyList<NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Endorses or abstains a mod on this account's behalf — a real write, confirmed against the
    /// official client's own endorseMod. modVersion has to name a version that genuinely exists for
    /// this mod on Nexus (its own documented constraint) — callers should fetch the mod's current
    /// real version (e.g. via GetModInfoAsync) rather than reuse a possibly-stale locally-recorded
    /// one. Returns the resulting status Nexus reports back.
    /// </summary>
    Task<NexusEndorsementStatus> SetEndorsementAsync(
        string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default);
}

/// <summary>One page of ListAllModsAsync — the slice plus the catalog's own total, so the UI can say "N of M shown" and know whether more remain.</summary>
public sealed record NexusModPage(IReadOnlyList<NexusModInfo> Mods, int TotalCount);

/// <summary>Which of Nexus's v1 browse lists GetModListAsync fetches. Names double as the UI's own pill labels, so they read as words, not endpoint paths.</summary>
public enum NexusModList
{
    Trending,
    Latest,
    Updated,
}
