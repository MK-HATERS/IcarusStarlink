using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// Unlike CueUassetDecoderTests (which needs a real CUE4Parse-parseable .uasset it can't check
/// into source control), every dependency OpaquePakAssetPreviewService takes is a plain interface
/// — IUnrealPakService, IUassetTextureDecoder, IUassetStaticMeshDecoder, IUassetSkeletalMeshDecoder,
/// IUassetSoundDecoder, IUassetMaterialDecoder — so its own wiring/caching/error-handling/decode-
/// chain-order logic is fully testable with fakes, the same approach UnrealPakServiceTests already
/// uses (FakeProcessRunner there) for its own external boundary.
/// </summary>
public class OpaquePakAssetPreviewServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _pakFilePath;
    private readonly string _cacheDirectory;
    private const string UnrealPakExePath = @"C:\Fake\UnrealPak.exe";

    public OpaquePakAssetPreviewServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _pakFilePath = Path.Combine(_tempDir, "Mod_P.pak");
        File.WriteAllText(_pakFilePath, "fake pak bytes");
        _cacheDirectory = Path.Combine(_tempDir, "Cache", "Mod");
    }

    private sealed class FakeUnrealPakService : IUnrealPakService
    {
        public int ExtractCallCount { get; private set; }
        public string? LastOutputDirectory { get; private set; }
        public Exception? ThrowOnExtract { get; set; }

        public Task<int> ExtractPakAsync(
            string unrealPakExePath, string pakFilePath, string outputDirectory,
            CancellationToken cancellationToken = default, string? filter = null)
        {
            ExtractCallCount++;
            LastOutputDirectory = outputDirectory;
            if (ThrowOnExtract is { } ex)
            {
                throw ex;
            }
            return Task.FromResult(0);
        }

        public Task<UnrealPakExtractResult> ExtractDataPakAsync(
            string unrealPakExePath, string icarusContentPath, string outputDirectory,
            DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<string?> TryGetDataPakHashAsync(string icarusContentPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<int> CreatePakAsync(string unrealPakExePath, string stagingDirectory, string outputPakPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<PakVerifyResult> VerifyPakAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUassetTextureDecoder : IUassetTextureDecoder
    {
        public byte[]? Result { get; set; }
        public int CallCount { get; private set; }

        public byte[]? TryDecodeToPng(string modFolderPath, string relativeAssetPath)
        {
            CallCount++;
            return Result;
        }
    }

    private sealed class FakeUassetStaticMeshDecoder : IUassetStaticMeshDecoder
    {
        public StaticMeshGeometry? Result { get; set; }
        public int CallCount { get; private set; }

        public StaticMeshGeometry? TryDecodeStaticMesh(string modFolderPath, string relativeAssetPath)
        {
            CallCount++;
            return Result;
        }
    }

    private sealed class FakeUassetSkeletalMeshDecoder : IUassetSkeletalMeshDecoder
    {
        public StaticMeshGeometry? Result { get; set; }
        public int CallCount { get; private set; }

        public StaticMeshGeometry? TryDecodeSkeletalMesh(string modFolderPath, string relativeAssetPath)
        {
            CallCount++;
            return Result;
        }
    }

    private sealed class FakeUassetSoundDecoder : IUassetSoundDecoder
    {
        public UassetSoundAudio? Result { get; set; }
        public int CallCount { get; private set; }

        public UassetSoundAudio? TryDecodeAudio(string modFolderPath, string relativeAssetPath)
        {
            CallCount++;
            return Result;
        }
    }

    private sealed class FakeUassetMaterialDecoder : IUassetMaterialDecoder
    {
        public UassetMaterialParams? Result { get; set; }
        public int CallCount { get; private set; }

        public UassetMaterialParams? TryDecodeMaterial(string modFolderPath, string relativeAssetPath)
        {
            CallCount++;
            return Result;
        }
    }

    /// <summary>Builds a service with every decoder faked out as "not this asset type" (null) except whichever ones the caller overrides — keeps every test below to only the decoders it actually cares about instead of repeating all five constructor args every time.</summary>
    private static OpaquePakAssetPreviewService MakeService(
        IUnrealPakService unrealPakService,
        IUassetTextureDecoder? textureDecoder = null,
        IUassetStaticMeshDecoder? meshDecoder = null,
        IUassetSkeletalMeshDecoder? skeletalMeshDecoder = null,
        IUassetSoundDecoder? soundDecoder = null,
        IUassetMaterialDecoder? materialDecoder = null) =>
        new(unrealPakService,
            textureDecoder ?? new FakeUassetTextureDecoder(),
            meshDecoder ?? new FakeUassetStaticMeshDecoder(),
            skeletalMeshDecoder ?? new FakeUassetSkeletalMeshDecoder(),
            soundDecoder ?? new FakeUassetSoundDecoder(),
            materialDecoder ?? new FakeUassetMaterialDecoder());

    [Fact]
    public async Task PreviewAssetAsync_PakFileDoesNotExist_ReturnsFailureWithoutExtracting()
    {
        var unrealPak = new FakeUnrealPakService();
        var service = MakeService(unrealPak);

        var result = await service.PreviewAssetAsync(
            UnrealPakExePath, Path.Combine(_tempDir, "NoSuchPak.pak"), "Textures/Icon.uasset", _cacheDirectory);

        Assert.Null(result.PngBytes);
        Assert.Null(result.Mesh);
        Assert.Null(result.Sound);
        Assert.Null(result.Material);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.Equal(0, unrealPak.ExtractCallCount);
    }

    [Fact]
    public async Task PreviewAssetAsync_TextureDecodes_ReturnsPngBytesAndExtractsExactlyOnce()
    {
        var unrealPak = new FakeUnrealPakService();
        var textureDecoder = new FakeUassetTextureDecoder { Result = [1, 2, 3] };
        var meshDecoder = new FakeUassetStaticMeshDecoder();
        var skeletalMeshDecoder = new FakeUassetSkeletalMeshDecoder();
        var soundDecoder = new FakeUassetSoundDecoder();
        var materialDecoder = new FakeUassetMaterialDecoder();
        var service = MakeService(unrealPak, textureDecoder, meshDecoder, skeletalMeshDecoder, soundDecoder, materialDecoder);

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Icon.uasset", _cacheDirectory);

        Assert.Equal(new byte[] { 1, 2, 3 }, result.PngBytes);
        Assert.Null(result.Mesh);
        Assert.Null(result.FailureReason);
        Assert.Equal(1, unrealPak.ExtractCallCount);
        // A successful texture decode short-circuits every decoder after it — same "texture
        // first, stop at the first decoder that succeeds" order
        // LibraryItemViewModel.DecodeCompiledAsset already uses for a regular EXMOD mod's own
        // preview.
        Assert.Equal(0, meshDecoder.CallCount);
        Assert.Equal(0, skeletalMeshDecoder.CallCount);
        Assert.Equal(0, soundDecoder.CallCount);
        Assert.Equal(0, materialDecoder.CallCount);
    }

    [Fact]
    public async Task PreviewAssetAsync_TextureFailsMeshDecodes_ReturnsMesh()
    {
        var unrealPak = new FakeUnrealPakService();
        var mesh = new StaticMeshGeometry([], [], [], []);
        var service = MakeService(unrealPak, meshDecoder: new FakeUassetStaticMeshDecoder { Result = mesh });

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Meshes/Thing.uasset", _cacheDirectory);

        Assert.Same(mesh, result.Mesh);
        Assert.Null(result.PngBytes);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task PreviewAssetAsync_StaticMeshFailsSkeletalMeshDecodes_ReturnsMeshAndSkipsSoundAndMaterial()
    {
        var unrealPak = new FakeUnrealPakService();
        var mesh = new StaticMeshGeometry([], [], [], []);
        var soundDecoder = new FakeUassetSoundDecoder();
        var materialDecoder = new FakeUassetMaterialDecoder();
        var service = MakeService(
            unrealPak, skeletalMeshDecoder: new FakeUassetSkeletalMeshDecoder { Result = mesh },
            soundDecoder: soundDecoder, materialDecoder: materialDecoder);

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Meshes/Creature.uasset", _cacheDirectory);

        Assert.Same(mesh, result.Mesh);
        Assert.Null(result.PngBytes);
        Assert.Null(result.FailureReason);
        // A static mesh miss falls through to the skeletal mesh decoder, but a skeletal mesh hit
        // still short-circuits sound/material exactly like a static mesh hit would.
        Assert.Equal(0, soundDecoder.CallCount);
        Assert.Equal(0, materialDecoder.CallCount);
    }

    [Fact]
    public async Task PreviewAssetAsync_SoundDecodes_ReturnsSoundAndSkipsMaterial()
    {
        var unrealPak = new FakeUnrealPakService();
        var sound = UassetSoundAudio.Decoded([1, 2, 3, 4]);
        var materialDecoder = new FakeUassetMaterialDecoder();
        var service = MakeService(unrealPak, soundDecoder: new FakeUassetSoundDecoder { Result = sound }, materialDecoder: materialDecoder);

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Sounds/Thump.uasset", _cacheDirectory);

        Assert.Same(sound, result.Sound);
        Assert.Null(result.PngBytes);
        Assert.Null(result.Mesh);
        Assert.Null(result.FailureReason);
        Assert.Equal(0, materialDecoder.CallCount);
    }

    [Fact]
    public async Task PreviewAssetAsync_SoundIsUnsupportedFormat_StillReturnsSoundNotFailure()
    {
        // A real USoundWave that's positively identified but stored in a format this app can't
        // play back is NOT the same as "couldn't decode this at all" — see UassetSoundAudio's own
        // doc comment. FailureReason must stay null so LibraryItemViewModel shows the specific
        // unsupported-format reason, not the generic "couldn't decode this asset" message.
        var unrealPak = new FakeUnrealPakService();
        var sound = UassetSoundAudio.Unsupported("this sound is stored as OGG");
        var service = MakeService(unrealPak, soundDecoder: new FakeUassetSoundDecoder { Result = sound });

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Sounds/Thump.uasset", _cacheDirectory);

        Assert.Same(sound, result.Sound);
        Assert.Null(result.Sound!.WavBytes);
        Assert.Equal("this sound is stored as OGG", result.Sound!.UnsupportedFormatReason);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task PreviewAssetAsync_MaterialDecodes_ReturnsMaterial()
    {
        var unrealPak = new FakeUnrealPakService();
        var material = new UassetMaterialParams([], [], [], "BLEND_Opaque", "MSM_DefaultLit");
        var service = MakeService(unrealPak, materialDecoder: new FakeUassetMaterialDecoder { Result = material });

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Materials/M_Thing.uasset", _cacheDirectory);

        Assert.Same(material, result.Material);
        Assert.Null(result.PngBytes);
        Assert.Null(result.Mesh);
        Assert.Null(result.Sound);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task PreviewAssetAsync_NoDecoderSucceeds_ReturnsFailureReason()
    {
        var unrealPak = new FakeUnrealPakService();
        var service = MakeService(unrealPak);

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Blueprints/BP_Thing.uasset", _cacheDirectory);

        Assert.Null(result.PngBytes);
        Assert.Null(result.Mesh);
        Assert.Null(result.Sound);
        Assert.Null(result.Material);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public async Task PreviewAssetAsync_ExtractionThrows_ReturnsFailureReasonInsteadOfThrowing()
    {
        var unrealPak = new FakeUnrealPakService { ThrowOnExtract = new InvalidOperationException("UnrealPak.exe exited with code 1") };
        var service = MakeService(unrealPak);

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Icon.uasset", _cacheDirectory);

        Assert.Null(result.PngBytes);
        Assert.Contains("UnrealPak.exe exited with code 1", result.FailureReason);
    }

    [Fact]
    public async Task PreviewAssetAsync_SecondCallSamePak_ReusesCacheInsteadOfReExtracting()
    {
        var unrealPak = new FakeUnrealPakService();
        var textureDecoder = new FakeUassetTextureDecoder { Result = [9] };
        var service = MakeService(unrealPak, textureDecoder);

        await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Icon.uasset", _cacheDirectory);
        await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Other.uasset", _cacheDirectory);

        // The whole point of the cache: a second preview click against the SAME pak reuses the
        // already-extracted copy rather than paying UnrealPak.exe's own whole-pak -Extract again.
        Assert.Equal(1, unrealPak.ExtractCallCount);
        Assert.Equal(2, textureDecoder.CallCount);
    }

    [Fact]
    public async Task PreviewAssetAsync_PakFileChangedSinceCaching_ReExtracts()
    {
        var unrealPak = new FakeUnrealPakService();
        var service = MakeService(unrealPak);

        await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Icon.uasset", _cacheDirectory);

        // Simulates the underlying .pak having changed since the cache was populated (its own
        // size/mtime stamp no longer matches) — the cache must not be trusted forever just
        // because the cache directory happens to already exist.
        File.WriteAllText(_pakFilePath, "different fake pak bytes, a very different length now");

        await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Icon.uasset", _cacheDirectory);

        Assert.Equal(2, unrealPak.ExtractCallCount);
    }

    [Fact]
    public async Task PreviewAssetAsync_PassesPakFilePathAndCacheDirectoryToExtract()
    {
        var unrealPak = new FakeUnrealPakService();
        var service = MakeService(unrealPak);

        await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Icon.uasset", _cacheDirectory);

        Assert.Equal(_cacheDirectory, unrealPak.LastOutputDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
