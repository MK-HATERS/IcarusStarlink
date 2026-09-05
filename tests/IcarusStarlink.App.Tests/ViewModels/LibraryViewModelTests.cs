using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// LibraryViewModel is the largest ViewModel in the app (~2400 lines) and the primary way a user
/// imports/organizes/deletes/renames real mod folders on disk — this is its first real test
/// coverage. Given its size, this deliberately does NOT try to cover every command; it targets the
/// highest-risk, previously completely-unverified paths: import classification's own ViewModel-level
/// entry point (deep branch coverage of the classifier itself lives in
/// Services/ExtractedModClassifierTests.cs), the confirm-then-delete flow including this session's
/// two new purge calls, the rename write-through, Reload/Refresh rebuilding from the repository's
/// current state (including an externally-driven LibraryChangedMessage), and GetUpdateAsync — picked
/// as the single riskiest untested method still on this file (see the comment on its own tests below
/// for why). Every dependency is a small hand-written fake, one per interface, matching the
/// established convention in SavesViewModelTests/LibraryItemViewModelTests/ConflictRowViewModelTests
/// rather than a mocking framework or a real DI container.
/// </summary>
public sealed class LibraryViewModelTests
{
    // ---------------------------------------------------------------------------------------
    // Import classification (ViewModel-level entry point — ImportPaths/ImportOnePath). Deep
    // branch coverage of the actual classification decision lives in
    // Services/ExtractedModClassifierTests.cs; these confirm LibraryViewModel wires the three
    // real entry shapes (a folder, a bare .pak, an archive) to the right place.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ImportPaths_RealFolder_ImportsThroughRepositoryDirectlyWithNoArchiveExtraction()
    {
        var harness = new TestHarness();
        var folderPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folderPath);
        try
        {
            harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => new LibraryEntry
            {
                FolderName = "ImportedMod", Name = "Imported Mod", Author = "Someone", Version = "1.0", Description = "D", FileName = "ImportedMod",
            };
            var vm = harness.Build();

            await vm.ImportPaths([folderPath]);

            Assert.Single(harness.Repository.ImportCalls);
            Assert.Equal(folderPath, harness.Repository.ImportCalls[0].SourcePath);
            Assert.Equal(0, harness.PrebuiltPakImporter.ImportCallCount);
            Assert.Equal(1, vm.ModCount);
            Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ImportedMod");
            Assert.Contains("Imported 1 mod", vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(folderPath, recursive: true);
        }
    }

