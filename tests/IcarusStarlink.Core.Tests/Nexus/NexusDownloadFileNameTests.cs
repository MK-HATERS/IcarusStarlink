using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.Core.Tests.Nexus;

public sealed class NexusDownloadFileNameTests
{
    [Theory]
    [InlineData("ArcFissionLibrary V1.2.2 356 3 2026-10-12T13-50Z b0gYfPbb2", "ArcFissionLibrary V1.2.2")]
    [InlineData("WirelessPower 1.0.0 361 1 2026-10-12T13-49Z UAfrx4UUt", "WirelessPower 1.0.0")]
    [InlineData("Ferani_Steadfast_Structures_EXMODZ 339 1 2026-09-25T13-25Z qdbjUuqbO", "Ferani_Steadfast_Structures_EXMODZ")]
    public void StripNexusSuffix_RealNexusNames_KeepsOnlyTheUploadName(string input, string expected) =>
        Assert.Equal(expected, NexusDownloadFileName.StripNexusSuffix(input));

    [Theory]
    [InlineData("MyMod")]
    [InlineData("Cool Mod 2")]
    [InlineData("nexus_361_2739")]
    public void StripNexusSuffix_NoSuffix_Unchanged(string input) =>
        Assert.Equal(input, NexusDownloadFileName.StripNexusSuffix(input));
}
