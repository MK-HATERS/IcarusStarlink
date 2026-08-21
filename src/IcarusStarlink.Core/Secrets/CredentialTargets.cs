namespace IcarusStarlink.Core.Secrets;

/// <summary>
/// Well-known Windows Credential Manager target names, centralized so a typo in one ViewModel
/// can't silently make another ViewModel's saved secret unreadable — e.g. Settings saves the
/// Nexus API key, DownloadsViewModel reads it back to check for updates; both need the exact
/// same string.
/// </summary>
public static class CredentialTargets
{
    public const string NexusApiKey = "IcarusStarlink:NexusApiKey";
}
