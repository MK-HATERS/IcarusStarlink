using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.Catalog.Tests.AppUpdate;

[Collection(IcarusStarlink.Catalog.Tests.GitHub.GitHubApiCacheCollection.Name)]
public class AppUpdateClientTests
{
    public AppUpdateClientTests() => IcarusStarlink.Catalog.GitHub.GitHubApiCache.Reset();

    private const string LatestReleaseUrl = "https://api.github.com/repos/MK-HATERS/IcarusStarlink/releases/latest";
    private const string BrowserDownloadUrl = "https://github.com/MK-HATERS/IcarusStarlink/releases/download/v0.10.0/IcarusStarlink-0.10.0.zip";

    private static AppUpdateClient CreateClient(Dictionary<string, string> responsesByUrl) =>
        new(new HttpClient(new FakeHttpMessageHandler(responsesByUrl)));

    // The "sha256:<hex>" shape GitHub's API uses (see AppUpdateAssetDto.Digest's own doc comment);
    // the hex itself happens to be SHA-256("zip-bytes"), the same value as MatchingDigestForZipBytes.
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

    /// <summary>Stable from Nexus (2026-10-25): Nexus's servers in turn, progress for the update window, then GitHub's checksum.</summary>
    [Fact]
    public async Task DownloadAsync_TriesTheNextServer_ReportsProgress_AndChecksTheDigest()
    {
        var client = CreateClient(new Dictionary<string, string> { ["https://cdn-b.example/starlink.zip"] = "zip-bytes" });
        var destinationPath = Path.Combine(Path.GetTempPath(), $"starlink-update-{Guid.NewGuid():N}.zip");
        var reports = new List<AppUpdateDownloadProgress>();
        try
        {
            await client.DownloadAsync(["https://cdn-a.example/starlink.zip", "https://cdn-b.example/starlink.zip"], MatchingDigestForZipBytes,
                destinationPath, new SyncProgress(reports.Add));

            Assert.Equal("zip-bytes", File.ReadAllText(destinationPath));
            Assert.Equal(9, reports[^1].Received);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task GetReleaseAsync_ReadsThatVersionsReleaseJson()
    {
        var client = CreateClient(new Dictionary<string, string>
        {
            ["https://github.com/MK-HATERS/IcarusStarlink/releases/download/v3.0.2/release.json"] =
                """{"version":"3.0.2","notes":"fixes","asset":"IcarusStarlink-v3.0.2-win-x64.zip","sha256":"abc"}""",
        });

        var release = await client.GetReleaseAsync("v3.0.2");

        Assert.Equal("3.0.2", release!.Version);
        Assert.Equal("sha256:abc", release.AssetDigest);
        Assert.EndsWith("/v3.0.2/IcarusStarlink-v3.0.2-win-x64.zip", release.AssetBrowserDownloadUrl);
        Assert.Null(await client.GetReleaseAsync("9.9.9"));
    }

    /// <summary>Progress&lt;T&gt; posts to a sync context; this reports straight away.</summary>
    private sealed class SyncProgress(Action<AppUpdateDownloadProgress> report) : IProgress<AppUpdateDownloadProgress>
    {
        public void Report(AppUpdateDownloadProgress value) => report(value);
    }

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

    /// <summary>A download cut off partway (connection dropped, cancelled) used to leave the truncated zip on disk.</summary>
    [Fact]
    public async Task DownloadAssetAsync_ConnectionDropsPartway_DeletesThePartialFileAndThrows()
    {
        var client = new AppUpdateClient(new HttpClient(new DroppingHandler()));
        var release = new AppUpdateRelease("0.10.0", "", BrowserDownloadUrl, MatchingDigestForZipBytes);
        var destinationPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

        try
        {
            await Assert.ThrowsAsync<IOException>(() => client.DownloadAssetAsync(release, destinationPath));

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

    /// <summary>Serves a body that gives a few bytes and then fails, like a dropped connection.</summary>
    private sealed class DroppingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream()) });

        private sealed class DroppingStream : Stream
        {
            private bool _gaveSome;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_gaveSome)
                {
                    throw new IOException("The connection was reset.");
                }

                _gaveSome = true;
                buffer[offset] = (byte)'z';
                return 1;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
