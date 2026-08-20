using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.Tests.GitHub;

public class GitHubRepoKeyTests
{
    [Fact]
    public void Extract_ReleasesDownloadUrl_ReturnsOwnerAndRepo()
    {
        var key = GitHubRepoKey.Extract("https://github.com/WZG-Mods/wzg-icarus-balance-overhaul/releases/download/WZG_680/WZG_Mod_V680_P.pak");

        Assert.Equal(("WZG-Mods", "wzg-icarus-balance-overhaul"), key);
    }

    [Fact]
    public void Extract_RawFileUrl_ReturnsOwnerAndRepo()
    {
        var key = GitHubRepoKey.Extract("https://github.com/AgentKush/Icarus-mods/raw/main/Agents_BioLab/Agents_BioLab.EXMODZ");

        Assert.Equal(("AgentKush", "Icarus-mods"), key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/not-github.pak")]
    public void Extract_NoGitHubRepoInUrl_ReturnsNull(string? url)
    {
        Assert.Null(GitHubRepoKey.Extract(url));
    }
}
