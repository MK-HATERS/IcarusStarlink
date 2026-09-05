using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.Tests.GitHub;

public class GitHubBlobUrlTests
{
    /// <summary>
    /// Regression guard: a real catalog entry (Ferani Homestead Expanded, and 59 other file URLs
    /// across 9 different authors, confirmed live in the Daedalus catalog) pointed its ExmodzUrl at
    /// exactly this shape — GitHub's own HTML file-viewer page, not the file's raw bytes. Downloading
    /// it and treating the response as an archive failed with "isn't a recognized archive format",
    /// giving no hint that the actual problem was the URL shape.
    /// </summary>
    [Fact]
    public void ToRawContentUrl_BlobUrl_RewritesToRawGithubusercontent()
    {
        var result = GitHubBlobUrl.ToRawContentUrl("https://github.com/FeraniShades/Icarus_Mods/blob/main/Ferani_Homestead_Expanded.EXMODZ");

        Assert.Equal("https://raw.githubusercontent.com/FeraniShades/Icarus_Mods/main/Ferani_Homestead_Expanded.EXMODZ", result);
    }

    [Fact]
    public void ToRawContentUrl_BlobUrlWithNestedPath_PreservesTheFullPath()
    {
        var result = GitHubBlobUrl.ToRawContentUrl("https://github.com/Owner/Repo/blob/main/sub/folder/Mod.EXMODZ");

        Assert.Equal("https://raw.githubusercontent.com/Owner/Repo/main/sub/folder/Mod.EXMODZ", result);
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/AgentKush/Icarus-mods/main/Agents_BioLab/Agents_BioLab.EXMODZ")]
    [InlineData("https://github.com/AgentKush/Icarus-mods/raw/main/Agents_BioLab/Agents_BioLab.EXMODZ")]
    [InlineData("https://github.com/WZG-Mods/wzg-icarus-balance-overhaul/releases/download/WZG_680/WZG_Mod_V680_P.pak")]
    [InlineData("https://example.com/not-github.pak")]
    public void ToRawContentUrl_AlreadyDirectOrNonGitHubUrl_ReturnsUnchanged(string url)
    {
        Assert.Equal(url, GitHubBlobUrl.ToRawContentUrl(url));
    }
}
