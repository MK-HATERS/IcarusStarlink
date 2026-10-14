using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.Core.Tests.Nexus;

public class NexusModWebUrlTests
{
    /// <summary>Audit finding: an absurdly long id in a pasted URL overflowed int.Parse and threw out of a Try-method.</summary>
    [Fact]
    public void TryParseModId_IdTooLargeForAnInteger_ReturnsFalseInsteadOfThrowing()
    {
        Assert.False(NexusModWebUrl.TryParseModId("https://www.nexusmods.com/icarus/mods/99999999999", out _));
        Assert.False(NexusModWebUrl.TryParseModId("99999999999", out _));
    }

    [Theory]
    [InlineData("289", 289)]
    [InlineData(" 289 ", 289)]
    [InlineData("https://www.nexusmods.com/icarus/mods/289", 289)]
    public void TryParseModId_BareIdOrFullUrl_BothAccepted(string text, int expectedId)
    {
        Assert.True(NexusModWebUrl.TryParseModId(text, out var modId));
        Assert.Equal(expectedId, modId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public void TryParseModId_NonPositiveOrGarbage_ReturnsFalse(string text)
    {
        Assert.False(NexusModWebUrl.TryParseModId(text, out _));
    }

    [Fact]
    public void For_BuildsTheCanonicalPageUrl()
    {
        Assert.Equal("https://www.nexusmods.com/icarus/mods/289", NexusModWebUrl.For(289));
    }
}
