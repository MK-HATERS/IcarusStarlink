namespace IcarusStarlink.Catalog.AppUpdate;

public interface IAppUpdateClient
{
    /// <summary>Null if the release can't be reached (offline, rate-limited, GitHub API shape changed, no zip asset attached) — never throws for a network-shaped failure.</summary>
    Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads release's own zip asset to destinationPath. Throws on failure — unlike
    /// GetLatestReleaseAsync, a caller that got this far already knows a real update exists and
    /// needs to know if fetching it failed. Also throws (deleting the downloaded file first) if
    /// release.AssetDigest was present and doesn't match the downloaded file's own SHA-256 — a
    /// caller must NOT proceed to Apply on that exception. A null/absent AssetDigest is not an
    /// error: verification is simply skipped and the download is returned as successful.
    /// </summary>
    Task DownloadAssetAsync(AppUpdateRelease release, string destinationPath, CancellationToken cancellationToken = default);
}
