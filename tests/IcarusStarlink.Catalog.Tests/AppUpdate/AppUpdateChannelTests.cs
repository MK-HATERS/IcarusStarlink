using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.Catalog.Tests.AppUpdate;

/// <summary>
/// Update channels (user request 2026-10-23): betas ship as GitHub prereleases tagged
/// vX.Y.Z-beta.N; the Beta channel also offers them, Stable doesn't, a beta tester is told when a
/// stable release is ready, and Stable while on a beta offers the way back.
/// </summary>
[Collection(IcarusStarlink.Catalog.Tests.GitHub.GitHubApiCacheCollection.Name)]
public class AppUpdateChannelTests
{
    public AppUpdateChannelTests() => IcarusStarlink.Catalog.GitHub.GitHubApiCache.Reset();

    private static AppUpdateRelease Release(string version) => new(version, "", $"https://example.test/{version}.zip");

    [Theory]
    [InlineData("2.10.0", "2.9.1")]
    [InlineData("2.10.0", "2.10.0-beta.2")]
    [InlineData("2.10.0-beta.2", "2.10.0-beta.1")]
    [InlineData("2.10.0-beta.10", "2.10.0-beta.9")]
    [InlineData("2.10.0-beta.1", "2.9.1")]
    [InlineData("v3.0.0", "2.99.99")]
    public void AppReleaseVersion_Orders(string newer, string older)
    {
        var a = AppReleaseVersion.TryParse(newer)!;
        var b = AppReleaseVersion.TryParse(older)!;
        Assert.True(a > b);
        Assert.True(b < a);
    }

    [Theory]
    [InlineData("2.9", "2.9.0")]
    [InlineData("2.9.1+6f1e2a", "2.9.1")]
    [InlineData("v2.10.0-beta.1", "2.10.0-beta.1")]
    public void AppReleaseVersion_SameRelease(string a, string b) =>
        Assert.Equal(0, AppReleaseVersion.TryParse(a)!.CompareTo(AppReleaseVersion.TryParse(b)));

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("beta")]
    // Four numbers used to read as "3.2.0" (ToString writes three), so a Nexus file "3.2.0.1"
    // would have been offered and fetched as release 3.2.0: not a Starlink version, skipped.
    [InlineData("3.2.0.1")]
    [InlineData("v3.2.0.1-beta.1")]
    public void AppReleaseVersion_NotAVersion(string text) => Assert.Null(AppReleaseVersion.TryParse(text));

    [Fact]
    public void Pick_StableChannelNeverOffersABeta() =>
        Assert.Equal("2.9.1", AppUpdateChoice.Pick(Release("2.9.1"), Release("2.10.0-beta.1"), betaChannel: false)!.Version);

    [Theory]
    [InlineData("2.9.1", "2.10.0-beta.1", "2.10.0-beta.1")]
    [InlineData("2.10.0", "2.10.0-beta.3", "2.10.0")] // the final release beats its own betas
    [InlineData(null, "2.10.0-beta.1", "2.10.0-beta.1")]
    [InlineData("2.9.1", null, "2.9.1")]
    public void Pick_BetaChannelOffersTheNewerOfBoth(string? stable, string? beta, string expected) =>
        Assert.Equal(expected, AppUpdateChoice.Pick(stable is null ? null : Release(stable), beta is null ? null : Release(beta), betaChannel: true)!.Version);

    [Theory]
    [InlineData("2.9.1", "2.10.0", false, AppUpdateOffer.Newer)]
    [InlineData("2.9.1", "2.9.1", false, AppUpdateOffer.None)]
    [InlineData("2.9.1", "2.10.0-beta.1", true, AppUpdateOffer.Newer)]
    [InlineData("2.10.0-beta.1", "2.10.0-beta.2", true, AppUpdateOffer.Newer)]
    [InlineData("2.10.0-beta.2", "2.10.0", true, AppUpdateOffer.StableReady)]
    [InlineData("2.10.0-beta.2", "2.10.0", false, AppUpdateOffer.StableReady)]
    [InlineData("2.10.0-beta.2", "2.9.1", false, AppUpdateOffer.SwitchBackToStable)] // lower number, still offered
    [InlineData("2.10.0-beta.2", "2.9.1", true, AppUpdateOffer.None)] // on Beta, an older stable isn't news
    [InlineData("2.10.0-beta.2", "2.10.0-beta.2", true, AppUpdateOffer.None)]
    [InlineData("unknown", "2.10.0", false, AppUpdateOffer.None)]
    public void Classify(string installed, string offered, bool betaChannel, AppUpdateOffer expected) =>
        Assert.Equal(expected, AppUpdateChoice.Classify(installed, Release(offered), betaChannel));

    [Fact]
    public async Task GetLatestBetaRelease_ReadsTheBetaPointersReleaseJson()
    {
        var client = new AppUpdateClient(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://github.com/MK-HATERS/IcarusStarlink/releases/download/beta/release.json"] =
                """{"version":"2.10.0-beta.2","notes":"Two panels","asset":"IcarusStarlink-2.10.0-beta.2.zip","sha256":"abc123"}""",
        })));

        var beta = await client.GetLatestBetaReleaseAsync();

        Assert.NotNull(beta);
        Assert.Equal("2.10.0-beta.2", beta.Version);
        Assert.Equal("https://github.com/MK-HATERS/IcarusStarlink/releases/download/v2.10.0-beta.2/IcarusStarlink-2.10.0-beta.2.zip", beta.AssetBrowserDownloadUrl);
        Assert.Equal("sha256:abc123", beta.AssetDigest);
    }

    [Fact]
    public async Task GetLatestBetaRelease_NoPointer_FallsBackToTheNewestPrereleaseInTheList()
    {
        var client = new AppUpdateClient(new HttpClient(new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/repos/MK-HATERS/IcarusStarlink/releases?per_page=20"] = """
                [
                  {"tag_name":"v2.10.0-beta.1","prerelease":true,"draft":false,"body":"one","assets":[{"name":"IcarusStarlink-2.10.0-beta.1.zip","browser_download_url":"https://x/b1.zip"}]},
                  {"tag_name":"v2.10.0-beta.2","prerelease":true,"draft":false,"body":"two","assets":[{"name":"IcarusStarlink-2.10.0-beta.2.zip","browser_download_url":"https://x/b2.zip"}]},
                  {"tag_name":"v2.10.0-beta.3","prerelease":true,"draft":true,"body":"draft","assets":[{"name":"IcarusStarlink-2.10.0-beta.3.zip","browser_download_url":"https://x/b3.zip"}]},
                  {"tag_name":"v2.9.1","prerelease":false,"draft":false,"body":"stable","assets":[{"name":"IcarusStarlink-2.9.1.zip","browser_download_url":"https://x/s.zip"}]}
                ]
                """,
        })));

        var beta = await client.GetLatestBetaReleaseAsync();

        Assert.NotNull(beta);
        Assert.Equal("2.10.0-beta.2", beta.Version);
        Assert.Equal("https://x/b2.zip", beta.AssetBrowserDownloadUrl);
    }

    [Fact]
    public async Task GetLatestBetaRelease_NothingThere_IsNull() =>
        Assert.Null(await new AppUpdateClient(new HttpClient(new FakeHttpMessageHandler([]))).GetLatestBetaReleaseAsync());
}
