using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.Catalog.Tests.AppUpdate;

public class AppUpdateClientTests
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/MK-HATERS/IcarusStarlink/releases/latest";
    private const string BrowserDownloadUrl = "https://github.com/MK-HATERS/IcarusStarlink/releases/download/v0.10.0/IcarusStarlink-0.10.0.zip";

    private static AppUpdateClient CreateClient(Dictionary<string, string> responsesByUrl) =>
        new(new HttpClient(new FakeHttpMessageHandler(responsesByUrl)));

    private const string RealisticReleaseJson = $$"""
        {
            "tag_name": "v0.10.0",
            "body": "## What's new\n- fixed things",
            "assets": [
                {
                    "name": "IcarusStarlink-0.10.0.zip",
                    "browser_download_url": "{{BrowserDownloadUrl}}"
                },
                {
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

        var result = await client.GetLatestReleaseAsync();

        Assert.NotNull(result);
        Assert.Equal("0.10.0", result.Version);
        Assert.Equal("## What's new\n- fixed things", result.ReleaseNotes);
        Assert.Equal(BrowserDownloadUrl, result.AssetBrowserDownloadUrl);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NoZipAsset_ReturnsNull()
    {
        const string json = """{"tag_name": "v0.10.0", "assets": [{"name": "readme.txt", "browser_download_url": "https://example.com/x.txt"}]}""";
        var client = CreateClient(new Dictionary<string, string> { [LatestReleaseUrl] = json });

        Assert.Null(await client.GetLatestReleaseAsync());
    }

    [Fact]
    public async Task GetLatestReleaseAsync_Unreachable_ReturnsNullNotThrows()
    {
        var client = CreateClient(new Dictionary<string, string>());

        Assert.Null(await client.GetLatestReleaseAsync());
    }

    [Fact]
    public async Task DownloadAssetAsync_UsesBrowserDownloadUrl()
    {
        var client = CreateClient(new Dictionary<string, string> { [BrowserDownloadUrl] = "zip-bytes" });
        var release = new AppUpdateRelease("0.10.0", "", BrowserDownloadUrl);
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

        try
        {
            await client.DownloadAssetAsync(release, destinationPath);

            Assert.Equal("zip-bytes", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }
}
