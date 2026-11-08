using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Conflict picks across Export and Import patch: kept on a re-import, carried by a format 3 patch.</summary>
public sealed partial class MergeInstallViewModelTests
{
    /// <summary>
    /// Importing a patch over the profile in use threw away its conflict picks. Now the picks for
    /// mods still queued are kept, a pick for a mod the patch no longer queues is dropped, and the
    /// patch's own picks come in with their folders rewritten to the local mods (the patch's pick
    /// wins where both picked).
    /// </summary>
    [Fact]
    public async Task ImportPatch_UnderTheProfileInUse_KeepsConflictPicks()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB"), MakeEntry("ModC")] };
        var store = new FakeProfileStore();
        store.Save(new Profile
        {
            Name = "Coop",
            MergeQueueFolderNames = ["ModA", "ModB", "ModC"],
            ConflictWinners = new(StringComparer.OrdinalIgnoreCase)
            {
                ["D_Items.json|Axe|Weight"] = "ModA",
                ["D_Items.json|Bow|Weight"] = "ModC", // ModC isn't in the patch's queue
                ["D_Items.json|Pick|Weight"] = "ModA", // the patch picks too: its pick wins
            },
        });
        var patches = new FakePatchService
        {
            ImportResult = new PatchImportContents(
                new PatchManifest
                {
                    FormatVersion = 3,
                    ProfileName = "Coop",
                    Mods =
                    [
                        new PatchModEntry { FolderName = "Their_A", Name = "ModA", Author = "Test", Version = "1", Bundled = false },
                        new PatchModEntry { FolderName = "Their_B", Name = "ModB", Author = "Test", Version = "1", Bundled = false },
                        new PatchModEntry { FolderName = "Their_X", Name = "Not here", Author = "Test", Version = "1", Bundled = false },
                    ],
                    ConflictWinners = new()
                    {
                        ["D_Items.json|Pick|Weight"] = "Their_B",
                        ["D_Items.json|Rope|Weight"] = "Their_X", // didn't resolve: dropped
                        ["D_Items.json|Ammo|Weight"] = MergeInstallViewModel.OptionsWinnerId,
                    },
                },
                new Dictionary<string, ExmodPackageContents>()),
        };
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, patchService: patches);
        vm.SelectedProfileName = "Coop";
        _dialogs.OnPickFile = () => "patch.json";

        await vm.ImportPatchCommand.ExecuteAsync(null);
        vm.SaveWorkingState();

        var expected = new Dictionary<string, string>
        {
            ["D_Items.json|Axe|Weight"] = "ModA",
            ["D_Items.json|Pick|Weight"] = "ModB",
            ["D_Items.json|Ammo|Weight"] = MergeInstallViewModel.OptionsWinnerId,
        };
        Assert.Equal(expected.OrderBy(p => p.Key), vm.WinnersForTesting.OrderBy(p => p.Key));
        Assert.Equal(expected.OrderBy(p => p.Key), store.Load("Coop").ConflictWinners.OrderBy(p => p.Key));
        Assert.Contains("kept 1 conflict pick(s)", vm.PatchStatusMessage);
        Assert.Contains("2 conflict pick(s) from the patch", vm.PatchStatusMessage);
    }

    /// <summary>Export carries the queue's conflict picks for the mods that went in, as format 3.</summary>
    [Fact]
    public async Task ExportPatch_CarriesTheConflictPicksForExportedMods()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA")] };
        library.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", 5), out _);
        var store = new FakeProfileStore();
        store.Save(new Profile
        {
            Name = "Coop",
            MergeQueueFolderNames = ["ModA"],
            ConflictWinners = new(StringComparer.OrdinalIgnoreCase)
            {
                ["D_Items.json|Axe|Weight"] = "ModA",
                ["D_Items.json|Bow|Weight"] = "Gone", // not in the Library any more
            },
        });
        var patches = new FakePatchService();
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, patchService: patches);
        vm.SelectedProfileName = "Coop";
        _dialogs.OnPickSaveFile = () => Path.Combine(_tempRoot, "ISL-Patch-Coop.zip");

        await vm.ExportPatchCommand.ExecuteAsync(null);

        var manifest = patches.ExportedManifest!;
        Assert.Equal(3, manifest.FormatVersion);
        Assert.Equal([new KeyValuePair<string, string>("D_Items.json|Axe|Weight", "ModA")], manifest.ConflictWinners!);
    }

    /// <summary>Each winner shape gets its folder part rewritten to the local folder; built-in ids stay; a mod that didn't resolve loses its pick.</summary>
    [Fact]
    public void PatchWinners_Remap_RewritesTheFolderOfEveryShape()
    {
        var patch = new Dictionary<string, string>
        {
            ["a"] = "Their_Exmod",
            ["b"] = "Their_Pak/Model_P",
            ["c"] = MergeInstallViewModel.OptionsWinnerId,
            ["d"] = MergeInstallViewModel.ArcFissionWinnerId,
            ["e"] = "Their_Missing",
        };
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["their_exmod"] = "My Exmod", ["Their_Pak"] = "MyPak" };

        var remapped = PatchWinners.Remap(patch, map);

        Assert.Equal("My Exmod", remapped["a"]);
        Assert.Equal("MyPak/Model_P", remapped["b"]);
        Assert.Equal(MergeInstallViewModel.OptionsWinnerId, remapped["c"]);
        Assert.Equal(MergeInstallViewModel.ArcFissionWinnerId, remapped["d"]);
        Assert.False(remapped.ContainsKey("e"));
        Assert.Empty(PatchWinners.Remap(null, map));
    }
}
