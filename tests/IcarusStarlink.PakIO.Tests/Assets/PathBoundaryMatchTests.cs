namespace IcarusStarlink.PakIO.Tests.Assets;

public class PathBoundaryMatchTests
{
    [Theory]
    [InlineData("Icon.png", "Icon.png")]
    [InlineData("ICON.PNG", "icon.png")]
    [InlineData("Weapons/Ammo.uasset", "Ammo.uasset")]
    [InlineData("JimK_Weapons_Pack_1/Pistols/Textures/Pistols_A_Diff.uasset", "Pistols/Textures/Pistols_A_Diff.uasset")]
    // An arbitrary prefix folder ahead of a genuinely nested path is exactly the tolerance this
    // method exists to provide (see CueAssetProviderLocator's own real use of it) —
    // "OldVersions/" and "data/Prebuilt/" both end in a real "/" immediately before the matched
    // suffix, so these are correct matches, not false positives to guard against.
    [InlineData("OldVersions/Weapons/Ammo.uasset", "Weapons/Ammo.uasset")]
    [InlineData("data/Prebuilt/Icons/Icon.png", "Icons/Icon.png")]
    public void EndsWithSegmentBoundary_RealPathBoundarySuffix_ReturnsTrue(string longerPath, string shorterPath) =>
        Assert.True(PathBoundaryMatch.EndsWithSegmentBoundary(longerPath, shorterPath));

    [Theory]
    [InlineData("MyIcon.png", "Icon.png")]
    [InlineData("OldVersionsWeapons/Ammo.uasset", "Weapons/Ammo.uasset")]
    public void EndsWithSegmentBoundary_SharesOnlyATrailingSubstringAcrossAFolderBoundary_ReturnsFalse(string longerPath, string shorterPath) =>
        Assert.False(PathBoundaryMatch.EndsWithSegmentBoundary(longerPath, shorterPath));

    [Fact]
    public void EndsWithSegmentBoundary_ShorterPathLongerThanLongerPath_ReturnsFalse() =>
        Assert.False(PathBoundaryMatch.EndsWithSegmentBoundary("Icon.png", "Icons/Icon.png"));

    [Fact]
    public void EndsWithSegmentBoundary_UnrelatedPaths_ReturnsFalse() =>
        Assert.False(PathBoundaryMatch.EndsWithSegmentBoundary("data/Traits/D_Armour.json", "data/Traits/D_Fuel.json"));
}
