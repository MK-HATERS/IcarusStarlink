using System.Net.Http.Headers;

namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// GitHub rejects any API request with no User-Agent at all (403), so every client of it here has
/// to set one. "IcarusStarlink" is enough to identify the caller in GitHub's own logs; the
/// endpoints this app uses don't require the product URL GitHub's guidance suggests for registered
/// apps. Previously hand-copied into all three GitHub-facing clients.
/// </summary>
public static class GitHubUserAgent
{
    public const string Value = "IcarusStarlink";

    /// <summary>Sets the header on a shared HttpClient, but only when nothing has set one yet — these clients can be handed the same injected HttpClient instance.</summary>
    public static void EnsureOn(HttpClient httpClient)
    {
        if (httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(Value);
        }
    }

    /// <summary>Per-request variant, for a client that also varies auth headers per call rather than setting them once on the HttpClient.</summary>
    public static void EnsureOn(HttpRequestMessage request)
    {
        if (!request.Headers.UserAgent.Any())
        {
            request.Headers.UserAgent.ParseAdd(Value);
        }
    }
}
