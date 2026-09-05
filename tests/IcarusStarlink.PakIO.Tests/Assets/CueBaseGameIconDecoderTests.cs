using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// CueBaseGameIconDecoder wraps real CUE4Parse texture decoding (via IBaseGameContentProvider and
/// UassetTexturePngEncoder), which needs a genuine UTexture2D export to exercise a successful
/// decode — not constructible in this test project, same gap CueUassetDecoderTests' own doc comment
/// already explains for every other Cue*Decoder here. What IS safely testable with no real asset at
/// all: the null/blank guard never touches the provider, the "/Game/Path/Asset.Asset" → path
/// normalization this class owns is exactly right (verified through a FakeBaseGameContentProvider
/// that just records what it was asked for), and every failure mode (no match, the provider
/// throwing) degrades to null rather than escaping as an exception.
/// </summary>
public class CueBaseGameIconDecoderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryDecodeIconToPng_NullOrBlankPath_ReturnsNullWithoutTouchingTheProvider(string? gameIconPath)
    {
        var provider = new FakeBaseGameContentProvider();
        var decoder = new CueBaseGameIconDecoder(provider);

        var result = decoder.TryDecodeIconToPng(gameIconPath);

        Assert.Null(result);
        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData(
        "/Game/Assets/2DArt/UI/Items/Item_Icons/Resources/ITEM_Fibre.ITEM_Fibre",
        "Assets/2DArt/UI/Items/Item_Icons/Resources/ITEM_Fibre.uasset")]
    [InlineData(
        "/Game/Assets/2DArt/UI/Talents/Companion/T_Talent_Base_Wolf.T_Talent_Base_Wolf",
        "Assets/2DArt/UI/Talents/Companion/T_Talent_Base_Wolf.uasset")]
    // The object-name half is discarded outright, never assumed to match the package's own name —
    // this covers the (real, if unusual) case where it genuinely doesn't.
    [InlineData("/Game/Path/Package.SomeOtherObjectName", "Path/Package.uasset")]
    // No leading "/Game/" at all — CueAssetProviderLocator's own doc comment notes a mod-relative
    // path never carries it either; this decoder tolerates the same shape rather than requiring it.
    [InlineData("Path/Package.Package", "Path/Package.uasset")]
    public void TryDecodeIconToPng_StripsGamePrefixAndObjectNameSuffix_BeforeAskingTheProvider(
        string gameIconPath, string expectedAssetPath)
    {
        var provider = new FakeBaseGameContentProvider();
        var decoder = new CueBaseGameIconDecoder(provider);

        decoder.TryDecodeIconToPng(gameIconPath);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(expectedAssetPath, provider.LastAssetPath);
    }

    [Fact]
    public void TryDecodeIconToPng_ProviderFindsNoMatch_ReturnsNullInsteadOfThrowing()
    {
        var provider = new FakeBaseGameContentProvider();
        var decoder = new CueBaseGameIconDecoder(provider);

        var result = decoder.TryDecodeIconToPng("/Game/Nowhere/Nothing.Nothing");

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeIconToPng_ProviderThrows_ReturnsNullInsteadOfThrowing()
    {
        var decoder = new CueBaseGameIconDecoder(new ThrowingBaseGameContentProvider());

        var result = decoder.TryDecodeIconToPng("/Game/Assets/Icon.Icon");

        Assert.Null(result);
    }

    /// <summary>
    /// Records every assetPath it was asked to load and always reports "not found" — real enough to
    /// verify CueBaseGameIconDecoder's own path normalization without needing a real CUE4Parse
    /// export. TryLoadExport deliberately throws: CueBaseGameIconDecoder must call
    /// TryLoadExportAndProject instead (see its own doc comment on why plain TryLoadExport, then
    /// running UassetTexturePngEncoder afterward outside CueBaseGameContentProvider's lock, is a real
    /// race) — if a future change ever reverted back to that two-step shape, this fake would make
    /// every test above fail loudly instead of silently passing anyway.
    /// </summary>
    private sealed class FakeBaseGameContentProvider : IBaseGameContentProvider
    {
        public int CallCount { get; private set; }
        public string? LastAssetPath { get; private set; }

        public T? TryLoadExport<T>(string assetPath) where T : class =>
            throw new NotSupportedException(
                "CueBaseGameIconDecoder must call TryLoadExportAndProject, not TryLoadExport — see this fake's own doc comment.");

        public TResult? TryLoadExportAndProject<T, TResult>(string assetPath, Func<T, TResult> project)
            where T : class where TResult : class
        {
            CallCount++;
            LastAssetPath = assetPath;
            return null;
        }
    }

    private sealed class ThrowingBaseGameContentProvider : IBaseGameContentProvider
    {
        public T? TryLoadExport<T>(string assetPath) where T : class => throw new InvalidOperationException("Simulated provider failure.");

        public TResult? TryLoadExportAndProject<T, TResult>(string assetPath, Func<T, TResult> project)
            where T : class where TResult : class =>
            throw new InvalidOperationException("Simulated provider failure.");
    }
}
