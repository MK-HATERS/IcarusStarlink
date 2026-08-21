namespace IcarusStarlink.Catalog.Nexus;

/// <summary>
/// One entry from Nexus's own /v1/games/{gameDomain}/mods/updated endpoint — confirmed against
/// Nexus's own official node-nexus-api client source (IUpdateEntry) during Phase 8 planning.
/// LatestFileUpdateUnix/LatestModActivityUnix are raw Unix seconds, as Nexus itself returns them.
/// </summary>
public sealed record NexusUpdateEntry(int ModId, long LatestFileUpdateUnix, long LatestModActivityUnix);
