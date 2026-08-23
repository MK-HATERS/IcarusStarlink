namespace IcarusStarlink.Diffing.Tests;

public class StaleItemHeuristicTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(20, false)]
    public void IsLikelyStale_UsesTwoFieldCutoff(int fieldCount, bool expected)
    {
        Assert.Equal(expected, StaleItemHeuristic.IsLikelyStale(fieldCount));
    }

    [Fact]
    public void BuildNote_MentionsItemFileAndFieldCount()
    {
        var note = StaleItemHeuristic.BuildNote("Traits-D_Fuel.json", "Old_Item", fieldCount: 2);

        Assert.Contains("Old_Item", note);
        Assert.Contains("Traits-D_Fuel.json", note);
        Assert.Contains("2 field(s)", note);
    }
}
