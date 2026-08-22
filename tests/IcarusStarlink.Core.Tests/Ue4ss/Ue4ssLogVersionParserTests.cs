using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class Ue4ssLogVersionParserTests
{
    // Real first few lines of a UE4SS.log, captured from the user's own real, working install
    // during Phase 8.5 planning — not synthesized.
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
    public void Parse_VersionLineIsFirst_StillMatches()
    {
        Assert.Equal("3.0.1", Ue4ssLogVersionParser.Parse(["UE4SS - v3.0.1 Beta #0 - Git SHA #1c1a1497"]));
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
