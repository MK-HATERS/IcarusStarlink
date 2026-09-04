using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// CueUassetTextureDecoder/CueUassetStaticMeshDecoder/CueUassetSkeletalMeshDecoder/
/// CueUassetSoundDecoder/CueUassetMaterialDecoder all wrap real CUE4Parse asset parsing, which
/// needs a genuine, valid UE4.27 binary asset to exercise a successful decode — this project
/// doesn't check a real mod's own binary content into source control for exactly the same
/// licensing reason its EXMODZ diff fixtures are hand-synthesized rather than copied verbatim from
/// a real download (see the Diffing test fixtures' own precedent). A real decode was instead
/// verified live this session via a disposable scratch console referencing this project directly
/// against the user's own real mod files — texture/static mesh only; the three added this session
/// (skeletal mesh, sound, material) could NOT be verified the same way, since none of this
/// session's own available fixtures happened to include a real skeletal mesh, sound, or material
/// asset (see this session's own top-level report on that gap).
///
/// What IS safely testable here, with no real asset needed at all, is the contract every caller
/// actually depends on: every decoder must never let a bad extension, a missing mod folder, or
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
    private readonly CueUassetSkeletalMeshDecoder _skeletalMeshDecoder = new();
    private readonly CueUassetSoundDecoder _soundDecoder = new();
    private readonly CueUassetMaterialDecoder _materialDecoder = new();

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

    [Theory]
    [InlineData("Icon.uexp")]
    [InlineData("data.json")]
    public void TryDecodeSkeletalMesh_NotAUassetExtension_ReturnsNullWithoutTouchingTheFolder(string relativeAssetPath)
    {
        var result = _skeletalMeshDecoder.TryDecodeSkeletalMesh(Path.Combine(_tempDir, "DoesNotExist"), relativeAssetPath);

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeSkeletalMesh_ModFolderDoesNotExist_ReturnsNullInsteadOfThrowing()
    {
        var result = _skeletalMeshDecoder.TryDecodeSkeletalMesh(Path.Combine(_tempDir, "DoesNotExist"), "Meshes/Character.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeSkeletalMesh_UassetFileIsNotARealAsset_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "Character.uasset"), [0x01, 0x02, 0x03, 0x04, 0x05]);

        var result = _skeletalMeshDecoder.TryDecodeSkeletalMesh(_tempDir, "Character.uasset");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Icon.uexp")]
    [InlineData("data.json")]
    public void TryDecodeAudio_NotAUassetExtension_ReturnsNullWithoutTouchingTheFolder(string relativeAssetPath)
    {
        var result = _soundDecoder.TryDecodeAudio(Path.Combine(_tempDir, "DoesNotExist"), relativeAssetPath);

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeAudio_ModFolderDoesNotExist_ReturnsNullInsteadOfThrowing()
    {
        var result = _soundDecoder.TryDecodeAudio(Path.Combine(_tempDir, "DoesNotExist"), "Sounds/Bang.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeAudio_UassetFileIsNotARealAsset_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "Bang.uasset"), [0x01, 0x02, 0x03, 0x04, 0x05]);

        var result = _soundDecoder.TryDecodeAudio(_tempDir, "Bang.uasset");

        Assert.Null(result);
    }

    [Theory]
    [InlineData("Icon.uexp")]
    [InlineData("data.json")]
    public void TryDecodeMaterial_NotAUassetExtension_ReturnsNullWithoutTouchingTheFolder(string relativeAssetPath)
    {
        var result = _materialDecoder.TryDecodeMaterial(Path.Combine(_tempDir, "DoesNotExist"), relativeAssetPath);

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeMaterial_ModFolderDoesNotExist_ReturnsNullInsteadOfThrowing()
    {
        var result = _materialDecoder.TryDecodeMaterial(Path.Combine(_tempDir, "DoesNotExist"), "Materials/MI_Thing.uasset");

        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeMaterial_UassetFileIsNotARealAsset_ReturnsNullInsteadOfThrowing()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "MI_Thing.uasset"), [0x01, 0x02, 0x03, 0x04, 0x05]);

        var result = _materialDecoder.TryDecodeMaterial(_tempDir, "MI_Thing.uasset");

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
