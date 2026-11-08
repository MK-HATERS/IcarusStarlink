using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryItemViewModelTests
{
    private static List<string> CollectChanges(LibraryItemViewModel item)
    {
        var raised = new List<string>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        return raised;
    }

    private LibraryItemViewModel NexusRowAt(string version, string nexusHas)
    {
        var entry = MakeEntry();
        entry.Version = version;
        entry.Source = "Nexus";
        entry.NexusModId = 5;
        var item = CreateViewModel(entry: entry);
        item.LatestVersion = nexusHas;
        return item;
    }

    private static LibraryEntry NexusEntryAt(string version)
    {
        var entry = MakeEntry();
        entry.Version = version;
        entry.Source = "Nexus";
        entry.NexusModId = 5;
        return entry;
    }

    /// <summary>Regression: after the Nexus version landed in the same folder, the row kept its "Your build" pill.</summary>
    [Fact]
    public void Update_ToTheVersionNexusHas_DropsYourBuild()
    {
        var item = NexusRowAt("2.3", nexusHas: "2.2");
        Assert.True(item.IsAheadOfNexus);
        var raised = CollectChanges(item);

        item.Update(NexusEntryAt("2.2"));

        Assert.Contains(nameof(LibraryItemViewModel.IsAheadOfNexus), raised);
        Assert.False(item.IsAheadOfNexus);
    }

    /// <summary>The other way: a build newer than Nexus landing in the same folder shows "Your build".</summary>
    [Fact]
    public void Update_ToABuildNewerThanNexus_ShowsYourBuild()
    {
        var item = NexusRowAt("2.2", nexusHas: "2.2");
        Assert.False(item.IsAheadOfNexus);
        var raised = CollectChanges(item);

        item.Update(NexusEntryAt("2.4"));

        Assert.Contains(nameof(LibraryItemViewModel.IsAheadOfNexus), raised);
        Assert.True(item.IsAheadOfNexus);
    }

    /// <summary>Polls until a preview that runs in the background has settled.</summary>
    private static async Task WaitForPreviewAsync(LibraryItemViewModel item)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (item.SelectedAssetPreview is "Decoding this asset..." or "Reading this file..." && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Regression: "In short" read "Nothing differs from current game data." for a mod made only of
    /// replaced blueprints (Steadfast Structures), because it filtered the replaced files twice.
    /// </summary>
    [Fact]
    public async Task ChangesSummary_BlueprintOnlyMod_NamesTheReplacedGameFile()
    {
        ExmodFolder.Write(_modFolderPath, new ExmodPackageContents(
            new ExmodPackage
            {
                Name = "Building Height", Author = "Someone", Version = "1", Description = "", FileName = "Building_Height",
                Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }],
            },
            [new ExmodAssetEntry("BP/Building/BP_Building_Base.uasset", [1, 2, 3]), new ExmodAssetEntry("BP/Building/BP_Building_Base.uexp", [4, 5, 6])]));
        var dataFolder = Path.Combine(_modFolderPath + "_Data");
        Directory.CreateDirectory(dataFolder);
        var item = CreateViewModel(repository: new FakeLibraryRepository(_modFolderPath) { ReadsTheRealFolder = true });
        item.DataFolder = dataFolder;

        item.EnsureDetailsLoaded();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (item.ChangesSummary is null or "Working out what this mod changes…" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains("Replaces 1 game file: BP/Building/BP_Building_Base", item.ChangesSummary);
    }

    /// <summary>Regression: clicking a big .ubulk read the whole file on the UI thread only to print its size.</summary>
    [Fact]
    public async Task SelectAsset_LargeBinaryFile_ShowsItsSizeWithoutReadingIt()
    {
        Directory.CreateDirectory(_modFolderPath);
        File.WriteAllBytes(Path.Combine(_modFolderPath, "Big.ubulk"), new byte[1024 * 1024]);
        var item = CreateViewModel();

        item.SelectedAssetPath = "Big.ubulk";
        await WaitForPreviewAsync(item);

        Assert.Contains((1024 * 1024).ToString("N0", CultureInfo.CurrentCulture) + " bytes", item.SelectedAssetPreview);
    }

    /// <summary>A small text file still shows its text (read off the UI thread now).</summary>
    [Fact]
    public async Task SelectAsset_SmallTextFile_ShowsItsText()
    {
        Directory.CreateDirectory(_modFolderPath);
        File.WriteAllText(Path.Combine(_modFolderPath, "readme.txt"), "hello there");
        var item = CreateViewModel(repository: new FakeLibraryRepository(_modFolderPath)
        {
            ReadAssetContentHandler = path => File.ReadAllBytes(Path.Combine(_modFolderPath, path)),
        });

        item.SelectedAssetPath = "readme.txt";
        await WaitForPreviewAsync(item);

        Assert.Equal("hello there", item.SelectedAssetPreview);
    }

    /// <summary>A file path that climbs out of the mod's folder is never read.</summary>
    [Fact]
    public async Task SelectAsset_PathOutsideTheModFolder_IsNotRead()
    {
        Directory.CreateDirectory(_modFolderPath);
        var item = CreateViewModel();

        item.SelectedAssetPath = "../outside.txt";
        await WaitForPreviewAsync(item);

        Assert.StartsWith("(failed to read this file", item.SelectedAssetPreview);
    }

    /// <summary>A 4096-px texture preview decodes at 2048 px, not full size.</summary>
    [Fact]
    public async Task SelectAsset_HugeTexture_DecodesAtMost2048PixelsWide()
    {
        var item = CreateViewModel(new FakeUassetTextureDecoder { Result = SolidPng(4096, 16) });

        item.SelectedAssetPath = "Textures/Huge.uasset";
        await WaitForPreviewAsync(item);

        Assert.NotNull(item.SelectedAssetImage);
        Assert.Equal(2048, item.SelectedAssetImage!.PixelWidth);
    }

    /// <summary>A big text file shows its start, read without loading the whole file.</summary>
    [Fact]
    public async Task SelectAsset_LargeTextFile_ShowsItsStart()
    {
        Directory.CreateDirectory(_modFolderPath);
        File.WriteAllText(Path.Combine(_modFolderPath, "log.txt"), "first line\n" + new string('x', 400 * 1024));
        var item = CreateViewModel();

        item.SelectedAssetPath = "log.txt";
        await WaitForPreviewAsync(item);

        Assert.StartsWith("first line", item.SelectedAssetPreview);
        Assert.EndsWith("(only the first 256 KB is shown)", item.SelectedAssetPreview);
    }

    /// <summary>
    /// Regression: a row keeps its decoded texture, mesh and material after its details window
    /// closes (rows are kept for the session); ReleasePreview empties them.
    /// </summary>
    [Fact]
    public async Task ReleasePreview_EmptiesEveryPreviewSlot()
    {
        var item = CreateViewModel(new FakeUassetTextureDecoder { Result = MinimalPng });
        item.SelectedAssetPath = "Textures/Icon.uasset";
        await WaitForPreviewAsync(item);
        Assert.NotNull(item.SelectedAssetImage);

        item.ReleasePreview();

        Assert.Null(item.SelectedAssetImage);
        Assert.Null(item.SelectedAssetMesh);
        Assert.Null(item.SelectedAssetMaterialParams);
        Assert.Null(item.SelectedAssetPath);
        Assert.Null(item.SelectedAssetPreview);
    }

    /// <summary>A small picture is never scaled up.</summary>
    [Fact]
    public async Task SelectAsset_SmallTexture_KeepsItsOwnSize()
    {
        var item = CreateViewModel(new FakeUassetTextureDecoder { Result = SolidPng(64, 16) });

        item.SelectedAssetPath = "Textures/Small.uasset";
        await WaitForPreviewAsync(item);

        Assert.Equal(64, item.SelectedAssetImage!.PixelWidth);
    }

    private static byte[] SolidPng(int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Regression: in a bundle of several paks, every preview extracted the first pak, so an asset
    /// from any other pak never decoded. Each pak now has its own cache folder.
    /// </summary>
    [Fact]
    public async Task SelectAsset_InTheSecondPakOfABundle_PreviewsThatPak()
    {
        Directory.CreateDirectory(_modFolderPath);
        File.WriteAllBytes(Path.Combine(_modFolderPath, "Bags_P.pak"), [0]);
        File.WriteAllBytes(Path.Combine(_modFolderPath, "Bags_Data_P.pak"), [0]);
        var settings = new FakeSettingsService();
        settings.Current.UnrealPakExePath = "UnrealPak.exe";
        var entry = MakeEntry();
        entry.IsOpaquePak = true;
        entry.PakFileNames = ["Bags_Data_P.pak", "Bags_P.pak"];
        var pakPreview = new FakeOpaquePakAssetPreviewService();
        var item = CreateViewModel(entry: entry, settings: settings, pakPreview: pakPreview);
        await item.LoadPakContentsCommand.ExecuteAsync(null);

        item.SelectedAssetPath = "Bags_P/Item.uasset";
        await WaitForPreviewAsync(item);
        item.SelectedAssetPath = "Bags_Data_P/Item.uasset";
        await WaitForPreviewAsync(item);

        Assert.Equal(2, pakPreview.Calls.Count);
        Assert.EndsWith("Bags_P.pak", pakPreview.Calls[0].PakFilePath);
        Assert.EndsWith("Bags_Data_P.pak", pakPreview.Calls[1].PakFilePath);
        Assert.NotEqual(pakPreview.Calls[0].CacheDirectory, pakPreview.Calls[1].CacheDirectory);
    }

    // ---- Version texts ----

    /// <summary>Regression: a Nexus version written "v1.2" showed as "Update available: vv1.2", and no version as a bare "v".</summary>
    [Theory]
    [InlineData("v1.2", "v1.2")]
    [InlineData("1.2", "v1.2")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LatestVersionText_NeverDoublesTheV(string? latest, string expected)
    {
        var item = CreateViewModel();

        item.LatestVersion = latest;

        Assert.Equal(expected, item.LatestVersionText);
        Assert.Equal(expected, item.NewestOnNexusText);
    }

    /// <summary>The bound texts follow the versions the update check sets.</summary>
    [Fact]
    public void LatestVersion_WhenTheCheckSetsIt_RaisesItsTexts()
    {
        var item = CreateViewModel();
        var raised = CollectChanges(item);

        item.LatestVersion = "2.0";
        item.NewestOnNexus = "2.1";

        Assert.Contains(nameof(LibraryItemViewModel.LatestVersionText), raised);
        Assert.Contains(nameof(LibraryItemViewModel.NewestOnNexusText), raised);
        Assert.Equal("v2.1", item.NewestOnNexusText);
    }

    // ---- The mod picture from the catalog ----

    /// <summary>
    /// A Database mod with no picture of its own, whose catalog entry links a picture at
    /// http://pictures.test/mod.png, served by the given handler. The details load fetches it.
    /// </summary>
    private LibraryItemViewModel CreateViewModelWithCatalogPicture(HttpMessageHandler pictureServer)
    {
        ExmodFolder.Write(_modFolderPath, new ExmodPackageContents(
            new ExmodPackage
            {
                Name = "Test Mod", Author = "Someone", Version = "1.0", Description = "", FileName = "TestMod",
                Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }],
            },
            []));
        var entry = MakeEntry();
        entry.Source = "Database";
        entry.CatalogEntryId = "cat1";
        var catalog = new CatalogEntry(
            CatalogSource.Daedalus, "cat1", "Test Mod", "Someone", "1.0", "", "", null, "http://pictures.test/mod.png", null, null, null, []);
        return new LibraryItemViewModel(
            entry, new FakeLibraryRepository(_modFolderPath) { ReadsTheRealFolder = true }, new FakeUnrealPakService(),
            new FakeUassetTextureDecoder(), new FakeUassetStaticMeshDecoder(), new FakeUassetSkeletalMeshDecoder(),
            new FakeUassetSoundDecoder(), new FakeUassetMaterialDecoder(), new FakeOpaquePakAssetPreviewService(), new FakeSettingsService(),
            new FakeNexusApiClient(), new FakeCredentialStore(), new HttpClient(pictureServer),
            ThumbnailCacheDirectory, Path.Combine(_modFolderPath, "PakPreviewCache"),
            () => Task.FromResult<IReadOnlyList<CatalogEntry>>([catalog]),
            _ => { }, () => { });
    }

    private string ThumbnailCacheDirectory => Path.Combine(_modFolderPath, "ThumbCache");

    private void DeleteModFolder()
    {
        try
        {
            Directory.Delete(_modFolderPath, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the test's own temp folder.
        }
    }

    /// <summary>Answers every request with the given body; Content-Length is left out when the body can't seek.</summary>
    private sealed class PictureServer(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    /// <summary>An endless-looking body of zeros, as long as asked, that counts what was read and says when it's closed.</summary>
    private sealed class CountingBody(long length) : Stream
    {
        private long _served;

        public long Served => Interlocked.Read(ref _served);

        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - Served);
            Array.Clear(buffer, offset, take);
            Interlocked.Add(ref _served, take);
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    /// <summary>Regression: the mod picture was read whole, however big the server's answer, and only then found not to be a picture.</summary>
    [Fact]
    public async Task RemotePicture_HugeAnswer_IsNotReadPastTheCap()
    {
        const long tenMegabytes = 10 * 1024 * 1024;
        var body = new CountingBody(length: 3 * tenMegabytes);
        var item = CreateViewModelWithCatalogPicture(new PictureServer(body));
        try
        {
            item.EnsureDetailsLoaded();
            await Task.WhenAny(body.Closed.Task, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.True(body.Served <= tenMegabytes + 1024 * 1024, $"read {body.Served} bytes of the answer");
            Assert.Null(item.ThumbnailImage);
            Assert.False(File.Exists(Path.Combine(ThumbnailCacheDirectory, "TestMod.img")));
        }
        finally
        {
            DeleteModFolder();
        }
    }

    /// <summary>Regression: a catalog picture was decoded at full size for a 120 px box.</summary>
    [Fact]
    public async Task RemotePicture_IsDecodedAtThumbnailSizeAndCached()
    {
        var item = CreateViewModelWithCatalogPicture(new PictureServer(new MemoryStream(SolidPng(1000, 10))));
        var cached = Path.Combine(ThumbnailCacheDirectory, "TestMod.img");
        try
        {
            item.EnsureDetailsLoaded();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((item.ThumbnailImage is null || !File.Exists(cached)) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.NotNull(item.ThumbnailImage);
            Assert.Equal(240, item.ThumbnailImage!.PixelWidth);
            Assert.True(File.Exists(cached));
        }
        finally
        {
            DeleteModFolder();
        }
    }
}
