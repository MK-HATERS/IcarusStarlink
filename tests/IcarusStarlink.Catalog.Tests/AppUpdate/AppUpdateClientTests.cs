using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.Catalog.Tests.AppUpdate;

public class AppUpdateClientTests
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/MK-HATERS/IcarusStarlink/releases/latest";
    private const string AssetEndpointUrl = "https://api.github.com/repos/MK-HATERS/IcarusStarlink/releases/assets/12345";
    private const string BrowserDownloadUrl = "https://github.com/MK-HATERS/IcarusStarlink/releases/download/v0.10.0/IcarusStarlink-0.10.0.zip";

    private static AppUpdateClient CreateClient(Dictionary<string, string> responsesByUrl) =>
        new(new HttpClient(new FakeHttpMessageHandler(responsesByUrl)));

    private const string RealisticReleaseJson = $$"""
        {
            "tag_name": "v0.10.0",
            "body": "## What's new\n- fixed things",
            "assets": [
                {
                    "id": 12345,
                    "name": "IcarusStarlink-0.10.0.zip",
                    "browser_download_url": "{{BrowserDownloadUrl}}"
                },
                {
                    "id": 12346,
                    "name": "IcarusStarlink-0.10.0.zip.sha256",
                    "browser_download_url": "https://github.com/MK-HATERS/IcarusStarlink/releases/download/v0.10.0/IcarusStarlink-0.10.0.zip.sha256"
                }
            ]
        }
        """;

    [Fact]
    public async Task GetLatestReleaseAsync_RealisticResponse_StripsLeadingVAndPicksZipAsset()
    {
        var client = CreateClient(new Dictionary<string, string> { [LatestReleaseUrl] = RealisticReleaseJson });

        var result = await client.GetLatestReleaseAsync(gitHubToken: "a-token");

        Assert.NotNull(result);
        Assert.Equal("0.10.0", result.Version);
        Assert.Equal("## What's new\n- fixed things", result.ReleaseNotes);
        Assert.Equal(12345, result.AssetId);
        Assert.Equal(BrowserDownloadUrl, result.AssetBrowserDownloadUrl);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NoZipAsset_ReturnsNull()
    {
        const string json = """{"tag_name": "v0.10.0", "assets": [{"id": 1, "name": "readme.txt", "browser_download_url": "https://example.com/x.txt"}]}""";
        var client = CreateClient(new Dictionary<string, string> { [LatestReleaseUrl] = json });

        Assert.Null(await client.GetLatestReleaseAsync(gitHubToken: null));
    }

    [Fact]
    public async Task GetLatestReleaseAsync_Unreachable_ReturnsNullNotThrows()
    {
        var client = CreateClient(new Dictionary<string, string>());

        Assert.Null(await client.GetLatestReleaseAsync(gitHubToken: null));
    }

    [Fact]
    public async Task DownloadAssetAsync_WithToken_UsesAuthenticatedAssetIdEndpointNotBrowserUrl()
    {
        // Only the asset-id endpoint is mapped — if the client wrongly used the plain
        // browser_download_url (which 404s outside an authenticated browser session for a
        // private repo) instead, this would throw.
        var client = CreateClient(new Dictionary<string, string> { [AssetEndpointUrl] = "zip-bytes" });
        var release = new AppUpdateRelease("0.10.0", "", AssetId: 12345, BrowserDownloadUrl);
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

        try
        {
            await client.DownloadAssetAsync(release, gitHubToken: "a-token", destinationPath);

            Assert.Equal("zip-bytes", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task DownloadAssetAsync_NoToken_FallsBackToBrowserDownloadUrl()
    {
        // Only the plain browser_download_url is mapped — if the client wrongly used the
        // authenticated asset-id endpoint with no token to authenticate with, this would throw.
        var client = CreateClient(new Dictionary<string, string> { [BrowserDownloadUrl] = "zip-bytes" });
        var release = new AppUpdateRelease("0.10.0", "", AssetId: 12345, BrowserDownloadUrl);
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

        try
        {
            await client.DownloadAssetAsync(release, gitHubToken: null, destinationPath);

            Assert.Equal("zip-bytes", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }
}
