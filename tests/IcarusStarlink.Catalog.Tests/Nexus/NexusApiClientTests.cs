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

    // Real shape, confirmed against Nexus's own official node-nexus-api client source (IModInfo).
    private const string RealisticModInfoJson = """
        {
            "mod_id": 289, "game_id": 1, "domain_name": "icarus", "category_id": 2,
            "contains_adult_content": false, "name": "Rada's Cheat Menu", "summary": "An in-game cheat menu.",
            "description": "[b]Full[/b] description in BBCode.", "version": "2.6", "author": "Rada",
            "uploaded_by": "Rada", "status": "published", "available": true,
            "created_timestamp": 1700000000, "created_time": "2023-11-14T22:13:20.000+00:00",
            "updated_timestamp": 1700000000, "updated_time": "2023-11-14T22:13:20.000+00:00",
            "allow_rating": true, "endorsement_count": 10, "mod_downloads": 100
        }
        """;

    [Fact]
    public async Task GetModInfoAsync_RealisticShape_ParsesNameAuthorSummary()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v1/games/icarus/mods/289"] = RealisticModInfoJson,
        });

        var result = await client.GetModInfoAsync("some-key", "icarus", 289);

        Assert.NotNull(result);
        Assert.Equal("Rada's Cheat Menu", result.Name);
        Assert.Equal("Rada", result.Author);
        Assert.Equal("An in-game cheat menu.", result.Summary);
        Assert.Equal("2.6", result.Version);
    }

    [Fact]
    public async Task GetModInfoAsync_RejectedKey_ReturnsNullNotAnException()
    {
        var client = CreateClient(
            new Dictionary<string, string> { ["https://api.nexusmods.com/v1/games/icarus/mods/289"] = """{"message":"Invalid API Key"}""" },
            new Dictionary<string, HttpStatusCode> { ["https://api.nexusmods.com/v1/games/icarus/mods/289"] = HttpStatusCode.Unauthorized });

        var result = await client.GetModInfoAsync("wrong-key", "icarus", 289);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetModInfoAsync_HtmlEntitiesInNameAndSummary_AreDecoded()
    {
        // Nexus's web UI renders these fields as HTML — a real summary containing "&amp;" (for a
        // literal "&") is expected content, not something to show raw in a plain-text UI.
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v1/games/icarus/mods/289"] = """
                {"mod_id": 289, "author": "Rada", "version": "2.6", "name": "Cheats &amp; Tools", "summary": "Toggle fog on &amp; off."}
                """,
        });

        var result = await client.GetModInfoAsync("some-key", "icarus", 289);

        Assert.NotNull(result);
        Assert.Equal("Cheats & Tools", result.Name);
        Assert.Equal("Toggle fog on & off.", result.Summary);
    }

    [Fact]
    public async Task GetModInfoAsync_ModUnderModeration_NameAbsent_StillParsesAuthor()
    {
        // Per Nexus's own documented IModInfo shape: "name" is absent specifically when a mod is
        // under moderation — the rest of the response still comes through.
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v1/games/icarus/mods/500"] = """{"mod_id": 500, "author": "SomeAuthor", "version": "1.0"}""",
        });

        var result = await client.GetModInfoAsync("some-key", "icarus", 500);

        Assert.NotNull(result);
        Assert.Null(result.Name);
        Assert.Equal("SomeAuthor", result.Author);
    }

    [Fact]
    public async Task GetModInfoAsync_RealisticShape_ParsesModIdAndPictureUrl()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v1/games/icarus/mods/289"] = """
                {"mod_id": 289, "author": "Rada", "version": "2.6", "name": "Rada's Cheat Menu",
                 "picture_url": "https://staticdelivery.nexusmods.com/mods/5869/images/289/header.jpg"}
                """,
        });

        var result = await client.GetModInfoAsync("some-key", "icarus", 289);

        Assert.NotNull(result);
        Assert.Equal(289, result.ModId);
        Assert.Equal("https://staticdelivery.nexusmods.com/mods/5869/images/289/header.jpg", result.PictureUrl);
    }

    [Theory]
    [InlineData(NexusModList.Trending, "https://api.nexusmods.com/v1/games/icarus/mods/trending")]
    [InlineData(NexusModList.Latest, "https://api.nexusmods.com/v1/games/icarus/mods/latest_added")]
    [InlineData(NexusModList.Updated, "https://api.nexusmods.com/v1/games/icarus/mods/latest_updated")]
    public async Task GetModListAsync_EachKind_HitsItsOwnRealEndpointAndParsesTheArray(NexusModList kind, string expectedUrl)
    {
        // A JSON array of the same IModInfo objects the single-mod endpoint returns — confirmed
        // against Nexus's own official node-nexus-api client source (getTrending/getLatestAdded/
        // getLatestUpdated all share IModInfo[]).
        var client = CreateClient(new Dictionary<string, string>
        {
            [expectedUrl] = """
                [
                  {"mod_id": 10, "name": "Mod A &amp; Friends", "author": "AuthorA", "version": "1.0",
                   "summary": "First.", "picture_url": "https://staticdelivery.nexusmods.com/a.jpg"},
                  {"mod_id": 20, "name": "Mod B", "author": "AuthorB", "version": "2.0", "summary": "Second."}
                ]
                """,
        });

        var result = await client.GetModListAsync("some-key", "icarus", kind);

        Assert.Equal(2, result.Count);
        Assert.Equal(10, result[0].ModId);
        Assert.Equal("Mod A & Friends", result[0].Name);
        Assert.Equal("https://staticdelivery.nexusmods.com/a.jpg", result[0].PictureUrl);
        Assert.Equal(20, result[1].ModId);
        Assert.Null(result[1].PictureUrl);
    }

    [Fact]
    public async Task GetModFilesAsync_RealisticWrappedShape_ParsesTheFilesArray()
    {
        // The response wraps the array ({"files":[...],"file_updates":[...]}) per the official
        // client's IModFiles — unlike the browse lists, which return a bare array.
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v1/games/icarus/mods/289/files"] = """
                {
                  "files": [
                    {"file_id": 1111, "name": "Cheat Menu", "version": "2.6", "category_name": "MAIN",
                     "is_primary": true, "file_name": "CheatMenu-289-2-6.zip", "size_kb": 500},
                    {"file_id": 900, "name": "Old", "version": "2.5", "category_name": "OLD_VERSION",
                     "is_primary": false, "file_name": "CheatMenu-289-2-5.zip", "size_kb": 480}
                  ],
                  "file_updates": []
                }
                """,
        });

        var result = await client.GetModFilesAsync("some-key", "icarus", 289);

        Assert.Equal(2, result.Count);
        Assert.Equal(1111, result[0].FileId);
        Assert.Equal("CheatMenu-289-2-6.zip", result[0].FileName);
        Assert.True(result[0].IsPrimary);
        Assert.Equal("OLD_VERSION", result[1].CategoryName);
    }

    [Fact]
    public async Task SearchModsAsync_RealisticGraphShape_ParsesNodesAndDecodesHtml()
    {
        // The v2 GraphQL wrapper — camelCase fields, {"data":{"mods":{"nodes":[...]}}} — captured
        // from a live probe of the real endpoint during planning.
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v2/graphql"] = """
                {"data":{"mods":{"nodes":[
                  {"modId":289,"name":"Cheats &amp; Tools","version":"2.6","summary":"Fog on &amp; off.",
                   "pictureUrl":"https://staticdelivery.nexusmods.com/289.png","author":"MisterRada03"},
                  {"modId":59,"name":"Other","version":"1.0","summary":null,"pictureUrl":null,"author":"nik4kin"}
                ],"totalCount":2}}}
                """,
        });

        var result = await client.SearchModsAsync("some-key", "icarus", "cheat");

        Assert.Equal(2, result.Count);
        Assert.Equal(289, result[0].ModId);
        Assert.Equal("Cheats & Tools", result[0].Name);
        Assert.Equal("Fog on & off.", result[0].Summary);
        Assert.Equal("https://staticdelivery.nexusmods.com/289.png", result[0].PictureUrl);
        Assert.Null(result[1].PictureUrl);
    }

    [Fact]
    public async Task SearchModsAsync_NullApiKey_StillWorks()
    {
        // The v2 GraphQL endpoint answers unauthenticated (confirmed live) — search must not
        // require a configured key the way the v1 list endpoints do.
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://api.nexusmods.com/v2/graphql"] = """{"data":{"mods":{"nodes":[],"totalCount":0}}}""",
        });

        var result = await client.SearchModsAsync(null, "icarus", "anything");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetModListAsync_RejectedKey_ThrowsInvalidOperation()
    {
        var url = "https://api.nexusmods.com/v1/games/icarus/mods/trending";
        var client = CreateClient(
            new Dictionary<string, string> { [url] = """{"message":"Invalid API Key"}""" },
            new Dictionary<string, HttpStatusCode> { [url] = HttpStatusCode.Unauthorized });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetModListAsync("wrong-key", "icarus", NexusModList.Trending));
    }
}
