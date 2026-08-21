namespace IcarusStarlink.Catalog.Nexus;

/// <summary>What Nexus's own /v1/users/validate endpoint returns for a valid API key — confirmed against Nexus's own official node-nexus-api client source (IValidateKeyResponse) during Phase 8 planning, not guessed.</summary>
public sealed record NexusUserInfo(int UserId, string Name, bool IsPremium, bool IsSupporter, string Email, string ProfileUrl);
