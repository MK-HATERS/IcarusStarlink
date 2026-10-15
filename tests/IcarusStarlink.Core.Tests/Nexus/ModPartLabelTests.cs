using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.Core.Tests.Nexus;

public sealed class ModPartLabelTests
{
    /// <summary>The real WirelessPower bundle: the two paks must come out as "Icon" and "Model", never as a second and third "Wireless Power".</summary>
    [Theory]
    [InlineData("ZZ_WirelessPower_Icon_P", "WirelessPower", "Icon")]
    [InlineData("ZZ_WirelessPower_Model_P.pak", "WirelessPower", "Model")]
    [InlineData("ZZ_WirelessPower_Model_P", "Wireless Power 1.0.0", "Model")]
    [InlineData("Z_Foo_Textures_HD_P", "Foo", "Textures HD")]
    public void FromPakName_StripsOrderingPrefixPatchSuffixAndTheModsOwnName(string pakName, string modName, string expected) =>
        Assert.Equal(expected, ModPartLabel.FromPakName(pakName, modName));

    /// <summary>A pak that is nothing but the mod's name keeps that name rather than collapsing to "".</summary>
    [Fact]
    public void FromPakName_NothingLeftAfterStripping_KeepsTheStrippedName() =>
        Assert.Equal("WirelessPower", ModPartLabel.FromPakName("ZZ_WirelessPower_P", "WirelessPower"));

    [Fact]
    public void FromPakName_NoModName_KeepsEveryToken() =>
        Assert.Equal("WirelessPower Icon", ModPartLabel.FromPakName("ZZ_WirelessPower_Icon_P", null));
}
