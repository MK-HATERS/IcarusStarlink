namespace IcarusStarlink.Catalog.AppUpdate;

/// <param name="Version">The release tag with any leading "v" stripped, e.g. "0.10.0".</param>
/// <param name="ReleaseNotes">The release's own Markdown body — shown as "what's new" before a user confirms updating.</param>
/// <param name="AssetBrowserDownloadUrl">The release zip's plain public download URL on GitHub — the repo is public, so this always resolves with no auth needed. Empty for a release only Nexus has.</param>
/// <param name="AssetDigest">The zip's "sha256:&lt;hex&gt;" digest from release.json or GitHub asset metadata. Downloads fail closed if it is missing, malformed, unsupported or mismatching, for both GitHub and Nexus.</param>
/// <param name="NexusFileId">The same release on Starlink's Nexus page (its file there): stable updates come from Nexus when this is set (NexusAppUpdates).</param>
public sealed record AppUpdateRelease(string Version, string ReleaseNotes, string AssetBrowserDownloadUrl, string? AssetDigest = null, int? NexusFileId = null)
{
    public bool IsOnGitHub => AssetBrowserDownloadUrl.Length > 0;
}

/// <summary>How far a download is: bytes so far, and the total when the server said.</summary>
public readonly record struct AppUpdateDownloadProgress(long Received, long? Total);
