namespace IcarusStarlink.Catalog.AppUpdate;

/// <param name="Version">The release tag with any leading "v" stripped, e.g. "0.10.0".</param>
/// <param name="ReleaseNotes">The release's own Markdown body — shown as "what's new" before a user confirms updating.</param>
/// <param name="AssetId">GitHub's numeric asset id — needed for DownloadAssetAsync's own authenticated endpoint, which is the only download path that works while the repo stays private.</param>
/// <param name="AssetBrowserDownloadUrl">Only usable once the repo is public — a private repo's browser_download_url 404s without a browser's own logged-in session, unlike the asset-id endpoint.</param>
public sealed record AppUpdateRelease(string Version, string ReleaseNotes, long AssetId, string AssetBrowserDownloadUrl);
