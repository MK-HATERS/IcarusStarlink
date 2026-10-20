using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class LevenshteinDistanceTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "abc", 0)]
    [InlineData("kitten", "sitting", 3)]
    public void Compute_MatchesExpectedDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, LevenshteinDistance.Compute(a, b));
    }

    [Fact]
    public void Compute_IsSymmetric()
    {
        Assert.Equal(
            LevenshteinDistance.Compute("stonepickaxe", "stonehatchet"),
            LevenshteinDistance.Compute("stonehatchet", "stonepickaxe"));
    }

    [Fact]
    public void Compute_IsCaseSensitive_ACaseOnlyDifferenceCountsOnce()
    {
        // Callers normalize case first (StaleItemFixSuggester lowercases); here 's' and 'S' differ.
        Assert.Equal(1, LevenshteinDistance.Compute("stonepickaxe", "Stonepickaxe"));
    }

    [Fact]
    public void Compute_GenuinelyDifferentDirectionalVariantsAreFarApart()
    {
        // Real Icarus building-piece naming: siblings differing only by "Left"/"Right" must not
        // collapse to a near-zero distance, or the fix suggester's ambiguity guard has nothing to
        // work with.
        Assert.Equal(4, LevenshteinDistance.Compute("concretewallangleleftwwood", "concretewallanglerightwwood"));
    }
}
