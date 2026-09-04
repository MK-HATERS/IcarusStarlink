using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.Catalog.Tests.AppUpdate;

public class AppUpdateClientTests
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/MK-HATERS/IcarusStarlink/releases/latest";
    private const string BrowserDownloadUrl = "https://github.com/MK-HATERS/IcarusStarlink/releases/download/v0.10.0/IcarusStarlink-0.10.0.zip";

    private static AppUpdateClient CreateClient(Dictionary<string, string> responsesByUrl) =>
        new(new HttpClient(new FakeHttpMessageHandler(responsesByUrl)));

    // The exact "digest" value below is a plausible-shaped placeholder, not one confirmed from a
    // real captured GitHub API response — see AppUpdateAssetDto.Digest's own doc comment.
    private const string RealisticDigest = "sha256:4b9a4ac59f3c3aa32273260df6cf4bf358d1c46f8415126aa35b6380d0abb8f7";

    private const string RealisticReleaseJson = $$"""
        {
            "tag_name": "v0.10.0",
            "body": "## What's new\n- fixed things",
            "assets": [
                {
                    "name": "IcarusStarlink-0.10.0.zip",
                    "browser_download_url": "{{BrowserDownloadUrl}}",
                    "digest": "{{RealisticDigest}}"
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
        Assert.Equal(RealisticDigest, result.AssetDigest);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NoDigestField_LeavesAssetDigestNull()
    {
        // Defensive coverage for GitHub's response shape possibly varying (an older cached
        // response, or the field not existing at all): AppUpdateReleaseDto must not require it.
        const string json = """
            {
                "tag_name": "v0.10.0",
                "assets": [{"name": "IcarusStarlink-0.10.0.zip", "browser_download_url": "https://example.com/x.zip"}]
            }
            """;
        var client = CreateClient(new Dictionary<string, string> { [LatestReleaseUrl] = json });

        var result = await client.GetLatestReleaseAsync();

        Assert.NotNull(result);
        Assert.Null(result.AssetDigest);
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
        // No AssetDigest at all — this doubles as the "digest absent still proceeds" coverage:
        // an old/uncommon GitHub response shape must not block every download.
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

    // The real SHA-256 of the ASCII bytes "zip-bytes" — the exact body FakeHttpMessageHandler
    // serves for BrowserDownloadUrl in these tests (confirmed with `sha256sum`, not guessed).
    private const string MatchingDigestForZipBytes = "sha256:4b9a4ac59f3c3aa32273260df6cf4bf358d1c46f8415126aa35b6380d0abb8f7";

    [Fact]
    public async Task DownloadAssetAsync_DigestMatches_KeepsFile()
    {
        var client = CreateClient(new Dictionary<string, string> { [BrowserDownloadUrl] = "zip-bytes" });
        var release = new AppUpdateRelease("0.10.0", "", BrowserDownloadUrl, MatchingDigestForZipBytes);
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

    [Fact]
    public async Task DownloadAssetAsync_DigestMismatch_DeletesFileAndThrows()
    {
        var client = CreateClient(new Dictionary<string, string> { [BrowserDownloadUrl] = "zip-bytes" });
        var wrongDigest = "sha256:" + new string('0', 64);
        var release = new AppUpdateRelease("0.10.0", "", BrowserDownloadUrl, wrongDigest);
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DownloadAssetAsync(release, destinationPath));

            Assert.Contains("integrity verification", ex.Message, StringComparison.OrdinalIgnoreCase);
            // The caller must never be able to hand a mismatched download off to Apply.
            Assert.False(File.Exists(destinationPath));
        }
        finally
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
        }
    }
}
