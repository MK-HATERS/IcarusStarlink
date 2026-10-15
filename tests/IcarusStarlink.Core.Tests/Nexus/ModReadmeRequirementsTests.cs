using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.Core.Tests.Nexus;

public sealed class ModReadmeRequirementsTests
{
    private const string RealReadme = """
        WirelessPower v0.9.3 - for Icarus (ArcFission)

        Adds the Wireless Power Node.

        REQUIREMENTS
        - UE4SS (experimental build): https://github.com/UE4SS-RE/RE-UE4SS/releases/tag/experimental-latest
        - ArcFission Library 1.2.0 or newer (separate download)

        INSTALL (host and every player who joins)
        1. Install the ArcFission Library first.
        """;

    [Fact]
    public void Parse_RealReadme_ReturnsTheRequirementLinesOnly()
    {
        var requirements = ModReadmeRequirements.Parse(RealReadme);

        Assert.Equal(2, requirements.Count);
        Assert.StartsWith("UE4SS (experimental build)", requirements[0]);
        Assert.Equal("ArcFission Library 1.2.0 or newer (separate download)", requirements[1]);
    }

    [Fact]
    public void Parse_NoRequirementsSection_IsEmpty() =>
        Assert.Empty(ModReadmeRequirements.Parse("Just a mod.\n\nINSTALL\n1. Copy it."));

    [Fact]
    public void Parse_MarkdownHeading_AlsoRecognized()
    {
        var requirements = ModReadmeRequirements.Parse("# Requirements\n* Some Library 2.0\n* Other Thing\n\n# Install\n...");

        Assert.Equal(["Some Library 2.0", "Other Thing"], requirements);
    }

    [Fact]
    public void ExtractName_StopsBeforeVersionAndParenthesis() =>
        Assert.Equal("ArcFission Library", ModReadmeRequirements.ExtractName("ArcFission Library 1.2.0 or newer (separate download)"));

    [Fact]
    public void IsSatisfiedBy_MatchesStagedFolderNameIgnoringSpacesAndCase()
    {
        Assert.True(ModReadmeRequirements.IsSatisfiedBy("ArcFission Library 1.2.0 or newer (separate download)", ["ArcFissionLibrary", "OtherMod"]));
        Assert.False(ModReadmeRequirements.IsSatisfiedBy("ArcFission Library 1.2.0 or newer (separate download)", ["WirelessPower"]));
    }

    [Fact]
    public void IsLoaderRequirement_OnlyForUe4ssItself()
    {
        Assert.True(ModReadmeRequirements.IsLoaderRequirement("UE4SS (experimental build): https://x"));
        Assert.False(ModReadmeRequirements.IsLoaderRequirement("ArcFission Library 1.2.0 or newer"));
    }
}
