namespace IcarusStarlink.Catalog.Nexus;

/// <summary>One entry from GET /v1/user/endorsements — the account's own whole endorsement history, across every game, not just Icarus (callers filter by DomainName).</summary>
public sealed record NexusEndorsement(int ModId, string DomainName, NexusEndorsementStatus Status);
