using System.Net;
using IcarusStarlink.Catalog.Ue4ss;

namespace IcarusStarlink.Catalog.Tests.Ue4ss;

public class Ue4ssReleaseClientTests
{
    private const string ReleaseUrl = "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/latest";

    private static Ue4ssReleaseClient CreateClient(
        Dictionary<string, string> responsesByUrl, Dictionary<string, HttpStatusCode>? statusCodesByUrl = null) =>
        new(new HttpClient(new FakeHttpMessageHandler(responsesByUrl, statusCodesByUrl)));

    // Real shape, trimmed to the fields this client reads — confirmed via gh api against the real
    // UE4SS-RE/RE-UE4SS repo during Phase 8.5 planning, including the real "extra" assets
    // (zDEV-/zCustomGameConfigs/zMapGenBP) a naive "first asset" pick would wrongly grab.
    private const string RealisticReleaseJson = """
        {
            "tag_name": "v3.0.1",
            "assets": [
                {
                    "name": "UE4SS_v3.0.1.zip",
                    "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v3.0.1/UE4SS_v3.0.1.zip"
                },
                {
                    "name": "zCustomGameConfigs.zip",
                    "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v3.0.1/zCustomGameConfigs.zip"
                },
                {
                    "name": "zDEV-UE4SS_v3.0.1.zip",
                    "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v3.0.1/zDEV-UE4SS_v3.0.1.zip"
                },
                {
                    "name": "zMapGenBP.zip",
                    "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v3.0.1/zMapGenBP.zip"
                }
            ]
        }
        """;

    [Fact]
    public async Task GetLatestStableReleaseAsync_RealisticResponse_StripsLeadingVAndPicksBasicAsset()
    {
        var client = CreateClient(new Dictionary<string, string> { [ReleaseUrl] = RealisticReleaseJson });

        var result = await client.GetLatestStableReleaseAsync();

        Assert.NotNull(result);
        Assert.Equal("3.0.1", result.Version);
        Assert.Equal("https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v3.0.1/UE4SS_v3.0.1.zip", result.DownloadUrl);
    }

    [Fact]
    public async Task GetLatestStableReleaseAsync_NoMatchingAsset_ReturnsNull()
    {
        const string json = """{"tag_name": "v3.0.1", "assets": [{"name": "zMapGenBP.zip", "browser_download_url": "https://example.com/x.zip"}]}""";
        var client = CreateClient(new Dictionary<string, string> { [ReleaseUrl] = json });

        Assert.Null(await client.GetLatestStableReleaseAsync());
    }

    [Fact]
    public async Task GetLatestStableReleaseAsync_Unreachable_ReturnsNullNotThrows()
    {
        var client = CreateClient(new Dictionary<string, string>());

        Assert.Null(await client.GetLatestStableReleaseAsync());
    }
}
