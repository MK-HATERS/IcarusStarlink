using System.Net;
using IcarusStarlink.Catalog.Ue4ss;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Catalog.Tests.Ue4ss;

[Collection(IcarusStarlink.Catalog.Tests.GitHub.GitHubApiCacheCollection.Name)]
public class Ue4ssReleaseClientTests
{
    public Ue4ssReleaseClientTests() => IcarusStarlink.Catalog.GitHub.GitHubApiCache.Reset();

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
        Assert.Null(result.Digest); // RealisticReleaseJson's matched asset has no "digest" field
    }

    [Fact]
    public async Task GetLatestStableReleaseAsync_AssetHasDigest_MapsItThrough()
    {
        // A real bug found live: this app's own self-update verifies its download's SHA-256
        // against GitHub's own asset digest, but the UE4SS loader download had no equivalent field
        // wired through at all, so it could never be verified even when GitHub provides one.
        const string json = """
            {
                "tag_name": "v3.0.1",
                "assets": [
                    {
                        "name": "UE4SS_v3.0.1.zip",
                        "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v3.0.1/UE4SS_v3.0.1.zip",
                        "digest": "sha256:4b9a4ac59f3c3aa32273260df6cf4bf358d1c46f8415126aa35b6380d0abb8f7"
                    }
                ]
            }
            """;
        var client = CreateClient(new Dictionary<string, string> { [ReleaseUrl] = json });

        var result = await client.GetLatestStableReleaseAsync();

        Assert.NotNull(result);
        Assert.Equal("sha256:4b9a4ac59f3c3aa32273260df6cf4bf358d1c46f8415126aa35b6380d0abb8f7", result.Digest);
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

    private const string DevBuildUrl = "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/releases/tags/experimental-latest";

    // The real experimental-latest assets (2026-10-17), trimmed: the basic build, the big dev build
    // with debug symbols, and two extras.
    private const string RealisticDevBuildJson = """
        {
            "tag_name": "experimental-latest",
            "assets": [
                { "name": "zDEV-UE4SS_v3.0.1-1181-g83330635.zip", "browser_download_url": "https://example.com/zDEV.zip" },
                { "name": "UE4SS_v3.0.1-1181-g83330635.zip", "browser_download_url": "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/experimental-latest/UE4SS_v3.0.1-1181-g83330635.zip",
                  "digest": "sha256:b9a166a756605b21058a64cde93bcaab1a855dba3553e5c7e6ce0fa8fdfad7b9" },
                { "name": "zCustomGameConfigs.zip", "browser_download_url": "https://example.com/c.zip" },
                { "name": "zMapGenBP.zip", "browser_download_url": "https://example.com/m.zip" }
            ]
        }
        """;

    [Fact]
    public async Task GetLatestDevBuildAsync_RealisticResponse_PicksTheBasicBuildWithItsNumberAndCommit()
    {
        var client = CreateClient(new Dictionary<string, string> { [DevBuildUrl] = RealisticDevBuildJson });

        var build = await client.GetLatestDevBuildAsync();

        Assert.NotNull(build);
        Assert.Equal(("3.0.1", 1181, "83330635"), (build.Version, build.BuildNumber, build.Commit));
        Assert.EndsWith("/UE4SS_v3.0.1-1181-g83330635.zip", build.DownloadUrl);
        Assert.Equal("sha256:b9a166a756605b21058a64cde93bcaab1a855dba3553e5c7e6ce0fa8fdfad7b9", build.Digest);
    }

    [Fact]
    public async Task GetLatestDevBuildAsync_Unreachable_ReturnsNull() =>
        Assert.Null(await CreateClient(new Dictionary<string, string>()).GetLatestDevBuildAsync());

    private static string CommitListJson(string sha, string date) =>
        $$"""[{ "sha": "{{sha}}", "commit": { "committer": { "date": "{{date}}" } } }]""";

    [Fact]
    public async Task CompareBuildsAsync_OrdersBuildsByTheirCommitDates()
    {
        const string commits = "https://api.github.com/repos/UE4SS-RE/RE-UE4SS/commits";
        var client = CreateClient(new Dictionary<string, string>
        {
            [$"{commits}?sha=1c1a1497&per_page=1"] = CommitListJson("1c1a1497f942c707f47ba668db75b25e86f6c08a", "2026-08-08T09:35:47Z"),
            [$"{commits}?sha=83330635&per_page=1"] = CommitListJson("83330635aaaabbbbccccddddeeeeffff00001111", "2026-10-17T16:20:00Z"),
        });

        Assert.Equal(Ue4ssBuildOrder.Newer, await client.CompareBuildsAsync("1c1a1497", "83330635"));
        Assert.Equal(Ue4ssBuildOrder.Older, await client.CompareBuildsAsync("83330635", "1c1a1497"));
        Assert.Equal(Ue4ssBuildOrder.Same, await client.CompareBuildsAsync("1c1a1497", "1c1a1497f942"));
        Assert.Equal(Ue4ssBuildOrder.Unknown, await client.CompareBuildsAsync("1c1a1497", "deadbeef"));
        Assert.Equal(Ue4ssBuildOrder.Unknown, await client.CompareBuildsAsync("not a commit", "83330635"));
    }
}
