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

    /// <summary>Regression: mods hosted on raw.githubusercontent.com never got a Last Updated date.</summary>
    [Fact]
    public void Extract_RawGitHubUserContentUrl_ReturnsOwnerAndRepo()
    {
        var key = GitHubRepoKey.Extract("https://raw.githubusercontent.com/TheLysdexicOne/Icarus-Mods/main/Mods/Some_Mod.EXMODZ");

        Assert.Equal(("TheLysdexicOne", "Icarus-Mods"), key);
    }

    /// <summary>Regression: "github.com/" anywhere in the text matched, inside another host or a query string.</summary>
    [Theory]
    [InlineData("https://example.com/download?from=github.com/a/b/c")]
    [InlineData("https://notgithub.com/a/b/c")]
    public void Extract_GitHubOnlyInsideAnotherAddress_ReturnsNull(string url)
    {
        Assert.Null(GitHubRepoKey.Extract(url));
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
