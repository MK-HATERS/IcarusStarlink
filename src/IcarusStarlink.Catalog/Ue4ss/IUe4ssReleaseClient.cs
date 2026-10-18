using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Catalog.Ue4ss;

public interface IUe4ssReleaseClient
{
    /// <summary>Null if the release can't be reached (offline, rate-limited, GitHub API shape changed, etc.) or its expected "UE4SS_v*.zip" asset is missing — never throws for a network-shaped failure.</summary>
    Task<Ue4ssReleaseInfo?> GetLatestStableReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>The newest dev build (GitHub's rolling "experimental-latest" release), with its build number and commit. Null when it can't be reached; never throws for a network-shaped failure.</summary>
    Task<Ue4ssReleaseInfo?> GetLatestDevBuildAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Ue4ssReleaseInfo?>(null);

    /// <summary>Whether <paramref name="candidateCommit"/> is newer or older than <paramref name="installedCommit"/> in UE4SS's history; Unknown when GitHub can't say.</summary>
    Task<Ue4ssBuildOrder> CompareBuildsAsync(string installedCommit, string candidateCommit, CancellationToken cancellationToken = default) =>
        Task.FromResult(Ue4ssBuildOrder.Unknown);
}
