using System.Globalization;
using System.Net;
using System.Text.Json;

namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// Every api.github.com call goes through here. GitHub allows 60 unauthenticated calls an hour per
/// internet connection, shared by every app on it, and Starlink made several on every launch. So:
/// an answer younger than <c>freshFor</c> is reused without asking; an older one is asked for with
/// its ETag, and GitHub's "304 not changed" reply doesn't count against the limit; and a hit
/// limit is remembered with its reset time (<see cref="RateLimitResetsAt"/>) so the app can say
/// exactly when it clears instead of "GitHub may be unreachable". Answers are kept on disk
/// (<see cref="Configure"/>), so a restart doesn't start from nothing.
/// </summary>
public static class GitHubApiCache
{
    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static string? _file;

    private sealed record Entry(string? ETag, string Body, DateTimeOffset FetchedAt);

    /// <summary>When the hourly limit was last hit and when GitHub said it clears; null while it isn't hit.</summary>
    public static DateTimeOffset? RateLimitResetsAt { get; private set; }

    /// <summary>"GitHub's hourly limit … resets at 12:37", or null when the limit isn't the problem.</summary>
    public static string? RateLimitMessage =>
        RateLimitResetsAt is { } at && at > DateTimeOffset.Now
            ? $"GitHub's hourly limit for your internet connection is used up (60 checks an hour for apps that don't sign in). It resets at {at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}."
            : null;

    /// <summary>Where answers are kept between runs (the app's Cache folder). Without it, they last the session.</summary>
    public static void Configure(string cacheFile)
    {
        lock (Gate)
        {
            _file = cacheFile;
            try
            {
                if (File.Exists(cacheFile))
                {
                    _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(cacheFile), Json)
                        is { } loaded ? new Dictionary<string, Entry>(loaded, StringComparer.Ordinal) : _entries;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A damaged cache only means asking GitHub again.
            }
        }
    }

    /// <summary>
    /// The body of a GitHub API GET: a recent saved answer, a "not changed" one, or a fresh one.
    /// Null when it can't be had (offline, limit hit with nothing saved, not found).
    /// </summary>
    public static async Task<string?> GetAsync(HttpClient httpClient, string url, TimeSpan freshFor, CancellationToken cancellationToken = default)
    {
        Entry? saved;
        lock (Gate)
        {
            _entries.TryGetValue(url, out saved);
        }

        if (saved is not null && DateTimeOffset.UtcNow - saved.FetchedAt < freshFor)
        {
            return saved.Body;
        }

        // While the limit is hit, asking again only fails: the saved answer, however old, beats none.
        if (RateLimitResetsAt is { } resets && resets > DateTimeOffset.Now)
        {
            return saved?.Body;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        GitHubUserAgent.EnsureOn(request);
        if (saved?.ETag is { } etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return saved?.Body;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified && saved is not null)
            {
                Store(url, saved with { FetchedAt = DateTimeOffset.UtcNow });
                return saved.Body;
            }

            if (IsRateLimited(response))
            {
                RateLimitResetsAt = ResetTime(response);
                return saved?.Body;
            }

            if (!response.IsSuccessStatusCode)
            {
                return saved?.Body;
            }

            RateLimitResetsAt = null;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Store(url, new Entry(response.Headers.ETag?.ToString(), body, DateTimeOffset.UtcNow));
            return body;
        }
    }

    /// <summary>Forgets saved answers and the limit (tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            _file = null;
            RateLimitResetsAt = null;
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
        && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0";

    private static DateTimeOffset ResetTime(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ratelimit-reset", out var values) && long.TryParse(values.FirstOrDefault(), out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.Now.AddHours(1);

    private static void Store(string url, Entry entry)
    {
        lock (Gate)
        {
            _entries[url] = entry;
            if (_file is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var temp = _file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
                File.Move(temp, _file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Saved for this session only.
            }
        }
    }
}
