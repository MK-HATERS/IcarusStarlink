namespace IcarusStarlink.Core.Secrets;

/// <summary>
/// A generic per-user secret store (Windows Credential Manager), used for the Nexus API key and
/// FTP credentials.
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
