namespace IcarusStarlink.Catalog.GitHub;

public interface IGitHubRepoDateClient
{
    Task<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>> FetchPushedDatesAsync(
        IReadOnlyCollection<(string Owner, string Repo)> repos, CancellationToken cancellationToken = default);
}
