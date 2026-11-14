using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class Ue4ssLogVersionParserTests
{
    // Real first few lines of a UE4SS.log, captured from a real, working install — not
    // synthesized.
    private static readonly string[] RealisticLogLines =
    [
        "[2026-08-17 19:30:33.9418541] Console created",
        "[2026-08-17 19:30:33.9483156] UE4SS - v3.0.1 Beta #0 - Git SHA #1c1a1497",
        "[2026-08-17 19:30:33.9483503] Timezone: America/Denver",
    ];

    [Fact]
    public void Parse_RealisticLogLines_ExtractsBareVersion()
    {
        Assert.Equal("3.0.1", Ue4ssLogVersionParser.Parse(RealisticLogLines));
    }

    [Fact]
    public void ParseCommit_RealisticLogLines_ExtractsTheBuildsCommit()
    {
        Assert.Equal("1c1a1497", Ue4ssLogVersionParser.ParseCommit(RealisticLogLines));
        Assert.Null(Ue4ssLogVersionParser.ParseCommit(["UE4SS - v3.0.1 Beta #0"]));
        Assert.Null(Ue4ssLogVersionParser.ParseCommit([]));
    }

    [Fact]
    public void Parse_VersionLineIsFirst_StillMatches()
    {
        Assert.Equal("3.0.1", Ue4ssLogVersionParser.Parse(["UE4SS - v3.0.1 Beta #0 - Git SHA #1c1a1497"]));
    }

    /// <summary>A 4.x log: with or without the "Beta #0" words, and a version written the way git names a dev build ("v4.0.0-6-gef15eba8").</summary>
    [Theory]
    [InlineData("UE4SS - v4.0.0 Beta #0 - Git SHA #30afe5f", "4.0.0", "30afe5f")]
    [InlineData("[2026-11-13 20:01:02.13] UE4SS - v4.0.0 - Git SHA #30afe5fff94a94c2313e220b2002059a080da9a0", "4.0.0", "30afe5fff94a94c2313e220b2002059a080da9a0")]
    [InlineData("UE4SS - v4.0.0-6-gef15eba8", "4.0.0", "ef15eba8")]
    [InlineData("UE4SS - v4.1.0", "4.1.0", null)]
    public void Parse_A4xLog_ReadsVersionAndCommit(string line, string version, string? commit)
    {
        Assert.Equal(version, Ue4ssLogVersionParser.Parse([line]));
        Assert.Equal(commit, Ue4ssLogVersionParser.ParseCommit([line]));
    }

    [Fact]
    public void Parse_EmptyLines_ReturnsNull()
    {
        Assert.Null(Ue4ssLogVersionParser.Parse([]));
    }

    [Fact]
    public void Parse_NoVersionMarker_ReturnsNull()
    {
        Assert.Null(Ue4ssLogVersionParser.Parse(["[2026-08-17 19:30:33.9418541] Console created", "some other line"]));
    }

    [Fact]
    public void Parse_VersionMarkerBeyondFirstTenLines_ReturnsNull()
    {
        var lines = Enumerable.Range(0, 15).Select(i => $"line {i}").ToList();
        lines[12] = "UE4SS - v9.9.9";

        Assert.Null(Ue4ssLogVersionParser.Parse(lines));
    }
}
