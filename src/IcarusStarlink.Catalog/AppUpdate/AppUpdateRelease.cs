namespace IcarusStarlink.Catalog.AppUpdate;

/// <param name="Version">The release tag with any leading "v" stripped, e.g. "0.10.0".</param>
/// <param name="ReleaseNotes">The release's own Markdown body — shown as "what's new" before a user confirms updating.</param>
/// <param name="AssetBrowserDownloadUrl">The release zip's plain public download URL — the repo is public, so this always resolves with no auth needed.</param>
public sealed record AppUpdateRelease(string Version, string ReleaseNotes, string AssetBrowserDownloadUrl);
