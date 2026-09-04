using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// Unlike CueUassetDecoderTests (which needs a real CUE4Parse-parseable .uasset it can't check
/// into source control), every dependency OpaquePakAssetPreviewService takes is a plain interface
/// — IUnrealPakService, IUassetTextureDecoder, IUassetStaticMeshDecoder — so its own wiring/
/// caching/error-handling logic is fully testable with fakes, the same approach
/// UnrealPakServiceTests already uses (FakeProcessRunner there) for its own external boundary.
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

        public Task<int> ExtractPakAsync(string unrealPakExePath, string pakFilePath, string outputDirectory, CancellationToken cancellationToken = default)
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

    [Fact]
    public async Task PreviewAssetAsync_PakFileDoesNotExist_ReturnsFailureWithoutExtracting()
    {
        var unrealPak = new FakeUnrealPakService();
        var service = new OpaquePakAssetPreviewService(unrealPak, new FakeUassetTextureDecoder(), new FakeUassetStaticMeshDecoder());

        var result = await service.PreviewAssetAsync(
            UnrealPakExePath, Path.Combine(_tempDir, "NoSuchPak.pak"), "Textures/Icon.uasset", _cacheDirectory);

        Assert.Null(result.PngBytes);
        Assert.Null(result.Mesh);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
        Assert.Equal(0, unrealPak.ExtractCallCount);
    }

    [Fact]
    public async Task PreviewAssetAsync_TextureDecodes_ReturnsPngBytesAndExtractsExactlyOnce()
    {
        var unrealPak = new FakeUnrealPakService();
        var textureDecoder = new FakeUassetTextureDecoder { Result = [1, 2, 3] };
        var meshDecoder = new FakeUassetStaticMeshDecoder();
        var service = new OpaquePakAssetPreviewService(unrealPak, textureDecoder, meshDecoder);

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Icon.uasset", _cacheDirectory);

        Assert.Equal(new byte[] { 1, 2, 3 }, result.PngBytes);
        Assert.Null(result.Mesh);
        Assert.Null(result.FailureReason);
        Assert.Equal(1, unrealPak.ExtractCallCount);
        // A successful texture decode short-circuits the mesh decode — same "texture first, mesh
        // only if that fails" order LibraryItemViewModel.DecodeCompiledAssetPreviewAsync already
        // uses for a regular EXMOD mod's own preview.
        Assert.Equal(0, meshDecoder.CallCount);
    }

    [Fact]
    public async Task PreviewAssetAsync_TextureFailsMeshDecodes_ReturnsMesh()
    {
        var unrealPak = new FakeUnrealPakService();
        var mesh = new StaticMeshGeometry([], [], [], []);
        var service = new OpaquePakAssetPreviewService(
            unrealPak, new FakeUassetTextureDecoder(), new FakeUassetStaticMeshDecoder { Result = mesh });

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Meshes/Thing.uasset", _cacheDirectory);

        Assert.Same(mesh, result.Mesh);
        Assert.Null(result.PngBytes);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task PreviewAssetAsync_NeitherDecoderSucceeds_ReturnsFailureReason()
    {
        var unrealPak = new FakeUnrealPakService();
        var service = new OpaquePakAssetPreviewService(unrealPak, new FakeUassetTextureDecoder(), new FakeUassetStaticMeshDecoder());

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Blueprints/BP_Thing.uasset", _cacheDirectory);

        Assert.Null(result.PngBytes);
        Assert.Null(result.Mesh);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public async Task PreviewAssetAsync_ExtractionThrows_ReturnsFailureReasonInsteadOfThrowing()
    {
        var unrealPak = new FakeUnrealPakService { ThrowOnExtract = new InvalidOperationException("UnrealPak.exe exited with code 1") };
        var service = new OpaquePakAssetPreviewService(unrealPak, new FakeUassetTextureDecoder(), new FakeUassetStaticMeshDecoder());

        var result = await service.PreviewAssetAsync(UnrealPakExePath, _pakFilePath, "Textures/Icon.uasset", _cacheDirectory);

        Assert.Null(result.PngBytes);
        Assert.Contains("UnrealPak.exe exited with code 1", result.FailureReason);
    }

    [Fact]
    public async Task PreviewAssetAsync_SecondCallSamePak_ReusesCacheInsteadOfReExtracting()
    {
        var unrealPak = new FakeUnrealPakService();
        var textureDecoder = new FakeUassetTextureDecoder { Result = [9] };
        var service = new OpaquePakAssetPreviewService(unrealPak, textureDecoder, new FakeUassetStaticMeshDecoder());

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
        var service = new OpaquePakAssetPreviewService(unrealPak, new FakeUassetTextureDecoder(), new FakeUassetStaticMeshDecoder());

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
        var service = new OpaquePakAssetPreviewService(unrealPak, new FakeUassetTextureDecoder(), new FakeUassetStaticMeshDecoder());

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
