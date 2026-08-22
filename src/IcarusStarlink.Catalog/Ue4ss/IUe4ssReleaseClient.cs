namespace IcarusStarlink.Catalog.Ue4ss;

public interface IUe4ssReleaseClient
{
    /// <summary>Null if the release can't be reached (offline, rate-limited, GitHub API shape changed, etc.) or its expected "UE4SS_v*.zip" asset is missing — never throws for a network-shaped failure.</summary>
    Task<Ue4ssReleaseInfo?> GetLatestStableReleaseAsync(CancellationToken cancellationToken = default);
}
