using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.Catalog.Tests;

/// <summary>Links from catalog or Nexus data are opened by the shell or shown as WPF images: only web links may pass (review 2026-10-28).</summary>
public class WebLinkTests
{
    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("http://example.com/readme.md")]
    [InlineData("  https://staticdelivery.nexusmods.com/mods/x.jpg  ")]
    public void HttpOrNull_WebLink_IsKept(string link) => Assert.Equal(link.Trim(), WebLink.HttpOrNull(link));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\evil-host\share\x.png")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("file://evil-host/share/x.png")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("readme.md")]
    public void HttpOrNull_AnythingElse_IsNull(string? link) => Assert.Null(WebLink.HttpOrNull(link));

    [Fact]
    public void NexusModInfo_PictureThatIsNotAWebLink_IsDropped()
    {
        Assert.Null(new NexusModInfo(1, "Mod", "Someone", null, "1.0", @"\\evil-host\share\x.png").PictureUrl);
        Assert.Equal("https://x.example/p.jpg", new NexusModInfo(1, "Mod", "Someone", null, "1.0", "https://x.example/p.jpg").PictureUrl);
    }
}
