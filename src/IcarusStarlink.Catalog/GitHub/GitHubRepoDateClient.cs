using System.Net.Http.Json;

namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// GitHub's own repo-info endpoint (`GET /repos/{owner}/{repo}`) returns a `pushed_at` timestamp —
/// unauthenticated, no token needed, but capped at 60 requests/hour per IP. Used as a best-effort
/// "Last Updated" signal for catalog entries whose download file is hosted on GitHub (nearly all
/// of them). This is repo-level, not per-file: an author who hosts many mods in one repo (the
/// common case — AgentKush, Jimk72, zailfyer etc. each host a dozen-plus mods in a single repo)
/// shows the same date for every mod from that repo, whichever file in it was pushed most
/// recently — not necessarily that specific mod's own file. Accepted tradeoff: a per-file-accurate
/// date needs the commits API (?path=...), which costs one request PER FILE rather than per repo,
/// and the catalog has far more distinct files (600+) than distinct repos (~40 as of Phase 4).
/// </summary>
public sealed class GitHubRepoDateClient : IGitHubRepoDateClient
{
    // Unauthenticated GitHub API is capped at 60 requests/hour per IP overall, but also has a
    // separate, stricter *secondary* (abuse-detection) limit that reacts to burstiness rather than
    // the hourly total — capping concurrency keeps a full-catalog refresh (~40 repos today) well
    // clear of that, without meaningfully slowing the fetch down.
    private const int MaxConcurrentRequests = 5;

    private readonly HttpClient _httpClient;

    public GitHubRepoDateClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        GitHubUserAgent.EnsureOn(_httpClient);
    }

    public async Task<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>> FetchPushedDatesAsync(
        IReadOnlyCollection<(string Owner, string Repo)> repos, CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentRequests);

        var lookups = await Task.WhenAll(repos.Select(repo => FetchOneAsync(repo, gate, cancellationToken)));

        var result = new Dictionary<(string, string), DateTimeOffset>();
        foreach (var (repo, pushedAt) in lookups)
        {
            if (pushedAt is { } value)
            {
                result[repo] = value;
            }
        }

        return result;
    }

    private async Task<((string Owner, string Repo) Repo, DateTimeOffset? PushedAt)> FetchOneAsync(
        (string Owner, string Repo) repo, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var dto = await _httpClient.GetFromJsonAsync<GitHubRepoDto>(
                $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}", cancellationToken);
            return (repo, dto?.PushedAt);
        }
        catch (Exception)
        {
            // One repo being renamed/deleted/private, or a stray rate-limit hit mid-batch,
            // shouldn't blank out every other repo's date — that mod's row (and any repo-mates)
            // just shows no Last Updated value instead of failing the whole lookup.
            return (repo, null);
        }
        finally
        {
            gate.Release();
        }
    }
}
