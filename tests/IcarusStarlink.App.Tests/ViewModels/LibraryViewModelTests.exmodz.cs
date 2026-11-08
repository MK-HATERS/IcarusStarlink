using System.IO;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    // ---- Export for sharing (.EXMODZ) ----

    /// <summary>An own mod (made in Starlink) with a real folder on disk; returns the folder.</summary>
    private static string AddOwnModOnDisk(TestHarness harness, string folderName, bool madeInStarlink = true)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), folderName);
        ExmodFolder.Write(folder, new ExmodPackageContents(
            new ExmodPackage { Name = "My Mod", Author = "Me", Version = "1.0", Description = "D", FileName = folderName, Rows = [] },
            [new ExmodAssetEntry("Textures/T_Mine.uasset", [1, 2, 3])]));
        harness.AddMod(folderName, name: "My Mod", version: "1.0", madeInStarlink: madeInStarlink);
        harness.Repository.FolderPathOverrides[folderName] = folder;
        return folder;
    }

    [Fact]
    public async Task ExportModForSharing_OwnMod_WritesAnExmodzWherePicked()
    {
        var harness = new TestHarness();
        AddOwnModOnDisk(harness, "MyMod");
        var output = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "My Mod 1.0.EXMODZ");
        harness.DialogService.SaveFilePath = output;
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "MyMod");

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.Equal("My Mod 1.0.EXMODZ", harness.DialogService.LastSaveFileDefaultName);
        var read = ExmodzArchive.Read(output);
        Assert.Equal("My Mod", read.Package.Name);
        Assert.Contains(read.Assets, a => a.RelativePath == "Textures/T_Mine.uasset");
        Assert.Contains("credit", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportModForSharing_ModNotMadeInStarlink_IsRefusedAndNeverAsksWhere()
    {
        var harness = new TestHarness();
        AddOwnModOnDisk(harness, "FromNexus", madeInStarlink: false);
        harness.DialogService.SaveFilePath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "x.EXMODZ");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "FromNexus");

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.False(item.IsOwnMod);
        Assert.Null(harness.DialogService.LastSaveFileDefaultName);
        Assert.False(File.Exists(harness.DialogService.SaveFilePath));
        Assert.Contains("you made in Starlink", vm.StatusMessage);
    }

    [Fact]
    public async Task ExportModForSharing_PickerCancelled_WritesNothing()
    {
        var harness = new TestHarness();
        var folder = AddOwnModOnDisk(harness, "MyMod");
        harness.DialogService.SaveFilePath = null;
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "MyMod");
        Assert.True(item.IsOwnMod);

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(folder)!, "*.EXMODZ", SearchOption.AllDirectories));
        Assert.DoesNotContain("Exported", vm.StatusMessage ?? "");
    }

    [Fact]
    public async Task ExportModForSharing_ModFolderMissing_SaysItFailed()
    {
        var harness = new TestHarness();
        harness.AddMod("Gone", madeInStarlink: true);
        harness.DialogService.SaveFilePath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "x.EXMODZ");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Gone");

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.StartsWith("Couldn't export", vm.StatusMessage);
        Assert.False(File.Exists(harness.DialogService.SaveFilePath));
    }
}