    [Fact]
    public async Task ImportPaths_BarePakFile_ImportsThroughPrebuiltPakImporterNotTheFolderOrArchivePath()
    {
        var harness = new TestHarness();
        var pakPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.pak");
        File.WriteAllBytes(pakPath, [1, 2, 3, 4]);
        try
        {
            harness.PrebuiltPakImporter.Result = new LibraryEntry
            {
                FolderName = "BarePak", Name = "Bare Pak Mod", Author = "Unknown", Version = "1.0", Description = "", FileName = "BarePak", IsOpaquePak = true,
            };
            var vm = harness.Build();

            await vm.ImportPaths([pakPath]);

            Assert.Equal(1, harness.PrebuiltPakImporter.ImportCallCount);
            Assert.Equal(pakPath, harness.PrebuiltPakImporter.LastPakFilePath);
            Assert.Empty(harness.Repository.ImportCalls);
            Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "BarePak");
        }
        finally
        {
            File.Delete(pakPath);
        }
    }

    [Fact]
    public async Task ImportPaths_ArchiveContainingAnExmod_ExtractsClassifiesAndImportsAsALibraryMod()
    {
        var harness = new TestHarness();
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.EXMODZ");
        using (var archive = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Extracted Mods/ZippedMod.EXMOD");
            using var stream = entry.Open();
            var json = """
                {
                    "name": "Zipped Mod", "author": "Someone", "version": "1.0", "description": "D",
                    "fileName": "ZippedMod", "Rows": []
                }
                """u8.ToArray();
            stream.Write(json);
        }

        try
        {
            harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => new LibraryEntry
            {
                FolderName = "ZippedMod", Name = "Zipped Mod", Author = "Someone", Version = "1.0", Description = "D", FileName = "ZippedMod",
            };
            var vm = harness.Build();

            await vm.ImportPaths([zipPath]);

            Assert.Single(harness.Repository.ImportCalls);
            Assert.Equal(0, harness.PrebuiltPakImporter.ImportCallCount);
            Assert.Equal(0, harness.Ue4ssModRepository.ImportFromFolderCallCount);
            Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ZippedMod");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportPaths_ArchiveWithNoExmodOrPak_ClassifiesAsAUe4ssModImport()
    {
        var harness = new TestHarness();
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.zip");
        using (var archive = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Scripts/main.lua");
            using var stream = entry.Open();
            stream.Write("print('hello')"u8.ToArray());
        }

        try
        {
            harness.Ue4ssModRepository.Result = "CoolMod_1";
            var vm = harness.Build();

            await vm.ImportPaths([zipPath]);

            Assert.Equal(1, harness.Ue4ssModRepository.ImportFromFolderCallCount);
            Assert.Empty(harness.Repository.ImportCalls);
            Assert.Equal(0, harness.PrebuiltPakImporter.ImportCallCount);
            // The "as a UE4SS mod" wording is logged to the activity log, not StatusMessage —
            // StatusMessage only ever carries the generic "Imported N mod(s)." summary (or a
            // partial-failure variant), same as every other classification branch.
            Assert.Contains(harness.ActivityLog.Entries, e => e.Message.Contains("as a UE4SS mod"));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task ImportPaths_OneOfSeveralFailsToExtract_StillImportsTheRestAndNamesTheFailure()
    {
        var harness = new TestHarness();
        var goodFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(goodFolder);
        // Never created on disk at all — falls through to AnyArchiveExtractor, which throws because
        // the file doesn't even exist to sniff a format from.
        var badPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.zip");
        try
        {
            harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => new LibraryEntry
            {
                FolderName = "GoodMod", Name = "Good Mod", Author = "Someone", Version = "1.0", Description = "D", FileName = "GoodMod",
            };
            var vm = harness.Build();

            await vm.ImportPaths([goodFolder, badPath]);

            Assert.Single(harness.Repository.ImportCalls);
            Assert.Equal(1, vm.ModCount);
            Assert.Contains("Imported 1 of 2", vm.StatusMessage);
            Assert.Contains(Path.GetFileName(badPath), vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(goodFolder, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // DeleteMods (DeleteItem/DeleteAll) — confirm-then-delete, including this session's own two
    // new purge calls (IPrebuiltPakSourceStore.Delete and the pak-preview-cache folder delete).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DeleteItemCommand_Confirmed_RemovesFromRepositoryAndPurgesBothTheSourceStoreAndThePakPreviewCache()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        var previewCacheDir = Path.Combine(harness.PakPreviewCacheDirectory, "ModA");
        Directory.CreateDirectory(previewCacheDir);
        File.WriteAllText(Path.Combine(previewCacheDir, "cached.bin"), "x");
        try
        {
            vm.DeleteItemCommand.Execute(item);

            Assert.Equal(1, harness.DialogService.ConfirmCallCount);
            var deletedFolder = Assert.Single(harness.Repository.DeleteCalls);
            Assert.Equal("ModA", deletedFolder);
            Assert.Contains("ModA", harness.PrebuiltPakSourceStore.DeleteCalls);
            Assert.False(Directory.Exists(previewCacheDir), "the mod's own pak-preview cache folder should have been purged");
            Assert.Equal(0, vm.ModCount);
            Assert.Contains("Deleted 'ModA'", vm.StatusMessage);
        }
        finally
        {
            if (Directory.Exists(harness.PakPreviewCacheDirectory))
            {
                Directory.Delete(harness.PakPreviewCacheDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteItemCommand_Declined_LeavesRepositoryAndBothCachesUntouched()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: false) };
        harness.AddMod("ModA");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        vm.DeleteItemCommand.Execute(item);

        Assert.Empty(harness.Repository.DeleteCalls);
        Assert.Empty(harness.PrebuiltPakSourceStore.DeleteCalls);
        Assert.Equal(1, vm.ModCount);
    }

    [Fact]
    public void DeleteItemCommand_WithAnActiveBulkSelection_DeletesEveryBulkSelectedItemInsteadOfJustTheClickedRow()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        harness.AddMod("ModB");
        harness.AddMod("ModC");
        var vm = harness.Build();
        var modA = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        var modB = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModB");
        var modC = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModC");
        vm.ToggleBulkSelection(modA);
        vm.ToggleBulkSelection(modB);

        // ModC is passed in directly (as if right-clicked) but was never folded into the bulk
        // selection here (that's code-behind's job in the real app) — DeleteMods must still favor
        // the existing BulkSelectedItems over the single passed-in row.
        vm.DeleteItemCommand.Execute(modC);

        Assert.Equal(2, harness.Repository.DeleteCalls.Count);
        Assert.Contains("ModA", harness.Repository.DeleteCalls);
        Assert.Contains("ModB", harness.Repository.DeleteCalls);
        Assert.DoesNotContain("ModC", harness.Repository.DeleteCalls);
        Assert.Equal(1, vm.ModCount);
        Assert.Empty(vm.BulkSelectedItems);
    }

    [Fact]
    public void DeleteItemCommand_OneItemFailsToDelete_StillDeletesTheRestAndReportsThePartialFailure()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        harness.AddMod("ModB");
        harness.Repository.DeleteException = ("ModA", new IOException("file is locked by another process"));
        var vm = harness.Build();
        var modA = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        var modB = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModB");
        vm.ToggleBulkSelection(modA);
        vm.ToggleBulkSelection(modB);

        vm.DeleteItemCommand.Execute(null);

        Assert.Contains("ModB", harness.Repository.DeleteCalls);
        // The failing item's own purge calls never ran — Delete() threw before either purge line.
        Assert.DoesNotContain("ModA", harness.PrebuiltPakSourceStore.DeleteCalls);
        Assert.Contains("ModB", harness.PrebuiltPakSourceStore.DeleteCalls);
        Assert.Contains("Deleted 1 of 2", vm.StatusMessage);
        Assert.Contains("locked by another process", vm.StatusMessage);
        Assert.Equal(1, vm.ModCount);
        Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ModA");
    }

    [Fact]
    public void DeleteAllCommand_DeletesEveryModRegardlessOfCurrentSelection()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        harness.AddMod("ModB");
        var vm = harness.Build();

        vm.DeleteAllCommand.Execute(null);

        Assert.Equal(2, harness.Repository.DeleteCalls.Count);
        Assert.Equal(0, vm.ModCount);
        Assert.Contains("Deleted 2 mod(s)", vm.StatusMessage);
    }

    // ---------------------------------------------------------------------------------------
    // RenameItem — the IDialogService.PromptRename flow and its write-through to the repository.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void RenameItemCommand_Confirmed_WritesDisplayNameOverrideThroughToTheRepositoryAndReloadsTheRow()
    {
        var dialogService = new FakeDialogService { PromptRenameResult = new RenamePromptResult(false, "My Custom Name") };
        var harness = new TestHarness { DialogService = dialogService };
        harness.AddMod("ModA", name: "Original Name");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        vm.RenameItemCommand.Execute(item);

        Assert.Equal(1, dialogService.PromptRenameCallCount);
        Assert.Equal("Original Name", dialogService.LastPromptRenameCurrentName);
        var renameCall = Assert.Single(harness.Repository.SetDisplayNameOverrideCalls);
        Assert.Equal("ModA", renameCall.FolderName);
        Assert.Equal("My Custom Name", renameCall.DisplayName);
        var reloaded = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        Assert.Equal("My Custom Name", reloaded.Name);
    }

    [Fact]
    public void RenameItemCommand_Cancelled_NeverWritesToTheRepository()
    {
        var dialogService = new FakeDialogService { PromptRenameResult = new RenamePromptResult(Cancelled: true, NewDisplayName: null) };
        var harness = new TestHarness { DialogService = dialogService };
        harness.AddMod("ModA", name: "Original Name");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        vm.RenameItemCommand.Execute(item);

        Assert.Empty(harness.Repository.SetDisplayNameOverrideCalls);
        Assert.Equal("Original Name", item.Name);
    }

    [Fact]
    public void RenameItemCommand_ResetToDefault_WritesANullOverrideRatherThanABlankName()
    {
        var dialogService = new FakeDialogService { PromptRenameResult = new RenamePromptResult(Cancelled: false, NewDisplayName: null) };
        var harness = new TestHarness { DialogService = dialogService };
        var entry = harness.AddMod("ModA", name: "Custom Override");
        entry.DisplayNameOverride = "Custom Override";
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        vm.RenameItemCommand.Execute(item);

        var call = Assert.Single(harness.Repository.SetDisplayNameOverrideCalls);
        Assert.Equal("ModA", call.FolderName);
        Assert.Null(call.DisplayName);
    }

    [Fact]
    public void RenameItemCommand_RepositoryThrows_ReportsFailureInStatusMessage()
    {
        var dialogService = new FakeDialogService { PromptRenameResult = new RenamePromptResult(false, "New Name") };
        var harness = new TestHarness { DialogService = dialogService };
        harness.AddMod("ModA");
        harness.Repository.SetDisplayNameOverrideException = new IOException("sidecar metadata file is locked");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        vm.RenameItemCommand.Execute(item);

        Assert.Contains("Couldn't rename", vm.StatusMessage);
        Assert.Contains("sidecar metadata file is locked", vm.StatusMessage);
    }

    // ---------------------------------------------------------------------------------------
    // Reload/Refresh — rebuilding the mod list from the repository's current state, including
    // an externally-driven LibraryChangedMessage (e.g. an import from Downloads/another page).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Constructor_RepositoryAlreadyHasMods_ReloadsThemImmediatelyWithNoExplicitActionNeeded()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA");
        harness.AddMod("ModB");

        var vm = harness.Build();

        Assert.Equal(2, vm.ModCount);
        Assert.Equal(2, vm.RootItems.OfType<LibraryItemViewModel>().Count());
    }

    [Fact]
    public void Reload_RepositoryReportsUnreadableFolders_SurfacesThemInUnreadableFoldersMessage()
    {
        var harness = new TestHarness();
        harness.Repository.UnreadableFoldersList = ["BrokenModFolder"];

        var vm = harness.Build();

        Assert.NotNull(vm.UnreadableFoldersMessage);
        Assert.Contains("BrokenModFolder", vm.UnreadableFoldersMessage);
    }

    [Fact]
    public void RefreshCommand_CallsRepositoryRefreshThenRebuildsRootItemsFromWhateverItNowReturns()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA");
        var vm = harness.Build();
        Assert.Equal(1, vm.ModCount);

        // Refresh() itself just calls repository.Refresh() (a real re-scan of disk in production);
        // this fake models that re-scan's effect on GetAll/Search directly, simulating a mod folder
        // that appeared on disk between construction and the click.
        harness.AddMod("ModB");

        vm.RefreshCommand.Execute(null);

        Assert.Equal(1, harness.Repository.RefreshCallCount);
        Assert.Equal(2, vm.ModCount);
        Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ModB");
        Assert.Equal("Library refreshed.", vm.StatusMessage);
    }

    [Fact]
    public void RefreshCommand_RepositoryRefreshThrows_ReportsFailureWithoutRebuilding()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA");
        harness.Repository.RefreshException = new IOException("Extracted_Mods is on a disconnected network drive");
        var vm = harness.Build();

        vm.RefreshCommand.Execute(null);

        Assert.Contains("Refresh failed", vm.StatusMessage);
        Assert.Contains("disconnected network drive", vm.StatusMessage);
    }

    [Fact]
    public void LibraryChangedMessage_TriggersAFullResyncThatPicksUpAModAddedFromAnotherPage()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA");
        var vm = harness.Build();
        Assert.Equal(1, vm.ModCount);

        try
        {
            // A different page (e.g. Downloads' own Download & extract) importing through the same
            // shared ILibraryRepository, then broadcasting the same message LibraryViewModel
            // registers for at construction time.
            harness.AddMod("ModFromDownloads");
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

            Assert.Equal(2, vm.ModCount);
            Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ModFromDownloads");
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<LibraryChangedMessage>(vm);
        }
    }

    [Fact]
    public void LibraryChangedMessage_ExistingModChangedExternally_FullResyncUpdatesTheAlreadyCachedRowInPlace()
    {
        var harness = new TestHarness();
        var entry = harness.AddMod("ModA", notes: "original notes");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        Assert.Equal("original notes", item.Notes);

        try
        {
            // Simulates the sidecar being edited by another page's own write (Reload's own doc
            // comment: fullResync is what makes an already-cached instance pick up a change it
            // didn't itself make).
            entry.Notes = "changed by another page";
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

            var sameItem = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
            Assert.Same(item, sameItem);
            Assert.Equal("changed by another page", sameItem.Notes);
        }
        finally
        {
            WeakReferenceMessenger.Default.Unregister<LibraryChangedMessage>(vm);
        }
    }

    // ---------------------------------------------------------------------------------------
    // CheckModsAgainstCurrentDataCommand's own auto-fix pass vs. an open EXMOD editor window —
    // the race LibraryViewModel's _openEditorCountByFolder/MarkFolderEditorOpenForTesting exist
    // to close. See CreateStaleAutoFixableModOnDisk's own doc comment for how the fixture
    // reproduces a genuine ExmodStalenessChecker.FindLikelyStaleItems + StaleItemFixSuggester
    // CanAutoApply: true hit, not just a suggestion in isolation.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckModsAgainstCurrentDataCommand_FolderHasAnOpenEditorWindow_NeverWritesTheAutoFixToDisk()
    {
        // Regression guard: CheckModsAgainstCurrentDataAsync's background auto-fix pass used to back
        // up and rewrite a mod's own .EXMOD with no regard for whether that same mod folder was
        // already open in an ExmodEditorViewModel/ExmodEditorWindow (via LibraryViewModel.OpenEditor).
        // The editor's own in-memory _package was loaded from disk BEFORE the auto-fix ran, so it has
        // no idea the auto-fix happened — the very next click of that still-open editor's own Save
        // button would write its stale in-memory copy straight back over the just-applied auto-fix,
        // silently discarding it with no error and leaving the repository inconsistent with what the
        // UI just told the user. The fix: CheckModsAgainstCurrentDataAsync snapshots which folders
        // currently have an open editor window (_openEditorCountByFolder) on the UI thread before its
        // background Task.Run, and CheckOneModAgainstCurrentData's auto-apply branch is gated on
        // `allowAutoFix` — still detecting/reporting the stale item as usual, just never backing up or
        // writing anything for that mod this pass. MarkFolderEditorOpenForTesting is the test-only
        // seam that simulates "this folder has an open editor" without a real WPF Window, which can't
        // run in this test host (see its own doc comment, and other ViewModel tests' precedent for the
        // same constraint).
        var harness = new TestHarness();
        const string folderName = "RaceyMod_GuardedByOpenEditor";
        var modFolderPath = CreateStaleAutoFixableModOnDisk(harness, folderName);
        try
        {
            var vm = harness.Build();
            vm.MarkFolderEditorOpenForTesting(folderName);

            await vm.CheckModsAgainstCurrentDataCommand.ExecuteAsync(null);

            var packageOnDisk = ExmodFolder.ReadPackageOnly(modFolderPath);
            var itemOnDisk = Assert.Single(Assert.Single(packageOnDisk.Rows).FileItems);
            Assert.Equal(StaleItemName, itemOnDisk.Name);
            Assert.DoesNotContain(folderName, harness.Repository.BackupModCalls);

            // Still detected and reported as stale — allowAutoFix:false only skips the write, not
            // the detection — so the row's own warning badge still shows up for the user to fix by
            // hand (e.g. via the still-open editor) once they close it and this check runs again.
            var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == folderName);
            Assert.True(item.HasPossiblyStaleItems);
        }
        finally
        {
            Directory.Delete(modFolderPath, recursive: true);
        }
    }

    [Fact]
    public async Task CheckModsAgainstCurrentDataCommand_NoOpenEditorWindow_AppliesTheAutoFixToDiskAndBacksUpFirst()
    {
        // The control for the guarded test above: same fixture, same mod, just without
        // MarkFolderEditorOpenForTesting — proving the fixture genuinely reaches and exercises the
        // auto-fix write path at all. Without this, the guarded test passing could just as easily
        // mean the fixture never triggered CanAutoApply in the first place, not that the guard
        // actually worked.
        var harness = new TestHarness();
        const string folderName = "RaceyMod_NoOpenEditor";
        var modFolderPath = CreateStaleAutoFixableModOnDisk(harness, folderName);
        try
        {
            var vm = harness.Build();

            await vm.CheckModsAgainstCurrentDataCommand.ExecuteAsync(null);

            var packageOnDisk = ExmodFolder.ReadPackageOnly(modFolderPath);
            var itemOnDisk = Assert.Single(Assert.Single(packageOnDisk.Rows).FileItems);
            Assert.Equal(FixedItemName, itemOnDisk.Name);
            Assert.Contains(folderName, harness.Repository.BackupModCalls);

            var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == folderName);
            Assert.False(item.HasPossiblyStaleItems);
        }
        finally
        {
            Directory.Delete(modFolderPath, recursive: true);
        }
    }

    // Exact same scenario StaleItemFixSuggesterTests.Suggest_UnambiguousCloseMatchWithGoodFieldOverlap_CanAutoApply
    // proves triggers CanAutoApply: true (a stale item named "Stone_Pickaxe_Mk2" with field
    // "RequiredMillijoules", against a base row "Stone_Pickaxe_MK2" with that same field) — reused
    // here rather than inventing a new one, since that test is the known-good reference for what
    // StaleItemFixSuggester.Suggest actually returns CanAutoApply: true for.
    private const string StaleItemName = "Stone_Pickaxe_Mk2";
    private const string FixedItemName = "Stone_Pickaxe_MK2";
    private const string StaleItemCurrentFile = "Items-D_ItemTemplate.json";

    /// <summary>
    /// Builds a REAL on-disk mod folder (a real .EXMOD via ExmodFolder.Write) containing exactly one
    /// item shaped to reproduce StaleItemFixSuggesterTests' own proven CanAutoApply: true scenario
    /// (see the constants above), AND writes a matching real base-game JSON file under the harness's
    /// own DataFolder (a real "Stone_Pickaxe_MK2" row with the same field) so
    /// ExmodStalenessChecker.FindLikelyStaleItems' own diff-against-base step genuinely flags it as
    /// stale too — not just StaleItemFixSuggester in isolation. Registers the mod with the harness
    /// (AddMod) and wires FakeLibraryRepository.FolderPathOverrides so
    /// CheckOneModAgainstCurrentData's real file I/O (ExmodFolder.ReadPackageOnly/Write, both keyed
    /// off ILibraryRepository.GetFolderPath) lands on this real folder rather than the fake
    /// repository's default nonexistent-path stand-in. Must be called BEFORE harness.Build() — same
    /// AddMod-before-Build ordering every other test in this file already follows.
    /// </summary>
    private static string CreateStaleAutoFixableModOnDisk(TestHarness harness, string folderName)
    {
        var baseDataDir = Path.Combine(harness.DataFolder, "Items");
        Directory.CreateDirectory(baseDataDir);
        File.WriteAllText(
            Path.Combine(baseDataDir, "D_ItemTemplate.json"),
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe_MK2","RequiredMillijoules":100}]}""");

        var modFolderPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modFolderPath);
        var package = new ExmodPackage
        {
            Name = "Stale Race Mod", Author = "Someone", Version = "1.0", Description = "D", FileName = folderName,
            Rows =
            [
                new ExmodFileRow
                {
                    CurrentFile = StaleItemCurrentFile,
                    FileItems = [new ExmodFileItem { Name = StaleItemName, Fields = { ["RequiredMillijoules"] = JsonValue.Create(100) } }],
                },
            ],
        };
        ExmodFolder.Write(modFolderPath, new ExmodPackageContents(package, []));

        harness.AddMod(folderName);
        harness.Repository.FolderPathOverrides[folderName] = modFolderPath;
        return modFolderPath;
    }

    // A search-debounce test (SearchText -> wait past 250ms -> assert the filtered result) was
    // deliberately not added here: DebounceTimer wraps a WPF DispatcherTimer, whose Tick only fires
    // when something pumps that thread's Dispatcher message queue — which a plain xUnit host never
    // does, regardless of how long a real Task.Delay waits. This exact constraint is already
    // documented on DebounceTimerTests.cs's own Restart_CalledAgain_StaysRunning ("can't observe
    // the actual reset of elapsed time without a live Dispatcher message pump") — this file follows
    // that same precedent rather than adding a test that fails deterministically in this host.

    // ---------------------------------------------------------------------------------------
    // GetUpdateAsync — picked as the single riskiest, most complex, completely-untested method
    // in this file (see the class-level doc comment). It deletes-then-reimports a mod's real
    // folder in place with a backup/restore safety net; its own restore branch has an inline
    // comment describing "a real bug found live" (metadata silently forgotten on a failed
    // update) — exactly the kind of asymmetric-risk, no-coverage-at-all logic this pass exists
    // to close. Only the Database-sourced branch (the actual delete/reimport/rollback machinery)
    // and the two cheap early-return branches are covered here — see the report for what's
    // deliberately left out (the Nexus-without-a-pending-download branch, which opens a real
    // NexusCatalogViewModel).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetUpdateCommand_DatabaseSourcedModSuccessfulUpdate_PurgesTheOldFolderAndAppliesMetadataToTheNewOne()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: false) };
        var entry = harness.AddMod(
            "ModA_v1", name: "ModA", version: "1.0", source: "Database", catalogEntryId: "cat1",
            isPinned: true, notes: "great mod");
        entry.DisplayNameOverride = "My Custom Name";

        harness.DaedalusClient.Result =
        [
            new CatalogEntry(CatalogSource.Daedalus, "cat1", "ModA", "SomeAuthor", "2.0", "d", "", null, null, null, null, "http://fake.test/ModA.exmodz", []),
        ];
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler("fake-exmodz-bytes"u8.ToArray()));
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => new LibraryEntry
        {
            FolderName = "ModA_v2", Name = "ModA", Author = "SomeAuthor", Version = "2.0", Description = "d",
            FileName = "ModA_v2", Source = source, CatalogEntryId = catalogId,
        };

        var vm = harness.Build();
        await vm.Downloads.GetOrFetchCatalogAsync();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA_v1");

        await vm.GetUpdateCommand.ExecuteAsync(item);

        Assert.Contains("ModA_v1", harness.Repository.BackupModCalls);
        Assert.Contains("ModA_v1", harness.Repository.DeleteCalls);
        Assert.Contains("ModA_v1", harness.PrebuiltPakSourceStore.DeleteCalls);
        Assert.Empty(harness.Repository.RestoreLatestModBackupCalls);

        var importCall = Assert.Single(harness.Repository.ImportCalls);
        Assert.True(importCall.SourcePath.EndsWith(".EXMODZ", StringComparison.OrdinalIgnoreCase), $"expected a .EXMODZ temp path, got '{importCall.SourcePath}'");
        Assert.Equal("Database", importCall.Source);
        Assert.Equal("cat1", importCall.CatalogEntryId);

        // The metadata carry-over must land on the NEW folder name the reimport produced, not the
        // old one that was just deleted — a real class of bug if the code ever used `folderName`
        // (captured before the delete) instead of `imported.FolderName` here.
        Assert.Contains(("ModA_v2", true, false, "great mod"), harness.Repository.UpdateMetadataCalls);
        Assert.Contains(("ModA_v2", "My Custom Name"), harness.Repository.SetDisplayNameOverrideCalls);

        Assert.Contains("Updated 'ModA' to v2.0.", vm.StatusMessage);
        Assert.Equal(1, vm.ModCount);
        Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ModA_v2");
        Assert.DoesNotContain(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ModA_v1");
    }

    [Fact]
    public async Task GetUpdateCommand_SuccessfulUpdateAcceptsVersionCompare_LooksUpTheBackupUnderTheOldFolderNameAndTheCurrentFolderUnderTheNew()
    {
        // Regression test for TWO bugs in this same flow, found one after the other:
        //
        // Bug 1 (fixed first): OfferVersionComparisonAsync used to be called with imported.FolderName
        // (the NEW folder name a reimport can land on) for BOTH the backup lookup and the current-
        // folder lookup, instead of the OLD name BackupMod actually saved the backup under —
        // silently reporting "no earlier copy" even when one exists.
        //
        // Bug 2 (found reviewing the fix for bug 1): fixing bug 1 by switching BOTH lookups to the
        // OLD name instead just traded which one broke — TryGetLatestModBackupPath("ModA_v1")
        // (correct) but then GetFolderPath("ModA_v1") (wrong: that folder was already Delete()d:
        // the current copy lives under "ModA_v2" now). The real fix needed two separate folder-name
        // parameters, one per lookup.
        //
        // This test uses two DIFFERENT folder names on purpose (ModA_v1 -> ModA_v2) and a
        // FakeLibraryRepository.GetFolderPath that actually enforces existence (mirroring the real
        // FolderLibraryRepository) so all three outcomes are genuinely distinguishable:
        //  - bug 1 present:  TryGetLatestModBackupPath("ModA_v2") -> null -> "no earlier copy" message.
        //  - bug 2 present:  TryGetLatestModBackupPath("ModA_v1") -> succeeds, but
        //                    GetFolderPath("ModA_v1") -> throws DirectoryNotFoundException (that
        //                    folder's gone) -> "Couldn't compare versions: No library entry named…"
        //  - both fixed:     TryGetLatestModBackupPath("ModA_v1") and GetFolderPath("ModA_v2") both
        //                    succeed -> proceeds into IModVersionComparer.CompareAsync, which this
        //                    harness's fake always throws NotSupportedException from (see
        //                    FakeModVersionComparer's own comment) -> "Couldn't compare versions:
        //                    Not exercised by these tests…", the only outcome that proves BOTH
        //                    lookups actually succeeded with their own correct, different folder name.
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA_v1", name: "ModA", version: "1.0", source: "Database", catalogEntryId: "cat1");

        harness.DaedalusClient.Result =
        [
            new CatalogEntry(CatalogSource.Daedalus, "cat1", "ModA", "SomeAuthor", "2.0", "d", "", null, null, null, null, "http://fake.test/ModA.exmodz", []),
        ];
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler("fake-exmodz-bytes"u8.ToArray()));
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => new LibraryEntry
        {
            FolderName = "ModA_v2", Name = "ModA", Author = "SomeAuthor", Version = "2.0", Description = "d",
            FileName = "ModA_v2", Source = source, CatalogEntryId = catalogId,
        };

        var vm = harness.Build();
        await vm.Downloads.GetOrFetchCatalogAsync();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA_v1");

        await vm.GetUpdateCommand.ExecuteAsync(item);

        Assert.DoesNotContain("no earlier copy", vm.StatusMessage);
        Assert.DoesNotContain("No library entry named", vm.StatusMessage);
        Assert.Contains("Couldn't compare versions", vm.StatusMessage);
        Assert.Contains("Not exercised by these tests", vm.StatusMessage);
    }

    [Fact]
    public async Task GetUpdateCommand_DatabaseSourcedModImportFails_RestoresTheBackupAndReappliesMetadataToTheOldFolder()
    {
        // Regression test for the fix described inline on GetUpdateAsync's own restore branch:
        // RestoreLatestModBackup only brings back the mod's own EXMOD/asset folder, never the
        // separate Pin/Favorite/Notes/DisplayNameOverride/CatalogEntryId sidecar (Delete() deletes
        // that sidecar outright, and the backup never captured it) — so a failed update used to
        // silently reset all of that to blank even though the mod's real content came back fine.
        // This fake's own BackupMod/RestoreLatestModBackup deliberately mirror that same real gap
        // (the restored entry starts with blank metadata) so this test only passes if
        // GetUpdateAsync's explicit reapply calls actually ran.
        var harness = new TestHarness();
        var entry = harness.AddMod(
            "ModA", name: "ModA", version: "1.0", source: "Database", catalogEntryId: "cat1",
            isPinned: true, isFavorite: true, notes: "great mod");
        entry.DisplayNameOverride = "My Custom Name";

        harness.DaedalusClient.Result =
        [
            new CatalogEntry(CatalogSource.Daedalus, "cat1", "ModA", "SomeAuthor", "2.0", "d", "", null, null, null, null, "http://fake.test/ModA.exmodz", []),
        ];
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler("corrupt-bytes"u8.ToArray()));
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => throw new FormatException("corrupt download");

        var vm = harness.Build();
        await vm.Downloads.GetOrFetchCatalogAsync();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        await vm.GetUpdateCommand.ExecuteAsync(item);

        Assert.Contains("ModA", harness.Repository.BackupModCalls);
        Assert.Contains("ModA", harness.Repository.DeleteCalls);
        Assert.Contains("ModA", harness.Repository.RestoreLatestModBackupCalls);

        var restored = harness.Repository.GetAll().Single(e => e.FolderName == "ModA");
        Assert.True(restored.IsPinned);
        Assert.True(restored.IsFavorite);
        Assert.Equal("great mod", restored.Notes);
        Assert.Equal("My Custom Name", restored.DisplayNameOverride);
        Assert.Equal("cat1", restored.CatalogEntryId);

        Assert.Contains("restored from its backup", vm.StatusMessage);
        Assert.Equal(1, vm.ModCount);
        Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ModA");
    }

    [Fact]
    public async Task GetUpdateCommand_NexusSourcedModWithAPendingDownloadAlreadyPresent_PointsThereInsteadOfTouchingTheRepository()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", source: "Nexus", nexusModId: 555);
        harness.PendingDownloadStore.EntriesList.Add(new PendingDownloadEntry
        {
            ModId = 555, FileId = 1, FileName = "ModA.zip", LocalFilePath = @"C:\fake\ModA.zip",
        });
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        await vm.GetUpdateCommand.ExecuteAsync(item);

        Assert.Empty(harness.Repository.DeleteCalls);
        Assert.Empty(harness.Repository.BackupModCalls);
        Assert.Contains("already in this page's own Mods tab", vm.StatusMessage);
    }

    [Fact]
    public async Task GetUpdateCommand_DatabaseSourcedModNoLongerInTheCatalog_ReportsStatusWithoutTouchingTheRepository()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", source: "Database", catalogEntryId: "cat-missing");
        // Daedalus/Jimk72 both default to an empty catalog — nothing matches at all.
        var vm = harness.Build();
        await vm.Downloads.GetOrFetchCatalogAsync();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        await vm.GetUpdateCommand.ExecuteAsync(item);

        Assert.Empty(harness.Repository.DeleteCalls);
        Assert.Empty(harness.Repository.BackupModCalls);
        Assert.Contains("Couldn't find 'ModA' in the catalog anymore.", vm.StatusMessage);
    }

    // =========================================================================================
    // Test harness — one shared bundle of fakes for every dependency LibraryViewModel's own
    // (very large) constructor needs, plus a real DownloadsViewModel (itself constructed from
    // fakes) since LibraryViewModel takes that as a concrete, eagerly-needed dependency, not a
    // lazy factory. Fakes not touched by any test here throw NotSupportedException, matching
    // this project's established convention.
    // =========================================================================================

    private sealed class TestHarness
    {
        public FakeLibraryRepository Repository { get; } = new();
        public FakeUe4ssModRepository Ue4ssModRepository { get; } = new();
        public FakeUe4ssModStateService Ue4ssModStateService { get; } = new();
        public FakeUe4ssModMetaStore Ue4ssModMetaStore { get; } = new();
        public FakeUe4ssLoaderInstallService Ue4ssLoaderInstallService { get; } = new();
        public FakeSettingsService SettingsService { get; } = new();
        public FakeUnrealPakService UnrealPakService { get; } = new();
        public FakeUassetTextureDecoder TextureDecoder { get; } = new();
        public FakeUassetStaticMeshDecoder StaticMeshDecoder { get; } = new();
        public FakeUassetSkeletalMeshDecoder SkeletalMeshDecoder { get; } = new();
        public FakeUassetSoundDecoder SoundDecoder { get; } = new();
        public FakeUassetMaterialDecoder MaterialDecoder { get; } = new();
        public FakeOpaquePakAssetPreviewService OpaquePakAssetPreviewService { get; } = new();
        public FakeNexusApiClient NexusApiClient { get; } = new();
        public FakeCredentialStore CredentialStore { get; } = new();
        public FakeActivityLog ActivityLog { get; } = new();
        public HttpClient DownloadHttpClient { get; set; } = new();
        public FakePendingDownloadStore PendingDownloadStore { get; } = new();
        public FakeModVersionComparer ModVersionComparer { get; } = new();
        public FakePrebuiltPakImporter PrebuiltPakImporter { get; }
        public FakePrebuiltPakToExmodConverter PrebuiltPakToExmodConverter { get; } = new();
        public FakePrebuiltPakSourceStore PrebuiltPakSourceStore { get; } = new();
        public FakeDialogService DialogService { get; set; } = new(confirmResult: true);
        public FakeDaedalusClient DaedalusClient { get; } = new();
        public FakeJimk72Client Jimk72Client { get; } = new();
        public FakeGitHubRepoDateClient GitHubRepoDateClient { get; } = new();
        public ActiveDownloadsTracker ActiveDownloadsTracker { get; } = new();

        public string ThumbnailCacheDirectory { get; } = UniqueTempPath("ThumbCache");
        public string PakPreviewCacheDirectory { get; } = UniqueTempPath("PakPreviewCache");
        public string BackupDirectory { get; } = UniqueTempPath("Backups");
        public string DataFolder { get; } = UniqueTempPath("DataFolder");
        public string PendingDownloadsDirectory { get; } = UniqueTempPath("PendingDownloads");
        public string LogsDirectory { get; } = UniqueTempPath("Logs");

        public TestHarness() => PrebuiltPakImporter = new FakePrebuiltPakImporter(Repository);

        private static string UniqueTempPath(string label) =>
            Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_" + label);

        public LibraryEntry AddMod(
            string folderName, string? name = null, string author = "Someone", string version = "1.0",
            bool isPinned = false, bool isFavorite = false, string notes = "", string? source = null,
            string? catalogEntryId = null, int? nexusModId = null, bool isOpaquePak = false)
        {
            var entry = new LibraryEntry
            {
                FolderName = folderName, Name = name ?? folderName, Author = author, Version = version,
                Description = "A test mod.", FileName = folderName, IsPinned = isPinned, IsFavorite = isFavorite,
                Notes = notes, Source = source, CatalogEntryId = catalogEntryId, NexusModId = nexusModId,
                IsOpaquePak = isOpaquePak, ImportedAtUtc = DateTimeOffset.UtcNow,
            };
            Repository.Seed(entry);
            return entry;
        }

        public LibraryViewModel Build()
        {
            var downloads = new DownloadsViewModel(
                DaedalusClient, Jimk72Client, GitHubRepoDateClient, Repository, Ue4ssModRepository, Ue4ssModMetaStore,
                SettingsService, NexusApiClient, CredentialStore, PendingDownloadStore, new HttpClient(),
                new PerformanceTracker(SettingsService, LogsDirectory), ActivityLog, ActiveDownloadsTracker,
                PendingDownloadsDirectory, PrebuiltPakImporter, DataFolder);

            return new LibraryViewModel(
                Repository, Ue4ssModRepository, Ue4ssModStateService, Ue4ssModMetaStore, Ue4ssLoaderInstallService,
                SettingsService, UnrealPakService, TextureDecoder, StaticMeshDecoder, SkeletalMeshDecoder, SoundDecoder,
                MaterialDecoder, OpaquePakAssetPreviewService, NexusApiClient, CredentialStore,
                _ => throw new NotSupportedException("Not exercised by these tests — no test opens the EXMOD editor."),
                ActivityLog, DownloadHttpClient, PendingDownloadStore, ModVersionComparer, downloads,
                () => throw new NotSupportedException("Not exercised by these tests — no test searches Nexus."),
                ActiveDownloadsTracker,
                () => throw new NotSupportedException("Not exercised by these tests — no test adds to the merge queue."),
                BackupDirectory, DataFolder, PrebuiltPakImporter, PrebuiltPakToExmodConverter, PrebuiltPakSourceStore,
                ThumbnailCacheDirectory, PakPreviewCacheDirectory, DialogService);
        }
    }

    private sealed class StaticByteResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private sealed class FakeLibraryRepository : ILibraryRepository
    {
        private readonly List<LibraryEntry> _entries = [];
        private readonly Dictionary<string, LibraryEntry> _backups = new(StringComparer.OrdinalIgnoreCase);

        public Func<string, string?, int?, string?, LibraryEntry>? ImportHandler { get; set; }
        public List<string> UnreadableFoldersList { get; set; } = [];
        public List<string> DeleteCalls { get; } = [];
        public (string FolderName, Exception Exception)? DeleteException { get; set; }
        public List<string> BackupModCalls { get; } = [];
        public List<string> RestoreLatestModBackupCalls { get; } = [];
        public List<(string SourcePath, string? Source, int? NexusModId, string? CatalogEntryId)> ImportCalls { get; } = [];
        public List<(string FolderName, bool IsPinned, bool IsFavorite, string Notes)> UpdateMetadataCalls { get; } = [];
        public List<(string FolderName, string? DisplayName)> SetDisplayNameOverrideCalls { get; } = [];
        public Exception? SetDisplayNameOverrideException { get; set; }
        public List<(string FolderName, string CatalogEntryId)> SetCatalogEntryCalls { get; } = [];
        public List<(string FolderName, int NexusModId)> LinkToNexusCalls { get; } = [];
        public int RefreshCallCount { get; private set; }
        public Exception? RefreshException { get; set; }

        /// <summary>
        /// Additive escape hatch for a test that needs GetFolderPath to point at a REAL, test-created
        /// directory on disk instead of GetFolderPath's own default nonexistent-path stand-in (see
        /// that method's own comment) — e.g. CheckModsAgainstCurrentDataCommand's auto-fix pass does
        /// genuine file I/O (ExmodFolder.ReadPackageOnly/Write) against whatever GetFolderPath
        /// returns, which the default stand-in deliberately can't satisfy. Starts empty, so every
        /// test that never touches this dictionary keeps getting the exact same nonexistent-path
        /// behavior as before this was added — only a folder name explicitly added here is affected.
        /// </summary>
        public Dictionary<string, string> FolderPathOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(LibraryEntry entry) => _entries.Add(entry);

        public IReadOnlyList<LibraryEntry> GetAll() => [.. _entries];

        public IReadOnlyList<string> UnreadableFolders => UnreadableFoldersList;

        public IReadOnlyList<LibraryEntry> Search(string query) =>
            string.IsNullOrWhiteSpace(query)
                ? GetAll()
                : [.. _entries.Where(e =>
                    e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || e.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || e.Description.Contains(query, StringComparison.OrdinalIgnoreCase))];

        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
        {
            ImportCalls.Add((sourcePath, source, nexusModId, catalogEntryId));
            var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var entry = ImportHandler is not null
                ? ImportHandler(sourcePath, source, nexusModId, catalogEntryId)
                : new LibraryEntry
                {
                    FolderName = Path.GetFileNameWithoutExtension(trimmed), Name = Path.GetFileNameWithoutExtension(trimmed),
                    Author = "Someone", Version = "1.0", Description = "D", FileName = Path.GetFileNameWithoutExtension(trimmed),
                    Source = source, NexusModId = nexusModId, CatalogEntryId = catalogEntryId, ImportedAtUtc = DateTimeOffset.UtcNow,
                };
            _entries.Add(entry);
            return entry;
        }

        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null) =>
            throw new NotSupportedException("Not exercised by these tests — LibraryViewModel goes through IPrebuiltPakImporter, not this directly.");

        public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version) { }

        public void Refresh()
        {
            RefreshCallCount++;
            if (RefreshException is not null)
            {
                throw RefreshException;
            }
        }

        public void Delete(string folderName)
        {
            DeleteCalls.Add(folderName);
            if (DeleteException is { } de && string.Equals(de.FolderName, folderName, StringComparison.OrdinalIgnoreCase))
            {
                throw de.Exception;
            }

            var entry = _entries.FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                _entries.Remove(entry);
            }
        }

        public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes)
        {
            UpdateMetadataCalls.Add((folderName, isPinned, isFavorite, notes));
            var entry = _entries.FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                entry.IsPinned = isPinned;
                entry.IsFavorite = isFavorite;
                entry.Notes = notes;
            }
        }

        public void MarkLocallyEdited(string folderName) { }

        public void MarkConvertedFromPrebuiltPak(string folderName) { }

        public void SetDisplayNameOverride(string folderName, string? displayName)
        {
            SetDisplayNameOverrideCalls.Add((folderName, displayName));
            if (SetDisplayNameOverrideException is not null)
            {
                throw SetDisplayNameOverrideException;
            }

            // Replaces the list slot with a brand-new LibraryEntry rather than mutating the
            // existing one in place — matches the real FolderLibraryRepository.SetDisplayNameOverride
            // exactly (its own doc comment: "No cached-entry mutator... A full rescan is the simple,
            // always-correct choice"): RescanAll() there rebuilds every entry from scratch, so an
            // already-returned LibraryEntry reference (e.g. GetUpdateAsync's own `imported` local,
            // captured before this call) is never touched, while a FRESH read afterward (Reload(),
            // via GetAll()) sees the new name. An earlier version of this fake mutated the shared
            // instance's Name property directly, which aliased both cases together — production
            // never does that.
            var index = _entries.FindIndex(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            var old = _entries[index];
            _entries[index] = new LibraryEntry
            {
                FolderName = old.FolderName,
                Name = string.IsNullOrWhiteSpace(displayName) ? old.Name : displayName,
                Author = old.Author,
                Version = old.Version,
                Description = old.Description,
                FileName = old.FileName,
                VariantGroup = old.VariantGroup,
                Variant = old.Variant,
                VariantSort = old.VariantSort,
                IsOpaquePak = old.IsOpaquePak,
                IsPinned = old.IsPinned,
                IsFavorite = old.IsFavorite,
                Notes = old.Notes,
                ImportedAtUtc = old.ImportedAtUtc,
                Source = old.Source,
                NexusModId = old.NexusModId,
                CatalogEntryId = old.CatalogEntryId,
                DisplayNameOverride = displayName,
                IsLocallyEdited = old.IsLocallyEdited,
                ConvertedFromPrebuiltPak = old.ConvertedFromPrebuiltPak,
                MergedPackModNames = old.MergedPackModNames,
            };
        }

        public void LinkToNexus(string folderName, int nexusModId)
        {
            LinkToNexusCalls.Add((folderName, nexusModId));
            var entry = _entries.FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                entry.Source = "Nexus";
                entry.NexusModId = nexusModId;
            }
        }

        public void SetCatalogEntry(string folderName, string catalogEntryId)
        {
            SetCatalogEntryCalls.Add((folderName, catalogEntryId));
            var entry = _entries.FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                entry.CatalogEntryId = catalogEntryId;
                entry.Source = "Database";
            }
        }

        public string BackupMod(string folderName)
        {
            BackupModCalls.Add(folderName);
            var entry = _entries.FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                // Mirrors the real repository's own scope: a backup snapshots the mod's own folder
                // content only — never the sidecar Pin/Favorite/Notes/DisplayNameOverride/
                // CatalogEntryId metadata (that lives in a separate sidecar the backup never
                // touches) — so a restore alone must NOT bring those back; only an explicit
                // UpdateMetadata/SetDisplayNameOverride/SetCatalogEntry call after restoring does.
                _backups[folderName] = new LibraryEntry
                {
                    FolderName = entry.FolderName, Name = entry.Name, Author = entry.Author, Version = entry.Version,
                    Description = entry.Description, FileName = entry.FileName, IsOpaquePak = entry.IsOpaquePak,
                    ImportedAtUtc = entry.ImportedAtUtc,
                };
            }

            return $"{folderName}_backup.zip";
        }

        public bool HasModBackup(string folderName) => _backups.ContainsKey(folderName);

        public bool RestoreLatestModBackup(string folderName)
        {
            RestoreLatestModBackupCalls.Add(folderName);
            if (!_backups.TryGetValue(folderName, out var backup))
            {
                return false;
            }

            if (!_entries.Any(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase)))
            {
                _entries.Add(backup);
            }

            return true;
        }

        public string? TryGetLatestModBackupPath(string folderName) => _backups.ContainsKey(folderName) ? $"{folderName}_backup_path" : null;

        public LibraryEntry CreateBlankMod(string name, string author, ModTemplate template = ModTemplate.Blank) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public IReadOnlyList<string> ListAssetPaths(string folderName) => [];
        public IReadOnlyList<string> ListAssetPaths(string folderName, IReadOnlyList<string> precomputedFiles) => [];
        public byte[] ReadAssetContent(string folderName, string relativePath) => throw new NotSupportedException("Not exercised by these tests.");
        public string? ReadReadme(string folderName) => null;
        public string? ReadReadme(string folderName, IReadOnlyList<string> precomputedFiles) => null;
        public IReadOnlyList<string> ListFolderFiles(string folderName) => [];
        // Matches the real FolderLibraryRepository.GetFolderPath: a plain existence check against
        // the current entries, throwing (not returning a path anyway) for a folder name that isn't
        // one right now — e.g. because it was already Delete()d. An earlier version of this fake
        // returned a fabricated path unconditionally, which masked a real bug: LibraryViewModel's
        // ShowVersionComparisonAsync used to be called with a folder name that GetFolderPath can't
        // actually resolve after a reimport lands on a new folder name (see
        // GetUpdateCommand_SuccessfulUpdateAcceptsVersionCompare_LooksUpTheBackupUnderTheOldFolderNameNotTheNew's
        // own comment) — a test against the old, always-succeeding fake couldn't tell that apart
        // from IModVersionComparer's own always-throws fake behavior, since both produce the same
        // "Couldn't compare versions" status text.
        public string GetFolderPath(string folderName) =>
            !_entries.Any(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase))
                ? throw new DirectoryNotFoundException($"No library entry named '{folderName}'.")
                : FolderPathOverrides.TryGetValue(folderName, out var overridePath)
                    ? overridePath
                    : Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests.NonExistentModFolder", folderName);
    }

    private sealed class FakeUe4ssModRepository : IUe4ssModRepository
    {
        public string Result { get; set; } = "SomeMod_1";
        public int ImportFromFolderCallCount { get; private set; }
        public string? LastSourceFolder { get; private set; }
        public string? LastFallbackName { get; private set; }

        public string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null)
        {
            ImportFromFolderCallCount++;
            LastSourceFolder = sourceFolder;
            LastFallbackName = fallbackName;
            return Result;
        }

        public IReadOnlyList<string> GetAll() => [];
        public string Import(string zipFilePath, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");
        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) => throw new NotSupportedException("Not exercised by these tests.");
        public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUe4ssModStateService : IUe4ssModStateService
    {
        public IReadOnlyList<Ue4ssModState> GetAll(string gameModsFolderPath) =>
            throw new NotSupportedException("Not exercised by these tests — IcarusContentPath stays unset, so ReloadInstalledUe4ssMods returns before reaching this.");
        public IReadOnlyList<Ue4ssModApplyFailure> Apply(string gameModsFolderPath, IReadOnlyDictionary<string, bool> desiredEnabledByName, string backupDirectory) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUe4ssModMetaStore : IUe4ssModMetaStore
    {
        public Ue4ssModMeta Load(string folderName) => new();
        public void Save(string folderName, Ue4ssModMeta meta) { }
        public void Delete(string folderName) { }
    }

    private sealed class FakeUe4ssLoaderInstallService : IUe4ssLoaderInstallService
    {
        public Ue4ssLoaderStatus GetStatus(string icarusContentPath) => throw new NotSupportedException("Not exercised by these tests.");
        public Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListUserAddedMods(string icarusContentPath) => throw new NotSupportedException("Not exercised by these tests.");
        public bool IsFrameworkOwned(string icarusContentPath, string modName) => throw new NotSupportedException("Not exercised by these tests.");
        public Task<Ue4ssUninstallResult> UninstallAsync(string icarusContentPath, string stagedModsDirectory, string backupDirectory, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public bool Save() => true;
    }

    private sealed class FakeUnrealPakService : IUnrealPakService
    {
        public Task<int> ExtractPakAsync(
            string unrealPakExePath, string pakFilePath, string outputDirectory,
            CancellationToken cancellationToken = default, string? filter = null) =>
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

    private sealed class FakeUassetTextureDecoder : IUassetTextureDecoder
    {
        public byte[]? TryDecodeToPng(string modFolderPath, string relativeAssetPath) =>
            throw new NotSupportedException("Not exercised by these tests — no test selects an asset.");
    }

    private sealed class FakeUassetStaticMeshDecoder : IUassetStaticMeshDecoder
    {
        public StaticMeshGeometry? TryDecodeStaticMesh(string modFolderPath, string relativeAssetPath) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUassetSkeletalMeshDecoder : IUassetSkeletalMeshDecoder
    {
        public StaticMeshGeometry? TryDecodeSkeletalMesh(string modFolderPath, string relativeAssetPath) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUassetSoundDecoder : IUassetSoundDecoder
    {
        public UassetSoundAudio? TryDecodeAudio(string modFolderPath, string relativeAssetPath) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUassetMaterialDecoder : IUassetMaterialDecoder
    {
        public UassetMaterialParams? TryDecodeMaterial(string modFolderPath, string relativeAssetPath) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeOpaquePakAssetPreviewService : IOpaquePakAssetPreviewService
    {
        public Task<OpaquePakAssetPreviewResult> PreviewAssetAsync(
            string unrealPakExePath, string pakFilePath, string relativeAssetPath, string cacheDirectory, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
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

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public void Save(string target, string secret) { }
        public string? Read(string target) => null;
        public void Delete(string target) { }
    }

    private sealed class FakeActivityLog : IActivityLog
    {
        public System.Collections.ObjectModel.ObservableCollection<ActivityEntry> Entries { get; } = [];
        public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info) =>
            Entries.Insert(0, new ActivityEntry(message, kind, DateTimeOffset.Now));
    }

    private sealed class FakePendingDownloadStore : IPendingDownloadStore
    {
        public List<PendingDownloadEntry> EntriesList { get; } = [];
        public IReadOnlyList<PendingDownloadEntry> Entries => EntriesList;
        public void Add(PendingDownloadEntry entry) => EntriesList.Add(entry);
        public void Remove(int modId, int fileId) => EntriesList.RemoveAll(e => e.ModId == modId && e.FileId == fileId);
        public void SetActivation(int modId, int fileId, string? folderName, PendingDownloadActivationKind? kind) { }
    }

    private sealed class FakeModVersionComparer : IModVersionComparer
    {
        public Task<ModVersionCompareResult> CompareAsync(string oldFolderPath, string newFolderPath, string? unrealPakExePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests — every test declines the post-update 'see what changed?' prompt.");
    }

    private sealed class FakePrebuiltPakImporter(FakeLibraryRepository repository) : IPrebuiltPakImporter
    {
        public LibraryEntry Result { get; set; } = new()
        {
            FolderName = "Pak", Name = "Pak", Author = "Unknown", Version = "1.0", Description = "", FileName = "Pak", IsOpaquePak = true,
        };

        public int ImportCallCount { get; private set; }
        public string? LastPakFilePath { get; private set; }

        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default)
        {
            ImportCallCount++;
            LastPakFilePath = pakFilePath;
            repository.Seed(Result);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePrebuiltPakToExmodConverter : IPrebuiltPakToExmodConverter
    {
        public Task<PrebuiltPakConversionResult?> TryConvertAsync(
            string pakFilePath, string dataFolder, string unrealPakExePath, string name, string author, MergeReport report, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakePrebuiltPakSourceStore : IPrebuiltPakSourceStore
    {
        public List<string> DeleteCalls { get; } = [];
        private readonly Dictionary<string, string> _saved = [];
        public void Save(string folderName, string pakFilePath) => _saved[folderName] = pakFilePath;
        public string? TryGetPath(string folderName) => _saved.GetValueOrDefault(folderName);
        public void Delete(string folderName) => DeleteCalls.Add(folderName);
    }

    private sealed class FakeDaedalusClient : IDaedalusCatalogClient
    {
        public IReadOnlyList<CatalogEntry> Result { get; set; } = [];
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class FakeJimk72Client : IJimk72CatalogClient
    {
        public IReadOnlyList<CatalogEntry> Result { get; set; } = [];
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class FakeGitHubRepoDateClient : IGitHubRepoDateClient
    {
        public Task<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>> FetchPushedDatesAsync(
            IReadOnlyCollection<(string Owner, string Repo)> repos, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>>(new Dictionary<(string, string), DateTimeOffset>());
    }

    private sealed class FakeDialogService(bool confirmResult = true) : IDialogService
    {
        public int ConfirmCallCount { get; private set; }
        public List<string> ConfirmMessages { get; } = [];
        public RenamePromptResult PromptRenameResult { get; set; } = new(Cancelled: true, NewDisplayName: null);
        public int PromptRenameCallCount { get; private set; }
        public string? LastPromptRenameCurrentName { get; private set; }

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            ConfirmCallCount++;
            ConfirmMessages.Add(message);
            return confirmResult;
        }

        public RenamePromptResult PromptRename(
            string currentName, string description = "", string? resetValue = null, string resetLabel = "",
            string resetTooltip = "", string title = "", string fieldLabel = "")
        {
            PromptRenameCallCount++;
            LastPromptRenameCurrentName = currentName;
            return PromptRenameResult;
        }
    }
}
