using IcarusStarlink.Core.Library;

namespace IcarusStarlink.Core.Tests.Library;

public sealed class UpdateVersionComparerTests
{
    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1.0", "1")]
    [InlineData("v1.0.0", "1")]
    [InlineData("2.0", "2.0.0")]
    [InlineData(" 1.2 ", "1.2")]
    [InlineData("Beta 3", "beta 3")]
    public void AreEquivalent_SameVersionWrittenDifferently_IsTrue(string left, string right) =>
        Assert.True(UpdateVersionComparer.AreEquivalent(left, right));

    [Theory]
    [InlineData("1.1", "1.10")]
    [InlineData("1.0", "1.0.1")]
    [InlineData("2.0-beta", "2.0")]
    [InlineData("1", "2")]
    [InlineData("", "1")]
    [InlineData(null, null)]
    [InlineData(null, "1")]
    public void AreEquivalent_DifferentOrUnknown_IsFalse(string? left, string? right) =>
        Assert.False(UpdateVersionComparer.AreEquivalent(left, right));

    /// <summary>
    /// The live report: Ferani's Steadfast Structures declares "1.0" inside its EXMOD, the Nexus
    /// page says "1", and the Nexus version was never recorded at activation (older code path).
    /// </summary>
    [Fact]
    public void HasUpdate_LatestMatchesDeclaredVersionNumerically_IsFalse() =>
        Assert.False(UpdateVersionComparer.HasUpdate(latestVersion: "1", installedVersion: "1.0", recordedSourceVersion: null));

    /// <summary>
    /// The recorded source version is authoritative even when the author's own declared string is
    /// something unrelated ("Release 2" inside the EXMOD, "2.0" on Nexus at download time).
    /// </summary>
    [Fact]
    public void HasUpdate_LatestMatchesRecordedSourceVersion_IsFalse() =>
        Assert.False(UpdateVersionComparer.HasUpdate(latestVersion: "2.0", installedVersion: "Release 2", recordedSourceVersion: "2"));

    [Fact]
    public void HasUpdate_LatestIsGenuinelyNewer_IsTrue() =>
        Assert.True(UpdateVersionComparer.HasUpdate(latestVersion: "1.1", installedVersion: "1.0", recordedSourceVersion: "1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HasUpdate_NoLatestKnown_IsFalse(string? latest) =>
        Assert.False(UpdateVersionComparer.HasUpdate(latest, "1.0", "1"));

    /// <summary>Audit finding: a timestamp-style component ("1.20261014153000") passed the numeric regex and then overflowed int.Parse, throwing out of a pure comparison.</summary>
    [Fact]
    public void AreEquivalent_ComponentTooLargeForAnInteger_NeverThrows()
    {
        Assert.True(UpdateVersionComparer.AreEquivalent("1.20261014153000", "1.20261014153000"));
        Assert.False(UpdateVersionComparer.AreEquivalent("1.99999999999999999999", "1.0"));
        Assert.True(UpdateVersionComparer.HasUpdate("1.20261014153000", "1.0", null));
    }

    [Fact]
    public void HasUpdate_NothingKnownOnTheInstalledSide_IsFalse() =>
        Assert.False(UpdateVersionComparer.HasUpdate("1.0", installedVersion: "", recordedSourceVersion: null));
}
