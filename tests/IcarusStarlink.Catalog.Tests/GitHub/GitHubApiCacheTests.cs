using System.Net;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.Tests.GitHub;

/// <summary>Every test that reaches GitHubApiCache (static, shared): one at a time, each starting empty.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitHubApiCacheCollection
{
    public const string Name = "GitHub API cache";
}

/// <summary>GitHub's 60-an-hour limit: saved answers, "not changed" replies, and saying when it resets.</summary>
[Collection(GitHubApiCacheCollection.Name)]
public sealed class GitHubApiCacheTests : IDisposable
{
    private const string Url = "https://api.github.com/repos/o/r";
    private readonly string _file = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ghcache_" + Guid.NewGuid().ToString("N") + ".json");

    public GitHubApiCacheTests() => GitHubApiCache.Reset();

    public void Dispose()
    {
        GitHubApiCache.Reset();
        if (File.Exists(_file))
        {
            File.Delete(_file);
        }
    }

    /// <summary>Answers in order; records the If-None-Match each request carried.</summary>
    private sealed class Sequence(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<string?> SentETags { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentETags.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.ToString());
            return Task.FromResult(answers[Math.Min(_next++, answers.Length - 1)]());
        }
    }

    private static HttpResponseMessage Ok(string body, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private static HttpResponseMessage Limited()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", DateTimeOffset.Now.AddMinutes(20).ToUnixTimeSeconds().ToString());
        return response;
    }

    [Fact]
    public async Task AFreshAnswer_IsReused_WithoutAskingGitHubAgain()
    {
        var handler = new Sequence(() => Ok("one", "\"a\""));
        var http = new HttpClient(handler);

        Assert.Equal("one", await GitHubApiCache.GetAsync(http, Url, TimeSpan.FromHours(1)));
        Assert.Equal("one", await GitHubApiCache.GetAsync(http, Url, TimeSpan.FromHours(1)));
        Assert.Single(handler.SentETags);
    }

    [Fact]
    public async Task AnOlderAnswer_IsAskedForWithItsETag_AndNotChangedKeepsIt()
    {
        var handler = new Sequence(() => Ok("one", "\"a\""), () => new HttpResponseMessage(HttpStatusCode.NotModified));
        var http = new HttpClient(handler);

        await GitHubApiCache.GetAsync(http, Url, TimeSpan.Zero);
        Assert.Equal("one", await GitHubApiCache.GetAsync(http, Url, TimeSpan.Zero));
        Assert.Equal([null, "\"a\""], handler.SentETags);
    }

    [Fact]
    public async Task TheLimitHit_KeepsTheSavedAnswer_StopsAsking_AndSaysWhenItResets()
    {
        var handler = new Sequence(() => Ok("one", "\"a\""), Limited);
        var http = new HttpClient(handler);
        await GitHubApiCache.GetAsync(http, Url, TimeSpan.Zero);

        Assert.Equal("one", await GitHubApiCache.GetAsync(http, Url, TimeSpan.Zero));
        Assert.Contains("hourly limit", GitHubApiCache.RateLimitMessage);
        Assert.Contains("It resets at", GitHubApiCache.RateLimitMessage);

        await GitHubApiCache.GetAsync(http, Url, TimeSpan.Zero);
        Assert.Equal(2, handler.SentETags.Count);
    }

    [Fact]
    public async Task SavedAnswers_LastBetweenRuns()
    {
        GitHubApiCache.Configure(_file);
        await GitHubApiCache.GetAsync(new HttpClient(new Sequence(() => Ok("one", "\"a\""))), Url, TimeSpan.FromHours(1));

        GitHubApiCache.Reset();
        GitHubApiCache.Configure(_file);
        var handler = new Sequence(() => Ok("two", "\"b\""));

        Assert.Equal("one", await GitHubApiCache.GetAsync(new HttpClient(handler), Url, TimeSpan.FromHours(1)));
        Assert.Empty(handler.SentETags);
    }

    [Fact]
    public async Task UpdateCheck_UsesReleaseJson_WithoutTheApi()
    {
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://github.com/MK-HATERS/IcarusStarlink/releases/latest/download/release.json"] =
                """{"version":"v2.5.0","notes":"New things","asset":"IcarusStarlink-v2.5.0-win-x64.zip","sha256":"ab12"}""",
        });

        var release = await new AppUpdateClient(new HttpClient(handler)).GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal("2.5.0", release.Version);
        Assert.Equal("New things", release.ReleaseNotes);
        Assert.Equal("https://github.com/MK-HATERS/IcarusStarlink/releases/download/v2.5.0/IcarusStarlink-v2.5.0-win-x64.zip", release.AssetBrowserDownloadUrl);
        Assert.Equal("sha256:ab12", release.AssetDigest);
    }
}
