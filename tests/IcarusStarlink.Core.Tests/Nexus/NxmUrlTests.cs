using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.Core.Tests.Nexus;

public class NxmUrlTests
{
    [Fact]
    public void Parse_PremiumStyleUrl_NoKeyOrExpires_ParsesIdsOnly()
    {
        // A premium account's own "Mod Manager Download" nxm link carries no key/expires at all —
        // confirmed against Vortex's own real NXMUrl.ts parser during Phase 8.3b planning.
        var url = NxmUrl.Parse("nxm://icarus/mods/290/files/1234");

        Assert.Equal("icarus", url.GameDomain);
        Assert.Equal(290, url.ModId);
        Assert.Equal(1234, url.FileId);
        Assert.Null(url.Key);
        Assert.Null(url.Expires);
    }

    [Fact]
    public void Parse_NonPremiumStyleUrl_WithKeyAndExpires_ParsesEverything()
    {
        var url = NxmUrl.Parse("nxm://icarus/mods/290/files/1234?key=abc123XYZ&expires=1700000000&user_id=999");

        Assert.Equal("icarus", url.GameDomain);
        Assert.Equal(290, url.ModId);
        Assert.Equal(1234, url.FileId);
        Assert.Equal("abc123XYZ", url.Key);
        Assert.Equal(1700000000, url.Expires);
    }

    [Fact]
    public void Parse_NotNxmScheme_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => NxmUrl.Parse("https://icarus/mods/290/files/1234"));
    }

    [Fact]
    public void Parse_CollectionStyleUrl_ThrowsFormatException()
    {
        // Real nxm variant this app deliberately doesn't handle — collections aren't a Phase 8.3b concern.
        Assert.Throws<FormatException>(() => NxmUrl.Parse("nxm://icarus/collections/abc123/revisions/1"));
    }

    [Fact]
    public void Parse_GarbageText_ThrowsFormatExceptionNotUriFormatException()
    {
        Assert.Throws<FormatException>(() => NxmUrl.Parse("not a url at all"));
    }
}
