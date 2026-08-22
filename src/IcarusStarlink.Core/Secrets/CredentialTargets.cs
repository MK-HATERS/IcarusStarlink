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

    /// <summary>Needed only while the IcarusStarlink GitHub repo stays private — App Updates' own GetLatestReleaseAsync/DownloadAssetAsync calls.</summary>
    public const string GitHubToken = "IcarusStarlink:GitHubToken";

    /// <summary>Keyed by the site's own stable Id (not its display name) so renaming a saved FTP site doesn't orphan its saved password.</summary>
    public static string FtpSite(Guid siteId) => $"IcarusStarlink:FtpSite:{siteId:N}";
}
