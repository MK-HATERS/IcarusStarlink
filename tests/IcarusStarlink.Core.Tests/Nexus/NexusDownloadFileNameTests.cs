using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.Core.Tests.Nexus;

public sealed class NexusDownloadFileNameTests
{
    [Theory]
    [InlineData("ArcFissionLibrary V1.2.2 356 3 2026-10-12T13-50Z b0gYfPbb2", "ArcFissionLibrary V1.2.2")]
    [InlineData("WirelessPower 1.0.0 361 1 2026-10-12T13-49Z UAfrx4UUt", "WirelessPower 1.0.0")]
    [InlineData("Ferani_Steadfast_Structures_EXMODZ 339 1 2026-09-25T13-25Z qdbjUuqbO", "Ferani_Steadfast_Structures_EXMODZ")]
    [InlineData("Synthetic Miasma V1 1 376 1.1 2026-10-22T10-52Z AHkozS8Ru", "Synthetic Miasma V1 1")]
    public void StripNexusSuffix_RealNexusNames_KeepsOnlyTheUploadName(string input, string expected) =>
        Assert.Equal(expected, NexusDownloadFileName.StripNexusSuffix(input));

    /// <summary>Nexus's manual-download form: "&lt;name&gt;-&lt;modId&gt;-&lt;version parts&gt;-&lt;id&gt;-&lt;unix time&gt;" (a real file name).</summary>
    [Theory]
    [InlineData("Cheat S5-II and Larkwell Haulier underwears-59-2-3-25-146718-1767981001", null, "Cheat S5-II and Larkwell Haulier underwears")]
    [InlineData("Cheat S5-II and Larkwell Haulier underwears-59-2-3-25-146718-1767981001", 59, "Cheat S5-II and Larkwell Haulier underwears")]
    [InlineData("SkyUI_5_2_SE-12604-5-2SE-1572032553", null, "SkyUI_5_2_SE")]
    [InlineData("Mod-Fix-59-1-0-1767981001", null, "Mod-Fix")]
    [InlineData("Part-2-59-1-0-1767981001", 59, "Part-2")]
    public void StripNexusSuffix_ManualDownloadForm_KeepsTheHyphenatedName(string input, int? modId, string expected) =>
        Assert.Equal(expected, NexusDownloadFileName.StripNexusSuffix(input, modId));

    [Theory]
    [InlineData("Cool-Mod-2-Fix")]
    [InlineData("Mod-59-1-0-123")]
    [InlineData("Some-Mod-1-2-3-0123456789")]
    public void StripNexusSuffix_HyphenatedNameWithoutUnixTime_Unchanged(string input) =>
        Assert.Equal(input, NexusDownloadFileName.StripNexusSuffix(input));

    [Fact]
    public void StripNexusSuffix_ManualForm_OtherModsId_Unchanged() =>
        Assert.Equal("My Mod-59-1-0-1767981001", NexusDownloadFileName.StripNexusSuffix("My Mod-59-1-0-1767981001", 60));

    [Theory]
    [InlineData("MyMod")]
    [InlineData("Cool Mod 2")]
    [InlineData("nexus_361_2739")]
    public void StripNexusSuffix_NoSuffix_Unchanged(string input) =>
        Assert.Equal(input, NexusDownloadFileName.StripNexusSuffix(input));
}
