using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.Tests.GitHub;

public class GitHubRepoDateClientTests
{
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

    [Fact]
    public async Task FetchPushedDatesAsync_NoRepos_ReturnsEmptyNotAnError()
    {
        var client = CreateClient([]);

        var dates = await client.FetchPushedDatesAsync([]);

        Assert.Empty(dates);
    }
}
