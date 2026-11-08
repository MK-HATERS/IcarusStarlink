using System.IO;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    // ---- Export for sharing (.EXMODZ) ----

    /// <summary>One test's own temp folder: a Library folder for the mods and an Out folder to save to. Deleted on Dispose.</summary>
    private sealed class ExportScratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Export_" + Guid.NewGuid().ToString("N"));

        public string Library => Path.Combine(Root, "Library");

        public string Out(string fileName) => Path.Combine(Root, "Out", fileName);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup only.
            }
        }
    }

    /// <summary>An own mod (made in Starlink) with a real folder on disk under the scratch Library; returns the folder.</summary>
    private static string AddOwnModOnDisk(TestHarness harness, ExportScratch scratch, string folderName, bool madeInStarlink = true)
    {
        var folder = Path.Combine(scratch.Library, folderName);
        ExmodFolder.Write(folder, new ExmodPackageContents(
            new ExmodPackage { Name = "My Mod", Author = "Me", Version = "1.0", Description = "D", FileName = folderName, Rows = [] },
            [new ExmodAssetEntry("Textures/T_Mine.uasset", [1, 2, 3])]));
        harness.AddMod(folderName, name: "My Mod", version: "1.0", madeInStarlink: madeInStarlink);
        harness.Repository.FolderPathOverrides[folderName] = folder;
        return folder;
    }

    /// <summary>Every file under folder with its bytes, to check nothing in it changed.</summary>
    private static Dictionary<string, byte[]> FolderSnapshot(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task ExportModForSharing_OwnMod_WritesAnExmodzWherePicked()
    {
        using var scratch = new ExportScratch();
        var harness = new TestHarness();
        AddOwnModOnDisk(harness, scratch, "MyMod");
        var output = scratch.Out("My Mod 1.0.EXMODZ");
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
        using var scratch = new ExportScratch();
        var harness = new TestHarness();
        AddOwnModOnDisk(harness, scratch, "FromNexus", madeInStarlink: false);
        harness.DialogService.SaveFilePath = scratch.Out("x.EXMODZ");
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
        using var scratch = new ExportScratch();
        var harness = new TestHarness();
        AddOwnModOnDisk(harness, scratch, "MyMod");
        harness.DialogService.SaveFilePath = null;
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "MyMod");
        Assert.True(item.IsOwnMod);

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.Empty(Directory.GetFiles(scratch.Root, "*.EXMODZ", SearchOption.AllDirectories));
        Assert.DoesNotContain("Exported", vm.StatusMessage ?? "");
    }

    [Fact]
    public async Task ExportModForSharing_ModFolderMissing_SaysItFailed()
    {
        using var scratch = new ExportScratch();
        var harness = new TestHarness();
        harness.AddMod("Gone", madeInStarlink: true);
        harness.Repository.FolderPathOverrides["Gone"] = Path.Combine(scratch.Library, "Gone");
        harness.DialogService.SaveFilePath = scratch.Out("x.EXMODZ");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Gone");

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.StartsWith("Couldn't export", vm.StatusMessage);
        Assert.False(File.Exists(harness.DialogService.SaveFilePath));
    }

    /// <summary>
    /// A file saved in the mod's own folder would be read as one of its assets next time: it would
    /// go into the merged pak, and every later export would carry the one before it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Textures")]
    public async Task ExportModForSharing_SavedInsideTheModFolder_IsRefusedAndTheFolderIsUnchanged(string subfolder)
    {
        using var scratch = new ExportScratch();
        var harness = new TestHarness();
        var folder = AddOwnModOnDisk(harness, scratch, "MyMod");
        var before = FolderSnapshot(folder);
        harness.DialogService.SaveFilePath = Path.Combine(folder, subfolder, "My Mod 1.0.EXMODZ");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "MyMod");

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.Contains("outside the Library", vm.StatusMessage);
        var after = FolderSnapshot(folder);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));
    }

    [Fact]
    public async Task ExportModForSharing_SavedInAnotherModsFolder_IsRefused()
    {
        using var scratch = new ExportScratch();
        var harness = new TestHarness();
        AddOwnModOnDisk(harness, scratch, "MyMod");
        var other = AddOwnModOnDisk(harness, scratch, "OtherMod");
        var exmod = Path.Combine(other, "Extracted Mods", "OtherMod.EXMOD");
        var exmodBefore = File.ReadAllBytes(exmod);
        harness.DialogService.SaveFilePath = exmod;
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "MyMod");

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.Contains("outside the Library", vm.StatusMessage);
        Assert.Equal(exmodBefore, File.ReadAllBytes(exmod));
        Assert.Empty(Directory.GetFiles(scratch.Library, "*.EXMODZ", SearchOption.AllDirectories));
    }

    /// <summary>A name typed as "X.EXMOD" would otherwise save a zip under a mod file's extension.</summary>
    [Fact]
    public async Task ExportModForSharing_TypedExmodExtension_SavesAsExmodz()
    {
        using var scratch = new ExportScratch();
        var harness = new TestHarness();
        AddOwnModOnDisk(harness, scratch, "MyMod");
        harness.DialogService.SaveFilePath = scratch.Out("Shared.EXMOD");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "MyMod");

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.False(File.Exists(scratch.Out("Shared.EXMOD")));
        Assert.Equal("My Mod", ExmodzArchive.Read(scratch.Out("Shared.EXMODZ")).Package.Name);
    }

    /// <summary>The picker asked about replacing "Shared.EXMOD", not "Shared.EXMODZ", so Starlink asks before replacing that one.</summary>
    [Fact]
    public async Task ExportModForSharing_ChangedNameAlreadyThere_AsksAndKeepsItOnNo()
    {
        using var scratch = new ExportScratch();
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: false) };
        AddOwnModOnDisk(harness, scratch, "MyMod");
        Directory.CreateDirectory(Path.GetDirectoryName(scratch.Out("x"))!);
        File.WriteAllBytes(scratch.Out("Shared.EXMODZ"), [7, 7]);
        harness.DialogService.SaveFilePath = scratch.Out("Shared.EXMOD");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "MyMod");

        await vm.ExportModForSharingCommand.ExecuteAsync(item);

        Assert.Contains("Shared.EXMODZ", Assert.Single(harness.DialogService.ConfirmMessages));
        Assert.Equal([7, 7], File.ReadAllBytes(scratch.Out("Shared.EXMODZ")));
    }
}
