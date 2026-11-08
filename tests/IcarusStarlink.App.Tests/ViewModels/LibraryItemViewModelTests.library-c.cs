using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IcarusStarlink.App.ViewModels;
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
}
