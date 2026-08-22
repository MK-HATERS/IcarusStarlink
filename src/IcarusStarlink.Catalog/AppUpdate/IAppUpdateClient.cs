namespace IcarusStarlink.Catalog.AppUpdate;

public interface IAppUpdateClient
{
    /// <summary>Null if the release can't be reached (offline, rate-limited, GitHub API shape changed, no zip asset attached, or — while the repo stays private — no/invalid gitHubToken) — never throws for a network-shaped failure.</summary>
    Task<AppUpdateRelease?> GetLatestReleaseAsync(string? gitHubToken, CancellationToken cancellationToken = default);

    /// <summary>Downloads release's own zip asset to destinationPath. Throws on failure — unlike GetLatestReleaseAsync, a caller that got this far already knows a real update exists and needs to know if fetching it failed.</summary>
    Task DownloadAssetAsync(AppUpdateRelease release, string? gitHubToken, string destinationPath, CancellationToken cancellationToken = default);
}
