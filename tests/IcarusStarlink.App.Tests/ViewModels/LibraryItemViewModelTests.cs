using System.IO;
using System.Net.Http;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Covers the one part of LibraryItemViewModel's own real-asset-decode wiring that's fully testable
/// with no real .uasset binary at all (see CueUassetDecoderTests' own doc comment on why none of
/// this project keeps one checked in): does DecodeCompiledAssetPreviewAsync's own chain actually
/// call texture → static mesh → skeletal mesh → sound → material in that order, stop at the first
/// one that succeeds, and clean up the temp .wav file a decoded sound writes to disk the moment a
/// different asset is selected. Every decoder here is a fake recording its own call count/args —
/// same "fake every interface dependency, verify the real orchestration logic around them" approach
/// OpaquePakAssetPreviewServiceTests already uses for its own decoder fallback chain. No existing
/// LibraryItemViewModel/LibraryViewModel tests existed before this session to mirror instead.
/// </summary>
public sealed class LibraryItemViewModelTests : IDisposable
{
    // A real, minimal, valid 1x1 PNG — needed because the texture-success branch doesn't just check
    // "PngBytes is non-null", it decodes them into a real BitmapImage (TryDecodeImage) before
    // treating the decode as successful, the same real check DecodeCompiledAssetPreviewAsync itself
    // applies to a real CueUassetTextureDecoder result.
    private static readonly byte[] MinimalPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _modFolderPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _tempFilesToCleanUp = [];

    private static LibraryEntry MakeEntry(string folderName = "TestMod") => new()
    {
        FolderName = folderName,
        Name = "Test Mod",
        Author = "Someone",
        Version = "1.0",
        Description = "A test mod.",
        FileName = folderName,
    };

    private LibraryItemViewModel CreateViewModel(
        FakeUassetTextureDecoder? textureDecoder = null,
        FakeUassetStaticMeshDecoder? staticMeshDecoder = null,
        FakeUassetSkeletalMeshDecoder? skeletalMeshDecoder = null,
        FakeUassetSoundDecoder? soundDecoder = null,
        FakeUassetMaterialDecoder? materialDecoder = null) =>
        new(
            MakeEntry(), new FakeLibraryRepository(_modFolderPath), new FakeUnrealPakService(),
            textureDecoder ?? new FakeUassetTextureDecoder(),
            staticMeshDecoder ?? new FakeUassetStaticMeshDecoder(),
            skeletalMeshDecoder ?? new FakeUassetSkeletalMeshDecoder(),
            soundDecoder ?? new FakeUassetSoundDecoder(),
            materialDecoder ?? new FakeUassetMaterialDecoder(),
            new FakeOpaquePakAssetPreviewService(), new FakeSettingsService(),
            new FakeNexusApiClient(), new FakeCredentialStore(), new HttpClient(),
            Path.Combine(_modFolderPath, "ThumbCache"), Path.Combine(_modFolderPath, "PakPreviewCache"),
            () => Task.FromResult<IReadOnlyList<CatalogEntry>>([]),
            _ => { }, () => { });

    /// <summary>Polls until the in-flight "Decoding this asset..." placeholder is gone — the decode
    /// itself runs on a background Task.Run with no Task this test can await directly (it's kicked
    /// off fire-and-forget from the SelectedAssetPath property setter, the same way a real UI
    /// selection change triggers it), so this is the one stable, public signal that it finished,
    /// success or failure either way.</summary>
    private static async Task WaitForDecodeAsync(LibraryItemViewModel item)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (item.SelectedAssetPreview == "Decoding this asset..." && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task SelectAsset_TextureDecodes_OnlyTextureDecoderIsCalled()
    {
        var textureDecoder = new FakeUassetTextureDecoder { Result = MinimalPng };
        var staticMeshDecoder = new FakeUassetStaticMeshDecoder();
        var skeletalMeshDecoder = new FakeUassetSkeletalMeshDecoder();
        var soundDecoder = new FakeUassetSoundDecoder();
        var materialDecoder = new FakeUassetMaterialDecoder();
        var item = CreateViewModel(textureDecoder, staticMeshDecoder, skeletalMeshDecoder, soundDecoder, materialDecoder);

        item.SelectedAssetPath = "Textures/Icon.uasset";
        await WaitForDecodeAsync(item);

        Assert.NotNull(item.SelectedAssetImage);
        Assert.Equal(1, textureDecoder.CallCount);
        Assert.Equal(0, staticMeshDecoder.CallCount);
        Assert.Equal(0, skeletalMeshDecoder.CallCount);
        Assert.Equal(0, soundDecoder.CallCount);
        Assert.Equal(0, materialDecoder.CallCount);
    }

    [Fact]
    public async Task SelectAsset_TextureAndStaticMeshFailSkeletalMeshDecodes_StopsBeforeSoundAndMaterial()
    {
        var textureDecoder = new FakeUassetTextureDecoder();
        var staticMeshDecoder = new FakeUassetStaticMeshDecoder();
        var skeletalMeshDecoder = new FakeUassetSkeletalMeshDecoder { Result = new StaticMeshGeometry([], [], [], []) };
        var soundDecoder = new FakeUassetSoundDecoder();
        var materialDecoder = new FakeUassetMaterialDecoder();
        var item = CreateViewModel(textureDecoder, staticMeshDecoder, skeletalMeshDecoder, soundDecoder, materialDecoder);

        item.SelectedAssetPath = "Meshes/Character.uasset";
        await WaitForDecodeAsync(item);

        Assert.NotNull(item.SelectedAssetMesh);
        Assert.Equal(1, textureDecoder.CallCount);
        Assert.Equal(1, staticMeshDecoder.CallCount);
        Assert.Equal(1, skeletalMeshDecoder.CallCount);
        Assert.Equal(0, soundDecoder.CallCount);
        Assert.Equal(0, materialDecoder.CallCount);
    }

    [Fact]
    public async Task SelectAsset_SoundDecodesToWav_WritesPlayableTempFileAndSkipsMaterial()
    {
        var wavBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0 }; // "RIFF" + filler — content doesn't matter here, only that it round-trips to disk.
        var soundDecoder = new FakeUassetSoundDecoder { Result = UassetSoundAudio.Decoded(wavBytes) };
        var materialDecoder = new FakeUassetMaterialDecoder();
        var item = CreateViewModel(soundDecoder: soundDecoder, materialDecoder: materialDecoder);

        item.SelectedAssetPath = "Sounds/Bang.uasset";
        await WaitForDecodeAsync(item);

        Assert.NotNull(item.SelectedAssetAudioPath);
        _tempFilesToCleanUp.Add(item.SelectedAssetAudioPath!);
        Assert.True(File.Exists(item.SelectedAssetAudioPath));
        Assert.Equal(wavBytes, await File.ReadAllBytesAsync(item.SelectedAssetAudioPath!));
        Assert.Equal(0, materialDecoder.CallCount);
    }

