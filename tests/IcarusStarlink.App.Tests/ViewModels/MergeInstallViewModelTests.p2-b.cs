using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// A build while Import patch still adds mods, a mod list loaded onto a profile that couldn't be
/// loaded or into its own profile, a renamed copy of another mod in a patch, Cancel during a
/// prebuilt pak's unpack, UE4SS's own mods in a patch, and the finder's open message.
/// </summary>
public sealed partial class MergeInstallViewModelTests
{
    /// <summary>A build started while Import patch was still adding mods ran with only half the patch's mods in the Library.</summary>
    [Fact]
    public async Task Rebuild_WhileAPatchImportRuns_IsRefused()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new FakePatchModFetcher { Hold = release.Task };
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA")] };
        library.FolderPaths["ModA"] = CreateExmodFolder("ModA", BuildCraftTimeExmodJson("ModA", craftTime: 5), out _);
        var settings = new FakeSettingsService();
        settings.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var rebuild = new FakeRebuildService();
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: new FakeProfileStore(), settingsService: settings, fetcher: fetcher);
        vm.Queue.Add(library.AllEntries[0]);
        _dialogs.OnConfirm = (_, title) => title == "Get the patch's mods";
        using var patch = new PatchImportContents(
            new PatchManifest { FormatVersion = 2, ProfileName = "First", Mods = [new PatchModEntry { FolderName = "W", Name = "Wireless Power", Author = "O", Version = "1", Bundled = false, NexusModId = 361 }] },
            new Dictionary<string, ExmodPackageContents>());

        var import = vm.ImportPatchContentsAsync(patch);
        var built = await vm.RebuildAsync();
        var status = vm.StatusMessage;
        release.SetResult();
        await import;

        Assert.False(built);
        Assert.Equal(0, rebuild.RebuildCallCount);
        Assert.Contains("Import patch", status);
    }

    /// <summary>
    /// Loading a mod list while the profile shown couldn't be loaded saved the list shown (another
    /// profile's) into the broken profile's file first, then replaced it.
    /// </summary>
    [Fact]
    public void LoadModList_OnAProfileThatCantLoad_IsRefused_AndLeavesItsFileAlone()
    {
        var store = RealProfileStore();
        store.Save(new Profile { Name = "Solo", MergeQueueFolderNames = ["ModA"] });
        var coopPath = WriteBrokenCoop();
        var vm = CreateViewModel(libraryRepository: ThreeMods(), profileStore: store);
        vm.SelectedProfileName = "Solo";
        vm.SelectedProfileName = "Coop";

        vm.LoadModListFromFile(WriteModList("ModC"));
        vm.SaveWorkingState();

        Assert.Equal(BrokenCoopJson, File.ReadAllText(coopPath));
        Assert.Contains("wasn't loaded", vm.StatusMessage);
        Assert.Equal(["ModA"], vm.Queue.Select(e => e.FolderName));
    }

    /// <summary>
    /// Migrate from IMM loaded the classic list over the profile in use. It goes into its own
    /// profile now, which a second run uses again (with the list from before as its backup).
    /// </summary>
    [Fact]
    public void LoadModListIntoOwnProfile_LeavesTheProfileInUseAlone_AndReusesItsProfile()
    {
        var store = RealProfileStore();
        store.Save(new Profile { Name = "Survival", MergeQueueFolderNames = ["ModA", "ModB"] });
        var vm = CreateViewModel(libraryRepository: ThreeMods(), profileStore: store);
        vm.SelectedProfileName = "Survival";
        var list = WriteModList("ModC");

        vm.LoadModListIntoOwnProfile(list, "Classic IMM");
        vm.SaveWorkingState();

        Assert.Equal(["ModA", "ModB"], store.Load("Survival").MergeQueueFolderNames);
        Assert.Equal(["ModC"], store.Load("Classic IMM").MergeQueueFolderNames);
        Assert.Equal("Classic IMM", vm.SelectedProfileName);
        Assert.Contains("'Classic IMM'", vm.StatusMessage);

        vm.SelectedProfileName = "Survival";
        vm.LoadModListIntoOwnProfile(list, "Classic IMM");
        vm.SaveWorkingState();

        Assert.Equal(1, store.ProfileNames.Count(n => string.Equals(n, "Classic IMM", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(["ModC"], store.Load("Classic IMM").MergeQueueFolderNames);
        Assert.Equal(["ModA", "ModB"], store.Load("Survival").MergeQueueFolderNames);
        Assert.True(store.HasBackup("Classic IMM"));
    }

    /// <summary>
    /// A renamed copy was taken for the patch's mod on its author alone, so a patch bundling
    /// another mod by the same author under the same folder name replaced it.
    /// </summary>
    [Fact]
    public async Task ImportPatch_RenamedCopyOfAnotherModBySameAuthor_IsKeptBesideIt()
    {
        var mine = MakeEntry("Mine");
        (mine.DeclaredName, mine.DisplayNameOverride, mine.Name) = ("Other Stacks", "My stacks", "My stacks");
        var library = new FakeLibraryRepository { AllEntries = [mine] };
        var bundled = new ExmodPackageContents(ExmodJson.Parse(BuildCraftTimeExmodJson("Mine", 7)), []);
        var patch = new PatchImportContents(
            new PatchManifest
            {
                FormatVersion = 2, ProfileName = "S",
                Mods = [new PatchModEntry { FolderName = "Mine", Name = "Mine", Author = "Test", Version = "1", Bundled = true }],
            },
            new Dictionary<string, ExmodPackageContents> { ["Mine"] = bundled });
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = patch });
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        Assert.DoesNotContain("delete:Mine", library.Calls);
        Assert.Contains(mine, library.AllEntries);
        Assert.Equal(2, library.AllEntries.Count);
        Assert.Contains("beside", vm.PatchStatusMessage);
    }

    /// <summary>A renamed copy of the same mod (its declared name matches) is still replaced.</summary>
    [Fact]
    public async Task ImportPatch_RenamedCopyOfTheSameExmod_IsReplaced()
    {
        var mine = MakeEntry("Mine");
        (mine.DeclaredName, mine.DisplayNameOverride, mine.Name) = ("Mine", "My stacks", "My stacks");
        var library = new FakeLibraryRepository { AllEntries = [mine] };
        var bundled = new ExmodPackageContents(ExmodJson.Parse(BuildCraftTimeExmodJson("Mine", 7)), []);
        var patch = new PatchImportContents(
            new PatchManifest
            {
                FormatVersion = 2, ProfileName = "S",
                Mods = [new PatchModEntry { FolderName = "Mine", Name = "Mine", Author = "Test", Version = "1", Bundled = true }],
            },
            new Dictionary<string, ExmodPackageContents> { ["Mine"] = bundled });
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = patch });
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        Assert.Contains("delete:Mine", library.Calls);
        Assert.Single(library.AllEntries);
    }

    /// <summary>Cancel pressed while a build unpacked a prebuilt pak for the conflict picks did nothing until that unpack was done.</summary>
    [Fact]
    public async Task CancelDuringThePrebuiltPakUnpackBeforeTheBuild_ReachesTheUnpack()
    {
        var (library, settings) = TwoConflictingMods();
        var pakFolder = CreateOpaquePakFolder("SomePak");
        library.AllEntries.Insert(0, MakeEntry("SomePak", isOpaquePak: true));
        library.FolderPaths["SomePak"] = pakFolder;
        var rebuild = new FakeRebuildService { ReadKeyedBaseTablesHandler = (_, _, _) => BuildSmelterRecipeBaseTables() };
        rebuild.PrebuiltPakChanges[Path.Combine(pakFolder, "Prebuilt.pak")] =
            [new FieldChange("Crafting-D_ProcessorRecipes.json", "SmelterRecipe", "CraftTime", JsonValue.Create(3), JsonValue.Create(42), ValueSemantic.Scalar)];
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Default", MergeQueueFolderNames = ["SomePak", "ModA", "ModB"], ConflictWinners = new() { [CraftTimeWinnerKey] = "ModA" } });
        var vm = CreateViewModel(libraryRepository: library, rebuildService: rebuild, profileStore: store, settingsService: settings);
        bool? unpackSawCancel = null;
        // The page's own conflict count (started when the profile loads) unpacks with no token: it
        // fails here, so it never caches the pak's changes and the build always unpacks it itself.
        rebuild.DuringPrebuiltPakDiff = token =>
        {
            if (!token.CanBeCanceled)
            {
                throw new IOException("Only the build's own unpack runs in this test.");
            }

            vm.CancelBuildCommand.Execute(null);
            unpackSawCancel = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested();
        };

        var built = await vm.RebuildAsync();

        Assert.True(unpackSawCancel);
        Assert.False(built);
        Assert.Equal(0, rebuild.RebuildCallCount);
        Assert.False(vm.IsRebuilding);
    }

    /// <summary>A mod the installed UE4SS lists in its own mods.json (not one Starlink knows by name) went into a patch as the player's.</summary>
    [Fact]
    public void Ue4ssModsOnIn_LeavesOutAModTheInstalledModsJsonNames()
    {
        var modsFolder = Path.Combine(_tempRoot, "ue4ss", "Mods");
        foreach (var name in new[] { "NewBuiltInMod", "PlayerMod" })
        {
            Directory.CreateDirectory(Path.Combine(modsFolder, name, "Scripts"));
            File.WriteAllText(Path.Combine(modsFolder, name, "Scripts", "main.lua"), "");
            File.WriteAllText(Path.Combine(modsFolder, name, "enabled.txt"), "");
        }

        File.WriteAllText(Path.Combine(modsFolder, "mods.json"), """[{"mod_name": "NewBuiltInMod", "mod_enabled": true}]""");

        Assert.Equal(["PlayerMod"], PatchBuilder.Ue4ssModsOnIn(modsFolder).Select(m => m.Name));
    }

    /// <summary>More ▾ › Find the broken mod sent its open message on the app-wide messenger, not the page's own.</summary>
    [Fact]
    public void FindBrokenMod_SendsTheOpenMessageOnThePagesMessenger()
    {
        var vm = CreateViewModel();
        var received = 0;
        var recipient = new object();
        _messenger.Register<OpenFindBrokenModMessage>(recipient, (_, _) => received++);

        vm.FindBrokenModCommand.Execute(null);

        Assert.Equal(1, received);
        GC.KeepAlive(recipient);
    }
}
