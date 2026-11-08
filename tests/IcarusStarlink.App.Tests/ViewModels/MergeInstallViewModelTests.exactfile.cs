using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Import patch fetches the exact Nexus file the patch was made with, not whatever the mod's file list offers.</summary>
public sealed partial class MergeInstallViewModelTests
{
    private FakePatchService PatchNamingTwoNexusFiles() => new()
    {
        ImportResult = new PatchImportContents(
            new PatchManifest
            {
                FormatVersion = 2, ProfileName = "Server",
                Mods =
                [
                    new PatchModEntry { FolderName = "Vanilla", Name = "All In One", Author = "Deyvid", Version = "1.0.8", Bundled = false, NexusModId = 317, NexusFileId = 100 },
                    new PatchModEntry { FolderName = "Wireless", Name = "Wireless Power", Author = "Other", Version = "1", Bundled = false, NexusModId = 361 },
                ],
            },
            new Dictionary<string, ExmodPackageContents>()),
    };

    /// <summary>Regression: the patch's NexusFileId was dropped, so a mod with several files fetched another variant or version than the friend's.</summary>
    [Fact]
    public async Task ImportPatch_MissingNexusMod_FetchesThePatchsExactFile()
    {
        var fetcher = new FakePatchModFetcher();
        var vm = CreateViewModel(libraryRepository: new FakeLibraryRepository(), profileStore: new FakeProfileStore(), patchService: PatchNamingTwoNexusFiles(), fetcher: fetcher);
        _dialogs.OnPickFile = () => "patch.json";
        _dialogs.OnConfirm = (_, title) => title == "Get the patch's mods";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        Assert.Equal([317, 361], fetcher.NexusFetches);
        Assert.Equal([100, null], fetcher.NexusFileFetches);
    }

    /// <summary>A fetch that says stop (Stop waiting, no download came, the file list cancelled) leaves the rest unfetched and says where it stopped.</summary>
    [Fact]
    public async Task ImportPatch_FetchSaysStop_TheRestAreNotFetched()
    {
        var fetcher = new FakePatchModFetcher { KeepGoing = false };
        var vm = CreateViewModel(libraryRepository: new FakeLibraryRepository(), profileStore: new FakeProfileStore(), patchService: PatchNamingTwoNexusFiles(), fetcher: fetcher);
        _dialogs.OnPickFile = () => "patch.json";
        _dialogs.OnConfirm = (_, title) => title == "Get the patch's mods";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        Assert.Equal([317], fetcher.NexusFetches);
        Assert.Contains("stopped downloading at 'All In One by Deyvid (Nexus #317)'", vm.PatchStatusMessage);
        Assert.Contains("2 mod(s) missing", vm.PatchStatusMessage);
    }
}
