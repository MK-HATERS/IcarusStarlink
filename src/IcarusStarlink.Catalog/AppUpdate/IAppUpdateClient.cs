namespace IcarusStarlink.Catalog.AppUpdate;

public interface IAppUpdateClient
{
    /// <summary>Null if the release can't be reached (offline, rate-limited, GitHub API shape changed, no zip asset attached) — never throws for a network-shaped failure.</summary>
    Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>The local test release folder in use instead of GitHub and Nexus (AppUpdateClient.TestFeedFolder); null normally.</summary>
    string? UpdateTestFeed => null;

    /// <summary>
    /// The newest beta (a GitHub prerelease, tagged like v2.10.0-beta.1) for the Beta update channel;
    /// null when there is none or it can't be reached. Stable users never see these: GitHub's
    /// "latest release" skips prereleases. The default serves clients without betas.
    /// </summary>
    Task<AppUpdateRelease?> GetLatestBetaReleaseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AppUpdateRelease?>(null);

    /// <summary>
    /// One version's GitHub release (its release.json: notes, zip and SHA-256), for a release found
    /// on Nexus: the same zip is on both, so the download from Nexus is checked against GitHub's hash.
    /// Null when GitHub has no such release or can't be reached.
    /// </summary>
    Task<AppUpdateRelease?> GetReleaseAsync(string version, CancellationToken cancellationToken = default) =>
        Task.FromResult<AppUpdateRelease?>(null);

    /// <summary>
    /// Downloads release's own zip asset to destinationPath. Throws on failure — unlike
    /// GetLatestReleaseAsync, a caller that got this far already knows a real update exists and
    /// needs to know if fetching it failed. Also throws (deleting the downloaded file first) if
    /// release.AssetDigest is missing, malformed, unsupported or does not match the file's SHA-256.
    /// A caller must NOT proceed to Apply on that exception.
    /// </summary>
    Task DownloadAssetAsync(AppUpdateRelease release, string destinationPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same, from the first of <paramref name="urls"/> that answers (GitHub's link, or Nexus's
    /// CDN servers), reporting progress as it goes, then checked against <paramref name="digest"/>.
    /// The default serves clients that only know GitHub's link.
    /// </summary>
    Task DownloadAsync(IReadOnlyList<string> urls, string? digest, string destinationPath, IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken = default) =>
        DownloadAssetAsync(new AppUpdateRelease("", "", urls[0], digest), destinationPath, cancellationToken);
}
