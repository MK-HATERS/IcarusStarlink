namespace IcarusStarlink.Catalog.Tests;

public class CompatibilityWeekParserTests
{
    [Theory]
    [InlineData("w139", 139)]
    [InlineData("W184", 184)]
    [InlineData("215", 215)]
    [InlineData(" w42 ", 42)]
    public void Parse_RecognizedFormats_ReturnsTheWeekNumber(string input, int expected)
    {
        Assert.Equal(expected, CompatibilityWeekParser.Parse(input));
    }

    [Theory]
    [InlineData("All")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("w")]
    [InlineData("week139")]
    public void Parse_UnrecognizedFormats_ReturnsNullRatherThanThrowing(string? input)
    {
        Assert.Null(CompatibilityWeekParser.Parse(input));
    }
}
