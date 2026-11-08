using System.IO;
using IcarusStarlink.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Import and Export patch, and a classic IMM mod list loaded as the queue.</summary>
public sealed partial class MergeInstallViewModelTests
{
    private string WriteModList(params string[] names)
    {
        Directory.CreateDirectory(_tempRoot);
        var path = Path.Combine(_tempRoot, "LastMergedMods.txt");
        File.WriteAllText(path, string.Join("\n", names));
        return path;
    }

    /// <summary>
    /// Migrate from IMM loads the classic list into the profile in use. It replaced that profile's
    /// list with no backup point, so the player's own list was gone. Now the list from before is
    /// kept and Restore profile backup brings it back.
    /// </summary>
    [Fact]
    public void LoadModList_ReplacingTheList_KeepsTheListFromBeforeAsAProfileBackup()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB"), MakeEntry("ModC")] };
        var store = RealProfileStore();
        store.Save(new Profile { Name = "Survival", MergeQueueFolderNames = ["ModA", "ModB"] });
        var vm = CreateViewModel(libraryRepository: library, profileStore: store);
        vm.SelectedProfileName = "Survival";

        vm.LoadModListFromFile(WriteModList("ModC"));
        vm.SaveWorkingState();

        Assert.Equal(["ModC"], vm.Queue.Select(e => e.FolderName));
        Assert.True(store.HasBackup("Survival"));
        Assert.Contains("Profile ▾ › Restore profile backup", vm.StatusMessage);
        _dialogs.OnConfirm = (_, title) => title == "Restore profile backup";
        vm.RestoreProfileBackupCommand.Execute(null);
        Assert.Equal(["ModA", "ModB"], vm.Queue.Select(e => e.FolderName));
    }

    /// <summary>A list with nothing that's in the Library used to empty the profile's list; it's left as it was now.</summary>
    [Fact]
    public void LoadModList_WithNoModInTheLibrary_LeavesTheMergeListAlone()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("ModA"), MakeEntry("ModB")] };
        var store = RealProfileStore();
        store.Save(new Profile { Name = "Survival", MergeQueueFolderNames = ["ModA", "ModB"] });
        var vm = CreateViewModel(libraryRepository: library, profileStore: store);
        vm.SelectedProfileName = "Survival";

        vm.LoadModListFromFile(WriteModList("NotInLibrary"));
        vm.SaveWorkingState();

        Assert.Equal(["ModA", "ModB"], store.Load("Survival").MergeQueueFolderNames);
        Assert.Equal(["ModA", "ModB"], vm.Queue.Select(e => e.FolderName));
        Assert.Contains("left as it was", vm.StatusMessage);
        Assert.Contains("listed under Warnings on Merge & Install", vm.StatusMessage);
    }

    /// <summary>Import patch copied every bundled pak on the UI thread, so the window hung for as long as the copy took.</summary>
    [Fact]
    public async Task ImportPatch_CopiesTheBundledPaksOffTheUiThread()
    {
        var library = new FakeLibraryRepository();
        using var callerReturned = new ManualResetEventSlim();
        var heldTheCaller = false;
        // On the caller's thread this waits for a return that can't happen until it gives up.
        library.BeforeImportPaks = () => heldTheCaller = !callerReturned.Wait(TimeSpan.FromSeconds(15));
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = BundledPakPatch("Mine") });
        _dialogs.OnPickFile = () => "patch.zip";

        var import = vm.ImportPatchCommand.ExecuteAsync(null);
        callerReturned.Set();
        await import;

        Assert.False(heldTheCaller, "The bundled pak was copied on the thread that pressed Import patch.");
        Assert.Single(library.ImportedPakSets);
        Assert.StartsWith("Imported 'S'", vm.PatchStatusMessage);
    }

    /// <summary>
    /// The Server page's "Apply a patch to this server" hands the unpacked patch straight to the
    /// Library side: the bundled paks were copied on the thread that called it, so the window hung.
    /// </summary>
    [Fact]
    public async Task ImportPatchContents_CopiesTheBundledPaksOffTheCallersThread()
    {
        var library = new FakeLibraryRepository();
        using var callerReturned = new ManualResetEventSlim();
        var heldTheCaller = false;
        // On the caller's thread this waits for a return that can't happen until it gives up.
        library.BeforeImportPaks = () => heldTheCaller = !callerReturned.Wait(TimeSpan.FromSeconds(15));
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore());
        using var patch = BundledPakPatch("Mine");

        var import = vm.ImportPatchContentsAsync(patch);
        callerReturned.Set();
        await import;

        Assert.False(heldTheCaller, "The bundled pak was copied on the thread that applied the patch.");
        Assert.Single(library.ImportedPakSets);
    }

    /// <summary>A second import while the first still adds mods would work on the same Library at the same time: it's refused.</summary>
    [Fact]
    public async Task ImportPatch_WhileAnotherImportRuns_IsRefused()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new FakePatchModFetcher { Hold = release.Task };
        var vm = CreateViewModel(libraryRepository: new FakeLibraryRepository(), profileStore: new FakeProfileStore(), fetcher: fetcher);
        _dialogs.OnConfirm = (_, title) => title == "Get the patch's mods";
        using var first = new PatchImportContents(
            new PatchManifest { FormatVersion = 2, ProfileName = "First", Mods = [new PatchModEntry { FolderName = "W", Name = "Wireless Power", Author = "O", Version = "1", Bundled = false, NexusModId = 361 }] },
            new Dictionary<string, ExmodPackageContents>());
        using var second = new PatchImportContents(new PatchManifest { FormatVersion = 2, ProfileName = "Second" }, new Dictionary<string, ExmodPackageContents>());

        var firstImport = vm.ImportPatchContentsAsync(first);
        var refused = await vm.ImportPatchContentsAsync(second);
        release.SetResult();
        await firstImport;

        Assert.Contains("already running", refused);
        Assert.StartsWith("Imported 'First'", vm.PatchStatusMessage);
    }

    /// <summary>
    /// A mod found in the catalog only by its name and author was exported as a link with no
    /// catalog id, so a friend's import could only call it missing and never offered the download.
    /// </summary>
    [Fact]
    public async Task ExportPatch_AModFoundInTheCatalogByName_CarriesTheEntrysId()
    {
        var stacks = MakeEntry("Stacks");
        stacks.Author = "Me";
        var library = new FakeLibraryRepository { AllEntries = [stacks] };
        library.FolderPaths["Stacks"] = CreateExmodFolder("Stacks", BuildCraftTimeExmodJson("Stacks", 5), out _);
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["Stacks"] });
        var patches = new FakePatchService();
        var daedalus = new FakeDaedalusCatalogClient
        {
            Entries = [new CatalogEntry(CatalogSource.Daedalus, "d-42", "Stacks", "Me", "1", "", "", null, null, null, null, "https://x/Stacks.EXMODZ", [])],
        };
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, patchService: patches, daedalus: daedalus);
        vm.SelectedProfileName = "Coop";
        _dialogs.OnPickSaveFile = () => Path.Combine(_tempRoot, "p.json");

        await vm.ExportPatchCommand.ExecuteAsync(null);

        var mod = Assert.Single(patches.ExportedManifest!.Mods);
        Assert.Equal((false, "d-42"), (mod.Bundled, mod.CatalogEntryId));
    }

    /// <summary>
    /// A Nexus mod you edited or fixed in Starlink went into the patch as a link to the Nexus
    /// original, so friends built with the unchanged copy. Your changed copy goes in now, still
    /// naming its Nexus page, and the status says which ones.
    /// </summary>
    [Fact]
    public async Task ExportPatch_AModYouEditedOrFixed_GoesInWithYourChanges_AndKeepsItsNexusLink()
    {
        var edited = MakeEntry("Edited");
        edited.NexusModId = 361;
        edited.IsLocallyEdited = true;
        var fixedHere = MakeEntry("Fixed");
        fixedHere.NexusModId = 362;
        fixedHere.FixedByIslAtUtc = DateTimeOffset.UtcNow;
        var library = new FakeLibraryRepository { AllEntries = [edited, fixedHere] };
        library.FolderPaths["Edited"] = CreateExmodFolder("Edited", BuildCraftTimeExmodJson("Edited", 5), out _);
        library.FolderPaths["Fixed"] = CreateExmodFolder("Fixed", BuildCraftTimeExmodJson("Fixed", 6), out _);
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["Edited", "Fixed"] });
        var patches = new FakePatchService();
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, patchService: patches);
        vm.SelectedProfileName = "Coop";
        _dialogs.OnPickSaveFile = () => Path.Combine(_tempRoot, "p.zip");

        await vm.ExportPatchCommand.ExecuteAsync(null);

        var mods = patches.ExportedManifest!.Mods;
        Assert.All(mods, m => Assert.True(m.Bundled, m.FolderName));
        Assert.Equal([361, 362], mods.Select(m => m.NexusModId));
        Assert.Equal(["Edited", "Fixed"], patches.ExportedBundle!.Exmods.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("changed in Starlink", vm.PatchStatusMessage);
        Assert.Contains("Edited, Fixed", vm.PatchStatusMessage);
    }

    /// <summary>
    /// A friend's patch bundling a different mod under a folder name you already use replaced
    /// your mod (and dropped its Nexus link). Only the same mod is replaced; another one is added beside it.
    /// </summary>
    [Fact]
    public async Task ImportPatch_AnotherAuthorsModUnderYourFolderName_IsAddedBesideYours()
    {
        var mine = MakeEntry("Stacks", isOpaquePak: true);
        mine.Author = "A";
        mine.NexusModId = 123;
        var library = new FakeLibraryRepository { AllEntries = [mine] };
        var patch = BundledPakPatch("Stacks");
        patch.Manifest.Mods[0].Author = "Friend";
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = patch });
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        Assert.DoesNotContain("delete:Stacks", library.Calls);
        Assert.Contains(mine, library.AllEntries);
        Assert.Equal(123, mine.NexusModId);
        Assert.Single(library.ImportedPakSets);
        Assert.DoesNotContain("was replaced", vm.PatchStatusMessage);
        Assert.Contains("beside", vm.PatchStatusMessage);
    }

    /// <summary>
    /// Replacing your copy of the same mod dropped its pin, favourite, notes and Nexus link, and the
    /// status promised Restore latest backup brings "yours" back, which brings back only the files.
    /// </summary>
    [Fact]
    public async Task ImportPatch_ReplacingTheSameMod_KeepsYourPinFavouriteNotesAndNexusLink()
    {
        var mine = MakeEntry("Mine", isOpaquePak: true);
        (mine.IsPinned, mine.IsFavorite, mine.Notes, mine.NexusModId, mine.Source) = (true, true, "keep this", 123, "Nexus");
        var library = new FakeLibraryRepository { AllEntries = [mine] };
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = BundledPakPatch("Mine") });
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        var copy = Assert.Single(library.AllEntries);
        Assert.NotSame(mine, copy);
        Assert.Equal((true, true, "keep this", 123), (copy.IsPinned, copy.IsFavorite, copy.Notes, copy.NexusModId));
        Assert.DoesNotContain("brings yours back", vm.PatchStatusMessage);
        Assert.Contains("brings your files back", vm.PatchStatusMessage);
    }

    /// <summary>
    /// Your Nexus copy of the same mod, renamed in the Library, wasn't seen as the same mod (the
    /// rename was compared with the patch's name), so the patch's copy went in as a second copy.
    /// </summary>
    [Fact]
    public async Task ImportPatch_YourRenamedCopyOfTheSameMod_IsReplaced_NotDuplicated()
    {
        var mine = MakeEntry("Mine", isOpaquePak: true);
        (mine.Source, mine.NexusModId, mine.DisplayNameOverride, mine.Name) = ("Nexus", 123, "My stacks", "My stacks");
        var library = new FakeLibraryRepository { AllEntries = [mine] };
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = BundledPakPatch("Mine") });
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        var copy = Assert.Single(library.AllEntries);
        Assert.NotSame(mine, copy);
        Assert.Equal(("My stacks", 123), (copy.DisplayNameOverride, copy.NexusModId));
        Assert.DoesNotContain("beside", vm.PatchStatusMessage);
    }

    /// <summary>
    /// A Nexus pak mod you edited whose pak files aren't on disk was left out of the patch as having
    /// "no source", though it has one. It goes in as a link to its source, as it did before.
    /// </summary>
    [Fact]
    public async Task ExportPatch_AnEditedSourcedPakModWithNoPakFiles_GoesInAsALinkToItsSource()
    {
        var edited = MakeEntry("Edited", isOpaquePak: true);
        (edited.NexusModId, edited.IsLocallyEdited) = (361, true);
        var library = new FakeLibraryRepository { AllEntries = [edited] };
        library.FolderPaths["Edited"] = Directory.CreateDirectory(Path.Combine(_tempRoot, "Mods", "Edited")).FullName;
        var store = new FakeProfileStore();
        store.Save(new Profile { Name = "Coop", MergeQueueFolderNames = ["Edited"] });
        var patches = new FakePatchService();
        var vm = CreateViewModel(libraryRepository: library, profileStore: store, patchService: patches);
        vm.SelectedProfileName = "Coop";
        _dialogs.OnPickSaveFile = () => Path.Combine(_tempRoot, "p.zip");

        await vm.ExportPatchCommand.ExecuteAsync(null);

        Assert.True(patches.ExportedManifest is not null, vm.PatchStatusMessage);
        var mod = Assert.Single(patches.ExportedManifest.Mods);
        Assert.Equal((false, 361), (mod.Bundled, mod.NexusModId));
        Assert.DoesNotContain("changed in Starlink", vm.PatchStatusMessage);
    }

    /// <summary>A copy an earlier import of the patch made, renamed by the player: the update keeps the name it was given.</summary>
    [Fact]
    public async Task ImportPatch_ReplacingACopyAnEarlierPatchMade_KeepsYourRename()
    {
        var earlier = MakeEntry("My_Pak", isOpaquePak: true);
        (earlier.Author, earlier.Source, earlier.DisplayNameOverride, earlier.Name) = ("?", "Patch", "Base pak", "Base pak");
        var library = new FakeLibraryRepository { AllEntries = [earlier] };
        var patch = BundledPakPatch("My_Pak");
        (patch.Manifest.Mods[0].Name, patch.Manifest.Mods[0].Author) = ("My Pak", "Me");
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = patch });
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        var copy = Assert.Single(library.AllEntries);
        Assert.NotSame(earlier, copy);
        Assert.Equal("Base pak", copy.DisplayNameOverride);
    }

    /// <summary>Replacing a mod went around the Library's remover, so the old copy's pak preview stayed and showed for the patch's copy.</summary>
    [Fact]
    public async Task ImportPatch_ReplacingAMod_ClearsTheOldCopysPakPreview()
    {
        var library = new FakeLibraryRepository { AllEntries = [MakeEntry("Mine", isOpaquePak: true)] };
        var previews = Path.Combine(_tempRoot, "PakPreviews");
        Directory.CreateDirectory(Path.Combine(previews, "Mine"));
        File.WriteAllText(Path.Combine(previews, "Mine", "D_ItemsStatic.json"), "{}");
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = BundledPakPatch("Mine") });
        vm.ModRemover = new IcarusStarlink.App.Services.LibraryModRemover(library, pakPreviewCacheDirectory: previews);
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        Assert.Equal(["backup:Mine", "delete:Mine"], library.Calls);
        Assert.False(Directory.Exists(Path.Combine(previews, "Mine")));
    }

    /// <summary>A copy you edited in Starlink was replaced by the patch's without a word. It's asked first; No keeps yours and the profile uses it.</summary>
    [Fact]
    public async Task ImportPatch_ReplacingACopyYouEdited_AsksFirst_AndNoKeepsYours()
    {
        var mine = MakeEntry("Mine");
        mine.IsLocallyEdited = true;
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
        var asked = new List<string>();
        _dialogs.OnConfirm = (message, title) =>
        {
            asked.Add(title);
            return title == "Replace an edited mod?" ? false : throw new InvalidOperationException(title);
        };

        await vm.ImportPatchCommand.ExecuteAsync(null);

        Assert.Equal(["Replace an edited mod?"], asked);
        Assert.Empty(library.Calls);
        Assert.Same(mine, Assert.Single(library.AllEntries));
        Assert.Equal(["Mine"], vm.Queue.Select(e => e.FolderName));
        Assert.Contains("kept your edited copy of 'Mine'", vm.PatchStatusMessage);
    }

    /// <summary>
    /// A bundled pak mod whose name isn't its folder name was imported under the name, so the next
    /// import of an updated patch didn't find it and added another full copy every time.
    /// </summary>
    [Fact]
    public async Task ImportPatch_Twice_ReplacesTheSameBundledPakFolder()
    {
        var library = new FakeLibraryRepository();
        var patch = BundledPakPatch("My_Pak");
        (patch.Manifest.Mods[0].Name, patch.Manifest.Mods[0].Author) = ("My Pak", "Me");
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = patch });
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);
        await vm.ImportPatchCommand.ExecuteAsync(null);

        var copy = Assert.Single(library.AllEntries, e => e.IsOpaquePak);
        Assert.Equal("My_Pak", copy.FolderName);
        Assert.Equal(["backup:My_Pak", "delete:My_Pak"], library.Calls);
    }

    /// <summary>
    /// When the patch's copy failed to import and the player's own copy couldn't be put back, only
    /// the log said so: the status read just "Import failed: disk full" while the mod was gone from the Library.
    /// </summary>
    [Fact]
    public async Task ImportPatch_FailedImport_AndYourCopyCantBePutBack_SaysWhereItIsSaved()
    {
        var library = new FakeLibraryRepository
        {
            AllEntries = [MakeEntry("Mine", isOpaquePak: true)], ImportPaksException = new IOException("disk full"), RestoreSucceeds = false,
        };
        var vm = CreateViewModel(libraryRepository: library, profileStore: new FakeProfileStore(), patchService: new FakePatchService { ImportResult = BundledPakPatch("Mine") });
        _dialogs.OnPickFile = () => "patch.zip";

        await vm.ImportPatchCommand.ExecuteAsync(null);

        Assert.StartsWith("Import failed: disk full", vm.PatchStatusMessage);
        Assert.Contains("couldn't be put back", vm.PatchStatusMessage);
        Assert.Contains("Mine_backup", vm.PatchStatusMessage);
    }
}
