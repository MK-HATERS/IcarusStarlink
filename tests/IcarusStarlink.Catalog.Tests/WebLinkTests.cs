using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.Catalog.Tests;

/// <summary>Links from catalog or Nexus data are opened by the shell or shown as WPF images: only web links may pass.</summary>
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

    [Theory]
    [InlineData("https://example.com/a.pak", "https://example.com/a.pak")]
    [InlineData("http://github.com/o/r/releases/download/v1/a.pak", "https://github.com/o/r/releases/download/v1/a.pak")]
    [InlineData("http://raw.githubusercontent.com/o/r/main/a.EXMODZ", "https://raw.githubusercontent.com/o/r/main/a.EXMODZ")]
    public void HttpsOrNull_HttpsIsKept_AndGitHubHttpIsUpgraded(string link, string expected) =>
        Assert.Equal(expected, WebLink.HttpsOrNull(link));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example.com/a.pak")]
    [InlineData("http://github.com.evil.example/o/r/a.pak")]
    [InlineData(@"\\evil-host\share\a.pak")]
    [InlineData("file:///C:/a.pak")]
    [InlineData("ftp://example.com/a.pak")]
    public void HttpsOrNull_PlainHttpOffGitHub_OrNotAWebLink_IsNull(string? link) => Assert.Null(WebLink.HttpsOrNull(link));

    [Fact]
    public void NexusModInfo_PictureThatIsNotAWebLink_IsDropped()
    {
        Assert.Null(new NexusModInfo(1, "Mod", "Someone", null, "1.0", @"\\evil-host\share\x.png").PictureUrl);
        Assert.Equal("https://x.example/p.jpg", new NexusModInfo(1, "Mod", "Someone", null, "1.0", "https://x.example/p.jpg").PictureUrl);
    }
}
