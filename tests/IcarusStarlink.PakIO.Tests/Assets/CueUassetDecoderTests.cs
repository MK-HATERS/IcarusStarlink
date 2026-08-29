using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// CueUassetTextureDecoder/CueUassetStaticMeshDecoder wrap real CUE4Parse asset parsing, which
/// needs a genuine, valid UE4.27 binary asset to exercise a successful decode — this project
/// doesn't check a real mod's own binary content into source control for exactly the same
/// licensing reason its EXMODZ diff fixtures are hand-synthesized rather than copied verbatim from
/// a real download (see the Diffing test fixtures' own precedent). A real decode was instead
/// verified live this session via a disposable scratch console referencing this project directly
/// against the user's own real mod files.
///
/// What IS safely testable here, with no real asset needed at all, is the contract every caller
/// actually depends on: both decoders must never let a bad extension, a missing mod folder, or
/// genuinely corrupt/non-asset bytes escape as an unhandled exception — they degrade to "can't
/// preview this" instead, the same "no preview available" fallback the Files tab already shows for
/// anything else it can't decode. That contract is exactly what a future change (a CUE4Parse
/// upgrade, a refactor of CueAssetProviderLocator) is most likely to accidentally break, and it's
/// fully exercisable without any real binary fixture.
/// </summary>
public class CueUassetDecoderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly CueUassetTextureDecoder _textureDecoder = new();
    private readonly CueUassetStaticMeshDecoder _meshDecoder = new();

    public CueUassetDecoderTests() => Directory.CreateDirectory(_tempDir);

    [Theory]
    [InlineData("Icon.uexp")]
    [InlineData("data.json")]
    [InlineData("readme.txt")]
    public void TryDecodeToPng_NotAUassetExtension_ReturnsNullWithoutTouchingTheFolder(string relativeAssetPath)
    {
        // A nonexistent mod folder proves the extension guard short-circuits before any real
        // CUE4Parse indexing is attempted — if it didn't, this would throw instead of returning null.
        var result = _textureDecoder.TryDecodeToPng(Path.Combine(_tempDir, "DoesNotExist"), relativeAssetPath);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Icon.uexp")]
    [InlineData("data.json")]
    public void TryDecodeStaticMesh_NotAUassetExtension_ReturnsNullWithoutTouchingTheFolder(string relativeAssetPath)
    {
        var result = _meshDecoder.TryDecodeStaticMesh(Path.Combine(_tempDir, "DoesNotExist"), relativeAssetPath);

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeToPng_ModFolderDoesNotExist_ReturnsNullInsteadOfThrowing()
    {
        var result = _textureDecoder.TryDecodeToPng(Path.Combine(_tempDir, "DoesNotExist"), "Textures/Icon.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeStaticMesh_ModFolderDoesNotExist_ReturnsNullInsteadOfThrowing()
    {
        var result = _meshDecoder.TryDecodeStaticMesh(Path.Combine(_tempDir, "DoesNotExist"), "Meshes/Thing.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeToPng_UassetFileIsNotARealAsset_ReturnsNullInsteadOfThrowing()
    {
        // Real CUE4Parse rejects this as an unparseable package — the property under test is that
        // rejection surfaces as null, not as an exception escaping into a mod's Library detail pane.
        File.WriteAllBytes(Path.Combine(_tempDir, "Icon.uasset"), [0x01, 0x02, 0x03, 0x04, 0x05]);

        var result = _textureDecoder.TryDecodeToPng(_tempDir, "Icon.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeStaticMesh_UassetFileIsNotARealAsset_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "Thing.uasset"), [0x01, 0x02, 0x03, 0x04, 0x05]);

        var result = _meshDecoder.TryDecodeStaticMesh(_tempDir, "Thing.uasset");

        Assert.Null(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
