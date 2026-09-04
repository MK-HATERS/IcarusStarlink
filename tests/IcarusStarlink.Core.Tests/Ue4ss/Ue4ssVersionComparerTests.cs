using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class Ue4ssVersionComparerTests
{
    [Fact]
    public void Compare_InstalledBelowDeclaredMinimum_ReturnsBelowMinimum()
    {
        Assert.Equal(Ue4ssVersionCompatibility.BelowMinimum, Ue4ssVersionComparer.Compare("3.0.5", "3.0.1"));
    }

    [Fact]
    public void Compare_InstalledAboveDeclaredMinimum_ReturnsMet()
    {
        Assert.Equal(Ue4ssVersionCompatibility.Met, Ue4ssVersionComparer.Compare("3.0.1", "3.0.5"));
    }

    [Fact]
    public void Compare_EqualVersions_ReturnsMetNotAWarning()
    {
        Assert.Equal(Ue4ssVersionCompatibility.Met, Ue4ssVersionComparer.Compare("3.0.1", "3.0.1"));
    }

    // Real-world reason this matters: a plain ordinal string compare would say "3.0.10" < "3.0.9".
    [Fact]
    public void Compare_MultiDigitComponents_ComparesNumericallyNotLexicographically()
    {
        Assert.Equal(Ue4ssVersionCompatibility.Met, Ue4ssVersionComparer.Compare("3.0.9", "3.0.10"));
        Assert.Equal(Ue4ssVersionCompatibility.BelowMinimum, Ue4ssVersionComparer.Compare("3.0.10", "3.0.9"));
    }

    [Fact]
    public void Compare_NoDeclaredMinimum_ReturnsUnknown()
    {
        Assert.Equal(Ue4ssVersionCompatibility.Unknown, Ue4ssVersionComparer.Compare(null, "3.0.1"));
        Assert.Equal(Ue4ssVersionCompatibility.Unknown, Ue4ssVersionComparer.Compare("", "3.0.1"));
        Assert.Equal(Ue4ssVersionCompatibility.Unknown, Ue4ssVersionComparer.Compare("   ", "3.0.1"));
    }

    [Fact]
    public void Compare_NoInstalledVersion_ReturnsUnknown()
    {
        Assert.Equal(Ue4ssVersionCompatibility.Unknown, Ue4ssVersionComparer.Compare("3.0.1", null));
    }

    [Fact]
    public void Compare_DeclaredMinimumHasNonNumericSuffix_DegradesToUnknownRatherThanGuessing()
    {
        // e.g. a build tagged "3.0.1-beta" with no space before the tag — Ue4ssLogVersionParser's
        // own capture is a raw \S+ token, so this is a real shape InstalledVersion could carry.
        Assert.Equal(Ue4ssVersionCompatibility.Unknown, Ue4ssVersionComparer.Compare("3.0.1-beta", "3.0.5"));
        Assert.Equal(Ue4ssVersionCompatibility.Unknown, Ue4ssVersionComparer.Compare("3.0.1", "3.0.5-beta"));
    }

    [Fact]
    public void Compare_CompletelyNonNumericInput_DegradesToUnknownRatherThanThrowing()
    {
        Assert.Equal(Ue4ssVersionCompatibility.Unknown, Ue4ssVersionComparer.Compare("not-a-version", "3.0.1"));
        Assert.Equal(Ue4ssVersionCompatibility.Unknown, Ue4ssVersionComparer.Compare("3.0.1", "also not a version"));
    }

    [Fact]
    public void Compare_BareSingleComponentVersions_StillComparesCorrectly()
    {
        Assert.Equal(Ue4ssVersionCompatibility.Met, Ue4ssVersionComparer.Compare("3", "4"));
        Assert.Equal(Ue4ssVersionCompatibility.BelowMinimum, Ue4ssVersionComparer.Compare("4", "3"));
    }
}
