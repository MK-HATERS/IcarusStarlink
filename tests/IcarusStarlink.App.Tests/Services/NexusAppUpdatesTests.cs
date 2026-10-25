using IcarusStarlink.App.Services;
using IcarusStarlink.Catalog.AppUpdate;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Stable updates from Starlink's Nexus page (user request 2026-10-24), betas from GitHub.</summary>
public sealed class NexusAppUpdatesTests
{
    private static AppUpdateRelease Release(string version, int? nexusFileId = null) => new(version, "", nexusFileId is null ? "https://github.com/x.zip" : "", null, nexusFileId);

    [Fact]
    public void PickStable_TakesTheNewer_AndNexusOnATie()
    {
        Assert.Equal(7, NexusAppUpdates.PickStable(Release("3.0.2", 7), Release("3.0.2"))!.NexusFileId);
        Assert.Equal("3.0.3", NexusAppUpdates.PickStable(Release("3.0.2", 7), Release("3.0.3"))!.Version); // not on Nexus yet
        Assert.Equal(7, NexusAppUpdates.PickStable(Release("3.0.4", 7), Release("3.0.3"))!.NexusFileId);
        Assert.Equal("3.0.3", NexusAppUpdates.PickStable(null, Release("3.0.3"))!.Version);
        Assert.Null(NexusAppUpdates.PickStable(null, null));
    }

    [Theory]
    [InlineData("nxm://icarus/mods/330/files/1200?key=abc&expires=1700000000&user_id=1", true)]
    [InlineData("nxm://icarus/mods/331/files/1200?key=abc&expires=1700000000&user_id=1", false)]
    [InlineData("nxm://skyrim/mods/330/files/1200?key=abc&expires=1700000000&user_id=1", false)]
    [InlineData("not a link", false)]
    public void IsStarlinkLink_OnlyForStarlinksOwnPage(string url, bool expected) =>
        Assert.Equal(expected, NexusAppUpdates.IsStarlinkLink(url));

    [Fact]
    public void DownloadPageUrl_OpensTheFilesTabOnThatFile() =>
        Assert.Equal("https://www.nexusmods.com/icarus/mods/330?tab=files&file_id=1200&nmm=1", NexusAppUpdates.DownloadPageUrl(1200));
}
