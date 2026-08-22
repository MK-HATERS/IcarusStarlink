using System.Net;
using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.Catalog.Tests.Nexus;

public class NexusApiClientTests
{
    private static NexusApiClient CreateClient(
        Dictionary<string, string> responsesByUrl, Dictionary<string, HttpStatusCode>? statusCodesByUrl = null) =>
        new(new HttpClient(new FakeHttpMessageHandler(responsesByUrl, statusCodesByUrl)));

    // Real shape, confirmed against Nexus's own official node-nexus-api client source (IValidateKeyResponse).
    private const string RealisticValidateJson = """
        {
            "user_id": 12345,
            "key": "abc123",
            "name": "TestUser",
            "is_premium": true,
            "is_supporter": false,
            "email": "test@example.com",
            "profile_url": "https://www.nexusmods.com/users/12345"
        }
        """;

    [Fact]
    public async Task ValidateKeyAsync_ValidKey_ReturnsUserInfo()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v1/users/validate"] = RealisticValidateJson,
        });

        var result = await client.ValidateKeyAsync("real-looking-key");

        Assert.NotNull(result);
        Assert.Equal(12345, result.UserId);
        Assert.Equal("TestUser", result.Name);
        Assert.True(result.IsPremium);
        Assert.False(result.IsSupporter);
        Assert.Equal("test@example.com", result.Email);
    }

    [Fact]
    public async Task ValidateKeyAsync_RejectedKey_ReturnsNullNotAnException()
    {
        var client = CreateClient(
            new Dictionary<string, string> { ["https://api.nexusmods.com/v1/users/validate"] = """{"message":"Invalid API Key"}""" },
            new Dictionary<string, HttpStatusCode> { ["https://api.nexusmods.com/v1/users/validate"] = HttpStatusCode.Unauthorized });

        var result = await client.ValidateKeyAsync("wrong-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateKeyAsync_ServiceUnavailable_ThrowsRatherThanReturningNull()
    {
        // Distinguishes "Nexus is down / network failure" (throws) from "that key is wrong"
        // (returns null) — the Settings onboarding flow needs to tell these apart for its error
        // message.
        var client = CreateClient(
            new Dictionary<string, string> { ["https://api.nexusmods.com/v1/users/validate"] = "Service Unavailable" },
            new Dictionary<string, HttpStatusCode> { ["https://api.nexusmods.com/v1/users/validate"] = HttpStatusCode.ServiceUnavailable });

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ValidateKeyAsync("some-key"));
    }

    [Fact]
    public async Task GetUpdatedModsAsync_RealisticShape_ParsesEveryEntry()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v1/games/icarus/mods/updated?period=1w"] = """
                [
                    {"mod_id": 42, "latest_file_update": 1700000000, "latest_mod_activity": 1700000500},
                    {"mod_id": 99, "latest_file_update": 1700100000, "latest_mod_activity": 1700100500}
                ]
                """,
        });

        var entries = await client.GetUpdatedModsAsync("some-key", "icarus", "1w");

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.ModId == 42 && e.LatestFileUpdateUnix == 1700000000);
        Assert.Contains(entries, e => e.ModId == 99 && e.LatestFileUpdateUnix == 1700100000);
    }

    [Fact]
    public async Task GetUpdatedModsAsync_RejectedKey_Throws()
    {
        var client = CreateClient(
            new Dictionary<string, string> { ["https://api.nexusmods.com/v1/games/icarus/mods/updated?period=1w"] = """{"message":"Invalid API Key"}""" },
            new Dictionary<string, HttpStatusCode> { ["https://api.nexusmods.com/v1/games/icarus/mods/updated?period=1w"] = HttpStatusCode.Unauthorized });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetUpdatedModsAsync("wrong-key", "icarus", "1w"));
    }

    // Real shape, confirmed against Nexus's own official node-nexus-api client source (IDownloadURL).
    private const string RealisticDownloadLinksJson = """
        [
            {"URI": "https://cdn.nexusmods.com/icarus/290/1234/Nearby_Crafting-290-1-3-7.zip?some=signed-params", "name": "Nexus CDN", "short_name": "Nexus CDN"}
        ]
        """;

    [Fact]
    public async Task GetDownloadLinksAsync_PremiumStyle_NoKeyOrExpires_OmitsQueryString()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            // No "?key=...&expires=..." suffix — the exact URL this call must hit for a premium account.
            ["https://api.nexusmods.com/v1/games/icarus/mods/290/files/1234/download_link"] = RealisticDownloadLinksJson,
        });

        var links = await client.GetDownloadLinksAsync("premium-key", "icarus", 290, 1234, key: null, expires: null);

        var link = Assert.Single(links);
        Assert.Equal("Nexus CDN", link.ServerName);
        Assert.Contains("Nearby_Crafting", link.Uri);
    }

    [Fact]
    public async Task GetDownloadLinksAsync_NonPremiumStyle_WithKeyAndExpires_IncludesQueryString()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v1/games/icarus/mods/290/files/1234/download_link?key=abc123&expires=1700000000"] = RealisticDownloadLinksJson,
        });

        var links = await client.GetDownloadLinksAsync("some-key", "icarus", 290, 1234, key: "abc123", expires: 1700000000);

        Assert.Single(links);
    }

    [Fact]
    public async Task GetDownloadLinksAsync_RejectedOrExpiredKey_Throws()
    {
        var client = CreateClient(
            new Dictionary<string, string> { ["https://api.nexusmods.com/v1/games/icarus/mods/290/files/1234/download_link?key=stale&expires=1"] = """{"message":"Key expired"}""" },
            new Dictionary<string, HttpStatusCode> { ["https://api.nexusmods.com/v1/games/icarus/mods/290/files/1234/download_link?key=stale&expires=1"] = HttpStatusCode.Forbidden });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDownloadLinksAsync("some-key", "icarus", 290, 1234, "stale", 1));
    }
}