    [Fact]
    public async Task SelectAsset_SoundIsUnsupportedFormat_ShowsReasonAndSkipsMaterial()
    {
        var soundDecoder = new FakeUassetSoundDecoder { Result = UassetSoundAudio.Unsupported("this sound is stored as OGG — only uncompressed WAV/PCM sound data can be previewed in-app right now") };
        var materialDecoder = new FakeUassetMaterialDecoder();
        var item = CreateViewModel(soundDecoder: soundDecoder, materialDecoder: materialDecoder);

        item.SelectedAssetPath = "Sounds/Music.uasset";
        await WaitForDecodeAsync(item);

        Assert.Null(item.SelectedAssetAudioPath);
        Assert.Contains("stored as OGG", item.SelectedAssetPreview);
        Assert.Equal(0, materialDecoder.CallCount);
    }

    [Fact]
    public async Task SelectAsset_EveryOtherDecoderFailsMaterialDecodes_ShowsMaterialParams()
    {
        var materialDecoder = new FakeUassetMaterialDecoder
        {
            Result = new UassetMaterialParams(
                Textures: [new MaterialTextureParam("BaseColor", MinimalPng)],
                Scalars: [new MaterialScalarParam("Roughness", 0.5f)],
                Colors: [new MaterialColorParam("Tint", 1f, 0f, 0f, 1f)],
                BlendMode: "BLEND_Opaque",
                ShadingModel: "MSM_DefaultLit"),
        };
        var item = CreateViewModel(materialDecoder: materialDecoder);

        item.SelectedAssetPath = "Materials/MI_Thing.uasset";
        await WaitForDecodeAsync(item);

        Assert.NotNull(item.SelectedAssetMaterialParams);
        Assert.Equal("BLEND_Opaque", item.SelectedAssetMaterialParams!.BlendMode);
        Assert.Equal("MSM_DefaultLit", item.SelectedAssetMaterialParams.ShadingModel);
        Assert.Single(item.SelectedAssetMaterialParams.Textures);
        Assert.Single(item.SelectedAssetMaterialParams.Scalars);
        Assert.Single(item.SelectedAssetMaterialParams.Colors);
    }

    [Fact]
    public async Task SelectAsset_EveryDecoderFails_ShowsGenericFailureMessage()
    {
        var item = CreateViewModel();

        item.SelectedAssetPath = "Blueprints/BP_Thing.uasset";
        await WaitForDecodeAsync(item);

        Assert.Null(item.SelectedAssetImage);
        Assert.Null(item.SelectedAssetMesh);
        Assert.Null(item.SelectedAssetAudioPath);
        Assert.Null(item.SelectedAssetMaterialParams);
        Assert.Contains("not a texture, mesh, sound, or material", item.SelectedAssetPreview);
    }

