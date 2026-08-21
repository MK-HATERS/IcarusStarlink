namespace IcarusStarlink.Core.Secrets;

/// <summary>
/// A generic per-user secret store — Phase 8's Nexus API key is the first real use, but the
/// plan's own architecture doc earmarked this (Windows Credential Manager) for "Agent token and
/// update tokens" back when Server was first cut from v1, before FTP credentials (Phase 8.5) were
/// planned to need the exact same thing.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Overwrites any existing secret already stored under this target.</summary>
    void Save(string target, string secret);

    /// <summary>Null if nothing is stored under this target — never throws for "not found".</summary>
    string? Read(string target);

    /// <summary>No-op if nothing is stored under this target.</summary>
    void Delete(string target);
}
