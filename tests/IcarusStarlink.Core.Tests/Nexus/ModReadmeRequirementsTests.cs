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

    /// <summary>Minimap V1.0.5's README underlines its headings — the underline used to become an empty first requirement ("Needs: ; UE4SS…").</summary>
    [Fact]
    public void Parse_UnderlinedHeading_SkipsTheUnderline()
    {
        var requirements = ModReadmeRequirements.Parse(
            "REQUIREMENTS\n------------\n- UE4SS for Icarus (Lua mods).\n- Icarus Mod Manager (for the .EXMODZ file).\n\nINSTALLATION\n------------\n1. Copy it.");

        Assert.Equal(["UE4SS for Icarus (Lua mods).", "Icarus Mod Manager (for the .EXMODZ file)."], requirements);
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

    /// <summary>The " v" version marker used to match any word starting with v: "Better Vehicles" became "Better", which any installed "Better…" mod then satisfied.</summary>
    [Theory]
    [InlineData("Better Vehicles 1.0", "Better Vehicles")]
    [InlineData("Valheim Style Building v2.1", "Valheim Style Building")]
    [InlineData("Some Mod V3", "Some Mod")]
    public void ExtractName_CutsAtAVersionNotAtAWordStartingWithV(string requirement, string expected) =>
        Assert.Equal(expected, ModReadmeRequirements.ExtractName(requirement));

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

    /// <summary>The real Minimap README: "Icarus Mod Manager (for the .EXMODZ file …)" — the app installing the mod is one.</summary>
    [Theory]
    [InlineData("Icarus Mod Manager (for the .EXMODZ file with the map icon rows).", true)]
    [InlineData("IMM", true)]
    [InlineData("Icarus Starlink 2.0 or newer", true)]
    [InlineData("Starlink", true)]
    [InlineData("ArcFission Library 1.2.0 or newer", false)]
    [InlineData("Icarus Map Icons", false)]
    public void IsModManagerRequirement_ForTheModManagerItself(string requirement, bool expected) =>
        Assert.Equal(expected, ModReadmeRequirements.IsModManagerRequirement(requirement));

    [Fact]
    public void NeedsNoDownload_BlankLoaderAndModManager()
    {
        Assert.True(ModReadmeRequirements.NeedsNoDownload("  "));
        Assert.True(ModReadmeRequirements.NeedsNoDownload("UE4SS for Icarus (Lua mods)."));
        Assert.True(ModReadmeRequirements.NeedsNoDownload("Icarus Mod Manager"));
        Assert.False(ModReadmeRequirements.NeedsNoDownload("ArcFission Library"));
    }
}