    [Fact]
    public async Task SelectDifferentAsset_PreviousAudioTempFileIsDeleted()
    {
        var soundDecoder = new FakeUassetSoundDecoder { Result = UassetSoundAudio.Decoded([1, 2, 3, 4]) };
        var item = CreateViewModel(soundDecoder: soundDecoder);

        item.SelectedAssetPath = "Sounds/Bang.uasset";
        await WaitForDecodeAsync(item);
        var firstTempPath = item.SelectedAssetAudioPath;
        Assert.NotNull(firstTempPath);
        Assert.True(File.Exists(firstTempPath));

        // Deselecting entirely (not just picking another asset) is what should trigger the
        // previous sound's own cleanup too — OnSelectedAssetPathChanged's null branch returns
        // before any decode even starts, so CleanupAudioTempFile is the only thing that runs here.
        item.SelectedAssetPath = null;

        Assert.Null(item.SelectedAssetAudioPath);
        Assert.False(File.Exists(firstTempPath), "the previous selection's own temp .wav file should have been deleted");
    }

    [Fact]
    public async Task SelectNewSound_PreviousAudioTempFileIsDeletedAndReplaced()
    {
        var soundDecoder = new FakeUassetSoundDecoder { Result = UassetSoundAudio.Decoded([1, 2, 3, 4]) };
        var item = CreateViewModel(soundDecoder: soundDecoder);

        item.SelectedAssetPath = "Sounds/First.uasset";
        await WaitForDecodeAsync(item);
        var firstTempPath = item.SelectedAssetAudioPath;
        Assert.NotNull(firstTempPath);

        soundDecoder.Result = UassetSoundAudio.Decoded([5, 6, 7, 8]);
        item.SelectedAssetPath = "Sounds/Second.uasset";
        await WaitForDecodeAsync(item);
        var secondTempPath = item.SelectedAssetAudioPath;
        _tempFilesToCleanUp.Add(secondTempPath!);

        Assert.NotNull(secondTempPath);
        Assert.NotEqual(firstTempPath, secondTempPath);
        Assert.False(File.Exists(firstTempPath), "the first selection's own temp .wav file should have been deleted");
        Assert.True(File.Exists(secondTempPath));
    }

    public void Dispose()
    {
        foreach (var path in _tempFilesToCleanUp)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Best-effort cleanup only — never the point of these tests.
            }
        }
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

    private sealed class FakeUnrealPakService : IUnrealPakService
    {
        public Task<int> ExtractPakAsync(string unrealPakExePath, string pakFilePath, string outputDirectory, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

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

    private sealed class FakeOpaquePakAssetPreviewService : IOpaquePakAssetPreviewService
    {
        public Task<OpaquePakAssetPreviewResult> PreviewAssetAsync(
            string unrealPakExePath, string pakFilePath, string relativeAssetPath, string cacheDirectory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests — no entry here is IsOpaquePak.");
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public bool Save() => true;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public void Save(string target, string secret) { }

        public string? Read(string target) => null;

        public void Delete(string target) { }
    }

    private sealed class FakeNexusApiClient : INexusApiClient
    {
        public Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(
            string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<NexusModInfo>> GetModListAsync(string apiKey, string gameDomain, NexusModList list, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<NexusModInfo>> SearchModsAsync(string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<NexusModPage> ListAllModsAsync(string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<IReadOnlyList<NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<NexusEndorsementStatus> SetEndorsementAsync(
            string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeLibraryRepository(string folderPath) : ILibraryRepository
    {
        public IReadOnlyList<LibraryEntry> GetAll() => throw new NotSupportedException("Not exercised by these tests.");

        public IReadOnlyList<string> UnreadableFolders => [];

        public IReadOnlyList<LibraryEntry> Search(string query) => throw new NotSupportedException("Not exercised by these tests.");

        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public void Refresh() => throw new NotSupportedException("Not exercised by these tests.");

        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes) { }

        public void MarkLocallyEdited(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public void MarkConvertedFromPrebuiltPak(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public void SetDisplayNameOverride(string folderName, string? displayName) => throw new NotSupportedException("Not exercised by these tests.");

        public void LinkToNexus(string folderName, int nexusModId) => throw new NotSupportedException("Not exercised by these tests.");

        public void SetCatalogEntry(string folderName, string catalogEntryId) => throw new NotSupportedException("Not exercised by these tests.");

        public string BackupMod(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public bool HasModBackup(string folderName) => false;

        public bool RestoreLatestModBackup(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public string? TryGetLatestModBackupPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public LibraryEntry CreateBlankMod(string name, string author, ModTemplate template = ModTemplate.Blank) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public IReadOnlyList<string> ListAssetPaths(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public IReadOnlyList<string> ListAssetPaths(string folderName, IReadOnlyList<string> precomputedFiles) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public byte[] ReadAssetContent(string folderName, string relativePath) => throw new NotSupportedException("Not exercised by these tests.");

        public string? ReadReadme(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        public string? ReadReadme(string folderName, IReadOnlyList<string> precomputedFiles) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public IReadOnlyList<string> ListFolderFiles(string folderName) => throw new NotSupportedException("Not exercised by these tests.");

        // The one real member every test in this file needs — DecodeCompiledAssetPreviewAsync
        // reads this before it ever touches a decoder, and no test here has a real folder on disk
        // for it to point at (the decoders are all fakes that never actually read from it).
        public string GetFolderPath(string folderName) => folderPath;
    }
}
