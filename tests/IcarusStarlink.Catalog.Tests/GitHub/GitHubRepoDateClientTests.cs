using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.Tests.GitHub;

[Collection(GitHubApiCacheCollection.Name)]
public class GitHubRepoDateClientTests
{
    public GitHubRepoDateClientTests() => IcarusStarlink.Catalog.GitHub.GitHubApiCache.Reset();

    private static GitHubRepoDateClient CreateClient(Dictionary<string, string> responsesByUrl) =>
        new(new HttpClient(new FakeHttpMessageHandler(responsesByUrl)));

    [Fact]
    public async Task FetchPushedDatesAsync_ReturnsPushedAtForEachRepo()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.github.com/repos/AgentKush/Icarus-mods"] = """{ "pushed_at": "2026-07-01T12:00:00Z" }""",
        });

        var dates = await client.FetchPushedDatesAsync([("AgentKush", "Icarus-mods")]);

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), dates[("AgentKush", "Icarus-mods")]);
    }

    [Fact]
    public async Task FetchPushedDatesAsync_OneRepoFailing_StillReturnsTheOthers()
    {
        // "Ghost/Renamed-Repo" isn't in the fixture dictionary at all, so FakeHttpMessageHandler
        // returns 404 for it — the same shape a real renamed/deleted repo or a rate-limit hit
        // would produce. Mirrors the ~10% mismatch rate DaedalusCatalogClient already tolerates
        // for its own cross-reference: one bad entry shouldn't cost the whole batch.
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.github.com/repos/AgentKush/Icarus-mods"] = """{ "pushed_at": "2026-07-01T12:00:00Z" }""",
        });

        var dates = await client.FetchPushedDatesAsync([("AgentKush", "Icarus-mods"), ("Ghost", "Renamed-Repo")]);

        Assert.Single(dates);
        Assert.True(dates.ContainsKey(("AgentKush", "Icarus-mods")));
        Assert.False(dates.ContainsKey(("Ghost", "Renamed-Repo")));
    }

    /// <summary>Answers every repo at once (no awaiting, so lookups run one after another) and notes whether the cache file was there at each request.</summary>
    private sealed class FileWatchingHandler(string cacheFile) : HttpMessageHandler
    {
        public List<bool> FileExistedAtEachRequest { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            FileExistedAtEachRequest.Add(File.Exists(cacheFile));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "pushed_at": "2026-07-01T12:00:00Z" }"""),
            });
        }
    }

    /// <summary>Regression: every answer rewrote the whole cache file (about 400 KB on a real install), about 40 times per catalog refresh.</summary>
    [Fact]
    public async Task FetchPushedDatesAsync_WritesTheCacheFileOnceAtTheEnd()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ghdates_" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(folder, "github_cache.json");
        try
        {
            GitHubApiCache.Configure(file);
            var handler = new FileWatchingHandler(file);

            var dates = await new GitHubRepoDateClient(new HttpClient(handler)).FetchPushedDatesAsync([("o", "one"), ("o", "two"), ("o", "three")]);

            Assert.Equal([false, false, false], handler.FileExistedAtEachRequest);
            Assert.Equal(3, dates.Count);
            Assert.True(File.Exists(file));
        }
        finally
        {
            GitHubApiCache.Reset();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FetchPushedDatesAsync_NoRepos_ReturnsEmptyNotAnError()
    {
        var client = CreateClient([]);

        var dates = await client.FetchPushedDatesAsync([]);

        Assert.Empty(dates);
    }
}
