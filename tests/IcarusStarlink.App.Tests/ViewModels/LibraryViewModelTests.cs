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
[Collection(MessengerCollection.Name)]
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
        Directory.CreateDirectory(Path.Combine(folderPath, "Extracted Mods"));
        File.WriteAllText(Path.Combine(folderPath, "Extracted Mods", "ImportedMod.EXMOD"), "{}");
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

    /// <summary>Audit finding: a dropped FOLDER holding a bare .pak (no .EXMOD) failed with "No .EXMOD file found" instead of getting the same content sniffing an archive gets.</summary>
    [Fact]
    public async Task ImportPaths_FolderHoldingABarePak_ImportsThroughThePrebuiltPakImporter()
    {
        var harness = new TestHarness();
        var folderPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folderPath);
        File.WriteAllBytes(Path.Combine(folderPath, "Cool_P.pak"), [1, 2, 3, 4]);
        try
        {
            harness.PrebuiltPakImporter.Result = new LibraryEntry
            {
                FolderName = "Cool_P", Name = "Cool", Author = "Unknown", Version = "1.0", Description = "", FileName = "Cool_P", IsOpaquePak = true,
            };
            var vm = harness.Build();

            await vm.ImportPaths([folderPath]);

            Assert.Equal(1, harness.PrebuiltPakImporter.ImportCallCount);
            Assert.Empty(harness.Repository.ImportCalls);
            Assert.True(File.Exists(Path.Combine(folderPath, "Cool_P.pak")), "the user's own folder must be left exactly as it was");
            Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "Cool_P");
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
        Directory.CreateDirectory(Path.Combine(goodFolder, "Extracted Mods"));
        File.WriteAllText(Path.Combine(goodFolder, "Extracted Mods", "GoodMod.EXMOD"), "{}");
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

    /// <summary>The detail window can show a different mod than the list's selection (Prev/Next): its Delete removes the mod it shows, not the selection.</summary>
    [Fact]
    public void DeleteOneItemCommand_DeletesTheGivenModEvenWhenAnotherIsSelected()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        harness.AddMod("ModB");
        var vm = harness.Build();
        var modA = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        var modB = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModB");
        vm.SetBulkSelectionAnchor(modA);
        Assert.Contains(modA, vm.BulkSelectedItems);

        vm.DeleteOneItemCommand.Execute(modB);

        Assert.Equal("ModB", Assert.Single(harness.Repository.DeleteCalls));
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

    /// <summary>
    /// One Nexus download that landed as a Library mod (its paks) PLUS a Lua mod on the UE4SS tab
    /// (WirelessPower; both carry Nexus mod ID 361) is deleted whole, with one confirmation that
    /// names both halves — half a mod is a broken mod, and the UE4SS tab's own Delete routes here too.
    /// </summary>
    [Fact]
    public void DeleteItemCommand_OnAModWithALuaHalf_DeletesBothHalvesAndNamesThemInTheConfirmation()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("WirelessPower", name: "Wireless Power", source: "Nexus", nexusModId: 361, isOpaquePak: true);
        harness.AddMod("Unrelated", source: "Nexus", nexusModId: 999);
        harness.Ue4ssModRepository.Staged.Add("WirelessPower");
        harness.Ue4ssModMetaStore.Metas["WirelessPower"] = new Ue4ssModMeta { NexusModId = 361, NexusName = "Wireless Power" };
        var vm = harness.Build();
        var mod = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "WirelessPower");
        Assert.Equal(["WirelessPower"], mod.LinkedUe4ssNames);
        Assert.Equal("Pak + UE4SS", mod.TypeLabel);
        Assert.Empty(vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Unrelated").LinkedUe4ssNames);

        vm.DeleteItemCommand.Execute(mod);

        var question = Assert.Single(harness.DialogService.ConfirmMessages);
        Assert.Contains("Delete 'Wireless Power' and all 2 of its parts?", question);
        Assert.Contains("Wireless Power (Library)", question);
        Assert.Contains("WirelessPower (UE4SS mod)", question);
        Assert.Equal(["WirelessPower"], harness.Repository.DeleteCalls);
        Assert.Equal(["WirelessPower"], harness.Ue4ssModRepository.DeleteCalls);
        Assert.Equal(1, vm.ModCount);
        Assert.Contains("Deleted 'Wireless Power' (2 parts)", vm.StatusMessage);
        Assert.Contains("Run Merge & Install", vm.StatusMessage);
    }

    /// <summary>
    /// Two separate files from one Nexus page (a main file with a Lua half, and an optional pak)
    /// are two downloads: the optional pak isn't part of the main file's package, so deleting it
    /// leaves the main file and its Lua half alone (packages used to be keyed by mod ID only).
    /// </summary>
    [Fact]
    public void DeleteItemCommand_OnAnotherFileOfTheSameNexusMod_DeletesOnlyThatFile()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("Main", source: "Nexus", nexusModId: 361, isOpaquePak: true).NexusFileId = 10;
        harness.AddMod("Optional", source: "Nexus", nexusModId: 361, isOpaquePak: true).NexusFileId = 11;
        harness.Ue4ssModRepository.Staged.Add("MainLua");
        harness.Ue4ssModMetaStore.Metas["MainLua"] = new Ue4ssModMeta { NexusModId = 361, NexusFileId = 10 };
        var vm = harness.Build();
        var main = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Main");
        var optional = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Optional");
        Assert.Equal(["MainLua"], main.LinkedUe4ssNames);
        Assert.Empty(optional.LinkedUe4ssNames);

        vm.DeleteItemCommand.Execute(optional);

        Assert.Equal(["Optional"], harness.Repository.DeleteCalls);
        Assert.Empty(harness.Ue4ssModRepository.DeleteCalls);
    }

    /// <summary>A part recorded before file IDs were kept still goes with its mod's one download.</summary>
    [Fact]
    public void Packages_PartWithoutAFileId_JoinsItsModsOnlyDownload()
    {
        var harness = new TestHarness();
        harness.AddMod("WirelessPower", source: "Nexus", nexusModId: 361, isOpaquePak: true);
        harness.Ue4ssModRepository.Staged.Add("WirelessPower");
        harness.Ue4ssModMetaStore.Metas["WirelessPower"] = new Ue4ssModMeta { NexusModId = 361, NexusFileId = 10 };
        var vm = harness.Build();

        Assert.Equal(["WirelessPower"], vm.RootItems.OfType<LibraryItemViewModel>().Single().LinkedUe4ssNames);
    }

    /// <summary>Delete, Restore latest backup and Reinstall original refuse while the mod is open in the editor (its next Save would write the old copy back), like the fix window.</summary>
    [Fact]
    public void DestructiveCommands_WhileTheModIsOpenInTheEditor_RefuseWithoutAsking()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single();
        vm.MarkFolderEditorOpenForTesting("ModA");

        vm.DeleteItemCommand.Execute(item);
        Assert.Contains("open in the mod editor", vm.StatusMessage);
        vm.StatusMessage = null;
        vm.RestoreModBackupCommand.Execute(item);
        Assert.Contains("open in the mod editor", vm.StatusMessage);

        Assert.Empty(harness.DialogService.ConfirmMessages);
        Assert.Empty(harness.Repository.DeleteCalls);
        Assert.Empty(harness.Repository.RestoreLatestModBackupCalls);
        Assert.Equal(1, vm.ModCount);
    }

    /// <summary>Clearing the Content path empties the right panel (it used to keep the old rows, and their selection).</summary>
    [Fact]
    public void ReloadInstalledUe4ssMods_ContentPathCleared_EmptiesTheUe4ssPanelAndItsSelection()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("ArcFissionLibrary", IsEnabled: true));
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var row = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single();
        vm.SetBulkSelectionAnchor(row);

        harness.SettingsService.Current.IcarusContentPath = null;
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        Assert.Empty(vm.Ue4ssRootItems);
        Assert.Empty(vm.BulkSelectedUe4ssRows);
        Assert.False(row.IsSelectedForBulk);
        Assert.Equal("Set the Icarus Content folder in Settings first.", vm.Ue4ssEmptyText);
    }

    // ---------------------------------------------------------------------------------------
    // The single Mods list: UE4SS rows live in the same list as pak/EXMOD rows, a Lua half is
    // folded into its Mods-tab row, every row has a Type, a Status and the same "On" checkbox.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Reload_WithUe4ssMods_ListsThemBesidePakRowsAndFoldsALuaHalfIntoItsPakRow()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.AddMod("WirelessPower", name: "Wireless Power", source: "Nexus", nexusModId: 361, isOpaquePak: true);
        harness.AddMod("Steadfast", name: "Steadfast Structures");
        harness.Ue4ssModRepository.Staged.Add("WirelessPower");
        harness.Ue4ssModMetaStore.Metas["WirelessPower"] = new Ue4ssModMeta { NexusModId = 361, NexusName = "Wireless Power" };
        harness.Ue4ssModMetaStore.Metas["ArcFissionLibrary"] = new Ue4ssModMeta { NexusModId = 356, NexusName = "ArcFission Library", NexusVersion = "1" };
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("ArcFissionLibrary", IsEnabled: true));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("WirelessPower", IsEnabled: false));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("ConsoleEnablerMod", IsEnabled: true));
        harness.Ue4ssLoaderInstallService.BuiltIns.Add("ConsoleEnablerMod");
        harness.SettingsService.Current.LibraryShowUe4ssBuiltIns = true;
        var vm = harness.Build();

        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        var ue4ssRows = vm.RootItems.OfType<Ue4ssModRowViewModel>().Select(r => r.Name).ToList();
        Assert.Equal(["ArcFissionLibrary", "ConsoleEnablerMod"], ue4ssRows);
        Assert.DoesNotContain("WirelessPower", ue4ssRows);

        var wireless = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "WirelessPower");
        Assert.Equal("Pak + UE4SS", wireless.TypeLabel);
        Assert.Equal(["WirelessPower"], wireless.LinkedUe4ssNames);
        Assert.Equal("EXMOD", vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Steadfast").TypeLabel);

        var arcFission = vm.RootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "ArcFissionLibrary");
        Assert.Equal("UE4SS", arcFission.TypeLabel);
        Assert.Equal("In game", arcFission.InstallStatus);
        Assert.True(arcFission.IsInGame);
        Assert.False(vm.HasPendingChanges);

        // Two panels (2026-10-23): data mods on the left, UE4SS mods on the right, each by name.
        Assert.Equal(["Steadfast Structures", "Wireless Power"], vm.DataRootItems.OfType<LibraryItemViewModel>().Select(i => i.Name));
        Assert.Equal(["ArcFissionLibrary", "ConsoleEnablerMod"], vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Select(r => r.Name));
    }

    /// <summary>UE4SS's own built-ins (BPModLoaderMod, Keybinds…) cluttered the list with bare "v" versions — hidden by default, one remembered toggle brings them back.</summary>
    [Fact]
    public void Ue4ssBuiltIns_HiddenByDefault_ToggleShowsThemAndIsRemembered()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("ArcFissionLibrary", IsEnabled: true));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("BPModLoaderMod", IsEnabled: true));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("Keybinds", IsEnabled: true));
        harness.Ue4ssLoaderInstallService.BuiltIns.Add("BPModLoaderMod");
        harness.Ue4ssLoaderInstallService.BuiltIns.Add("Keybinds");
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        Assert.False(vm.ShowUe4ssBuiltIns);
        Assert.Equal(["ArcFissionLibrary"], vm.RootItems.OfType<Ue4ssModRowViewModel>().Select(r => r.Name));
        Assert.Equal("Show UE4SS built-ins (2)", vm.ShowUe4ssBuiltInsLabel);

        vm.ShowUe4ssBuiltIns = true;

        Assert.Equal(["ArcFissionLibrary", "BPModLoaderMod", "Keybinds"], vm.RootItems.OfType<Ue4ssModRowViewModel>().Select(r => r.Name));
        Assert.True(harness.SettingsService.Current.LibraryShowUe4ssBuiltIns);
        Assert.Equal("", vm.RootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "Keybinds").VersionText);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("1.2.0", "v1.2.0")]
    [InlineData("v1.0", "v1.0")]
    [InlineData("Beta", "Beta")]
    public void VersionLabel_NeverABareV(string? version, string expected) =>
        Assert.Equal(expected, IcarusStarlink.App.Utilities.VersionLabel.For(version));

    [Fact]
    public void UntickingAUe4ssRow_ShowsNeedsApplyAndThePendingBar()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("ArcFissionLibrary", IsEnabled: true));
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var row = vm.RootItems.OfType<Ue4ssModRowViewModel>().Single();

        row.IsEnabled = false;

        Assert.Equal("Needs Apply", row.InstallStatus);
        Assert.True(row.NeedsAction);
        Assert.True(vm.HasPendingChanges);
        Assert.True(vm.HasPendingUe4ssChanges);
        Assert.Contains("1 UE4SS change", vm.PendingChangesSummary);
    }

    [Fact]
    public void TypeFilter_NarrowsTheListToOneKind()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.AddMod("Steadfast");
        harness.AddMod("SomePak", isOpaquePak: true);
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("ArcFissionLibrary", IsEnabled: true));
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        Assert.Equal(3, vm.RootItems.Count);

        vm.TypeFilter = "UE4SS";
        Assert.Single(vm.RootItems.OfType<Ue4ssModRowViewModel>());
        Assert.Empty(vm.RootItems.OfType<LibraryItemViewModel>());

        vm.TypeFilter = "Pak";
        Assert.Equal(["SomePak"], vm.RootItems.OfType<LibraryItemViewModel>().Select(i => i.FolderName).ToList());
        Assert.Empty(vm.RootItems.OfType<Ue4ssModRowViewModel>());

        vm.TypeFilter = "EXMOD";
        Assert.Equal(["Steadfast"], vm.RootItems.OfType<LibraryItemViewModel>().Select(i => i.FolderName).ToList());

        vm.TypeFilter = "All";
        Assert.Equal(3, vm.RootItems.Count);
    }

    /// <summary>The "On" checkbox: a user tick raises EnabledToggled (the view model turns that into a merge-queue change); a tick set FROM the queue must not, or the two would loop.</summary>
    [Fact]
    public void PakRowOnCheckbox_UserTickRaisesEnabledToggled_QueueSyncDoesNot()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", isOpaquePak: true);
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single();
        var raised = new List<bool>();
        item.EnabledToggled += (_, enabled) => raised.Add(enabled);

        item.IsEnabled = true;
        item.SetEnabledFromQueue(false);
        item.SetEnabledFromQueue(true);
        item.IsEnabled = false;

        Assert.Equal([true, false], raised);
        Assert.Equal("Not enabled", item.InstallStatus);
        Assert.Equal("Pak", item.TypeLabel);
    }

    /// <summary>A ticked UE4SS mod with no UE4SS in the game: the bar under the list says so and offers the install, and the row's status says why.</summary>
    [Fact]
    public void Ue4ssLoaderMissing_WarnsOnlyWhileAUe4ssModIsTicked()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.Ue4ssLoaderInstallService.Installed = false;
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("NearbyCrafting", IsEnabled: false));
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var row = vm.RootItems.OfType<Ue4ssModRowViewModel>().Single();

        Assert.False(vm.ShowUe4ssLoaderMissing); // nothing ticked: nothing to warn about
        row.IsEnabled = true;

        Assert.True(vm.ShowUe4ssLoaderMissing);
        Assert.Equal("Needs UE4SS", row.InstallStatus);
        Assert.True(row.NeedsAction);

        harness.Ue4ssLoaderInstallService.Installed = true;
        WeakReferenceMessenger.Default.Send(new Ue4ssLoaderChangedMessage("UE4SS v3.0.1 is installed in the game."));

        Assert.False(vm.ShowUe4ssLoaderMissing);
        Assert.Equal("UE4SS v3.0.1 is installed in the game.", vm.Ue4ssStatusMessage);
        Assert.Equal("Needs Apply", vm.RootItems.OfType<Ue4ssModRowViewModel>().Single().InstallStatus);
    }

    // ---- Per-file updates: a Nexus mod with several variant files (Deyvid's All-in-One, mod 317) ----

    private static NexusModFile NexusFile(int id, string name, string version) => new(id, $"{id}.zip", name, version, "OPTIONAL", false);

    /// <summary>Vanilla Weight 1.0.8 (file 100) was replaced by 1.0.9 (file 101); the Without Level Progression file (200) is still 1.0.5, its own latest.</summary>
    private static NexusModFileSet AllInOneFiles() => new(
        [NexusFile(100, "All In One Vanilla Weight", "1.0.8"), NexusFile(101, "All In One Vanilla Weight", "1.0.9"),
         NexusFile(200, "All In One 75 Weight Without Level Progression", "1.0.5")],
        [new NexusFileUpdate(100, 101)]);

    [Fact]
    public async Task CheckForUpdates_TrackedFiles_AreJudgedByTheirOwnFileNotTheModsHeadlineVersion()
    {
        var harness = new TestHarness();
        harness.CredentialStore.ApiKey = "key";
        harness.NexusApiClient.FileSets[317] = AllInOneFiles();
        harness.AddMod("Vanilla", source: "Nexus", nexusModId: 317, version: "1.0.8", isOpaquePak: true).NexusFileId = 100;
        harness.AddMod("NoLevel", source: "Nexus", nexusModId: 317, version: "1.0.5", isOpaquePak: true).NexusFileId = 200;
        var vm = harness.Build();

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        var vanilla = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Vanilla");
        var noLevel = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "NoLevel");
        Assert.True(vanilla.HasUpdateAvailable);
        Assert.Equal("1.0.9", vanilla.LatestVersion);
        Assert.Equal(101, vanilla.LatestNexusFileId);
        Assert.False(noLevel.HasUpdateAvailable); // 1.0.5 is that file's latest, whatever the other variants are at
        Assert.Equal("1.0.5", noLevel.LatestVersion);
        Assert.Contains("1 mod(s) have an update", vm.UpdateCheckStatusMessage);
    }

    [Fact]
    public async Task CheckForUpdates_TrackedUe4ssMod_FollowsItsOwnFile()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.CredentialStore.ApiKey = "key";
        harness.NexusApiClient.FileSets[317] = AllInOneFiles();
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("AllInOneLua", IsEnabled: true));
        harness.Ue4ssModMetaStore.Metas["AllInOneLua"] = new Ue4ssModMeta { NexusModId = 317, NexusVersion = "1.0.8", NexusFileId = 100 };
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        var row = vm.RootItems.OfType<Ue4ssModRowViewModel>().Single();
        Assert.Equal(100, row.NexusFileId);
        Assert.True(row.HasUpdateAvailable);
        Assert.Equal("1.0.9", row.LatestVersion);
    }

    /// <summary>Downloading an update fetches the newer version of the file you have, without asking which file — but only when that is unambiguous.</summary>
    [Fact]
    public void NewerVersionOfInstalledFile_PicksTheInstalledFilesSuccessorOnly()
    {
        var harness = new TestHarness();
        var vanilla = harness.AddMod("Vanilla", source: "Nexus", nexusModId: 317);
        vanilla.NexusFileId = 100;
        var vm = harness.Build();

        Assert.Equal(101, vm.Downloads.NewerVersionOfInstalledFile(317, AllInOneFiles())!.FileId);

        vanilla.NexusFileId = 101; // already the newest
        Assert.Null(vm.Downloads.NewerVersionOfInstalledFile(317, AllInOneFiles()));

        vanilla.NexusFileId = 100;
        harness.AddMod("NoLevel", source: "Nexus", nexusModId: 317).NexusFileId = 200; // two variants: no single answer
        Assert.Null(vm.Downloads.NewerVersionOfInstalledFile(317, AllInOneFiles()));

        Assert.Null(vm.Downloads.NewerVersionOfInstalledFile(999, AllInOneFiles())); // nothing installed from that mod
    }

    private static (LibraryViewModel Vm, TestHarness Harness) BuildMixedList()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.AddMod("Steadfast");
        harness.AddMod("SomePak", isOpaquePak: true);
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("ArcFissionLibrary", IsEnabled: false));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("NearbyCrafting", IsEnabled: false));
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        return (vm, harness);
    }

    // ---------------------------------------------------------------------------------------
    // Two panels (2026-10-23): filter chips, linked halves, each panel's pending bar.
    // ---------------------------------------------------------------------------------------

    private static (LibraryViewModel Vm, TestHarness Harness) BuildPanels()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.AddMod("MkHaters_Bags", name: "MkHaters Bags", isFavorite: true);
        harness.AddMod("Minimap", name: "Minimap & Map Icons");
        harness.AddMod("Steadfast", name: "Steadfast Structures");
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("MkHatersBags", IsEnabled: true));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("MiniMapa", IsEnabled: true));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("ArcFissionLibrary", IsEnabled: false));
        harness.Ue4ssModMetaStore.Metas["MiniMapa"] = new Ue4ssModMeta { NexusModId = 11, NexusName = "Minimap and Map Icons" };
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        return (vm, harness);
    }

    private static int ChipCount(LibraryViewModel vm, string key) => vm.FilterChips.Single(c => c.Key == key).Count;

    [Fact]
    public void FilterChips_CountWhatEachWouldShow_AndNarrowBothPanels()
    {
        var (vm, _) = BuildPanels();

        // No merge queue in this harness, so every data mod is off; two UE4SS mods are on.
        Assert.Equal(6, ChipCount(vm, LibraryViewModel.FilterAll));
        Assert.Equal(2, ChipCount(vm, LibraryViewModel.FilterOn));
        Assert.Equal(4, ChipCount(vm, LibraryViewModel.FilterOff));
        Assert.Equal(1, ChipCount(vm, LibraryViewModel.FilterFavourites));

        vm.SelectFilterCommand.Execute(LibraryViewModel.FilterOn);
        Assert.Empty(vm.DataRootItems);
        Assert.Equal(["MiniMapa", "MkHatersBags"], vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Select(r => r.Name));
        Assert.True(vm.FilterChips.Single(c => c.Key == LibraryViewModel.FilterOn).IsSelected);
        Assert.Equal(2, ChipCount(vm, LibraryViewModel.FilterOn)); // counts don't shrink to the filter

        vm.SelectFilterCommand.Execute(LibraryViewModel.FilterFavourites);
        Assert.Equal(["MkHaters Bags"], vm.DataRootItems.OfType<LibraryItemViewModel>().Select(i => i.Name));
        Assert.Empty(vm.Ue4ssRootItems);

        // The chip that's on, clicked again, goes back to All.
        vm.SelectFilterCommand.Execute(LibraryViewModel.FilterFavourites);
        Assert.Equal(LibraryViewModel.FilterAll, vm.ListFilter);
        Assert.Equal(3, vm.DataShownCount);
        Assert.Equal(3, vm.Ue4ssShownCount);
    }

    /// <summary>A mod with both halves stays two rows, one per panel; each knows the other (same name without spaces, or its Nexus title).</summary>
    [Fact]
    public void LinkedHalves_PairByNameOrNexusTitle_AndStayTwoRows()
    {
        var (vm, _) = BuildPanels();
        var bags = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.Name == "MkHaters Bags");
        var luaBags = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MkHatersBags");
        var minimap = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.Name == "Minimap & Map Icons");
        var miniMapa = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MiniMapa");

        Assert.Same(luaBags, bags.LinkedPartner);
        Assert.Same(bags, luaBags.LinkedPartner);
        Assert.Same(miniMapa, minimap.LinkedPartner);
        Assert.Null(vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.Name == "Steadfast Structures").LinkedPartner);
        Assert.Null(vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "ArcFissionLibrary").LinkedPartner);
        Assert.Equal("EXMOD", bags.TypeLabel); // not folded into a "Pak + UE4SS" row
    }

    [Fact]
    public void Ue4ssPanelPendingBar_NamesWhatChanged_AndTurnOffSelectedActsOnThatPanelOnly()
    {
        var (vm, _) = BuildPanels();
        Assert.False(vm.HasPendingUe4ssChanges);

        var luaBags = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MkHatersBags");
        var steadfast = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.Name == "Steadfast Structures");
        vm.SetBulkSelectionAnchor(luaBags);
        vm.ToggleBulkSelection(steadfast);
        vm.TurnOnSelectedCommand.Execute("ue4ss"); // already on: nothing changes
        Assert.False(vm.HasPendingUe4ssChanges);

        vm.TurnOffSelectedCommand.Execute("ue4ss");

        Assert.True(vm.HasPendingUe4ssChanges);
        Assert.Equal("MkHatersBags turned off", vm.PendingUe4ssText);
        Assert.False(steadfast.IsEnabled);
        Assert.True(steadfast.IsSelectedForBulk); // the data panel's selection stays as it was
    }

    [Theory]
    [InlineData("Import failed: access denied", true)]
    [InlineData("Couldn't open the folder: gone", true)]
    [InlineData("Imported 'MkHaters Bags'.", false)]
    [InlineData("Applied 1 change(s).", false)]
    public void Notice_FailuresShowAsWarnings(string message, bool isWarning) =>
        Assert.Equal(isWarning, LibraryViewModel.LooksLikeAProblem(message));

    /// <summary>The two panels are separate lists: a Shift-click range never runs from a data mod across into the UE4SS panel.</summary>
    [Fact]
    public void ShiftClickRange_StaysInsideItsPanel()
    {
        var (vm, _) = BuildMixedList();

        vm.SetBulkSelectionAnchor(vm.DataRootItems[0]);
        vm.SelectBulkRange(vm.Ue4ssRootItems[^1]);

        Assert.Equal(1, vm.BulkSelectedCount);
        Assert.True(((Ue4ssModRowViewModel)vm.Ue4ssRootItems[^1]).IsSelectedForBulk);
        Assert.False(vm.HasDataSelection);
    }

    /// <summary>One checkbox per row: clicking selects (grey), Shift-click ranges within a panel, Ctrl-click adds rows from the other panel, and ticking ON on a selected row ticks every selected row.</summary>
    [Fact]
    public void ClickSelection_SpansBothKinds_AndOneOnTickAppliesToAllSelected()
    {
        var (vm, _) = BuildMixedList();
        Assert.Equal(4, vm.RootItems.Count);

        vm.SetBulkSelectionAnchor(vm.DataRootItems[0]);
        Assert.Equal(1, vm.BulkSelectedCount);
        vm.SelectBulkRange(vm.DataRootItems[^1]);
        vm.ToggleBulkSelection(vm.Ue4ssRootItems[0]);
        vm.ToggleBulkSelection(vm.Ue4ssRootItems[^1]);

        Assert.Equal(4, vm.BulkSelectedCount);
        Assert.Equal(2, vm.BulkSelectedPakCount);
        Assert.True(vm.IsAllSelectedForBulk);
        Assert.All(vm.RootItems.OfType<Ue4ssModRowViewModel>(), r => Assert.True(r.IsSelectedForBulk));

        var steadfast = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Steadfast");
        steadfast.IsEnabled = true; // what the checkbox's binding does on the click
        var others = vm.ApplyEnabledToSelection(steadfast, true);

        // Every other selected row was set (paks go through the merge queue, which this harness
        // doesn't have, so only the UE4SS rows can be checked here).
        Assert.Equal(3, others);
        Assert.All(vm.RootItems.OfType<Ue4ssModRowViewModel>(), r => Assert.True(r.IsEnabled));
        Assert.True(vm.HasPendingUe4ssChanges);
    }

    [Fact]
    public void OnTick_OnARowOutsideTheSelection_ChangesOnlyThatRow()
    {
        var (vm, _) = BuildMixedList();
        var arc = vm.RootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "ArcFissionLibrary");
        var nearby = vm.RootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "NearbyCrafting");
        vm.ToggleBulkSelection(arc);
        vm.ToggleBulkSelection(vm.RootItems.OfType<LibraryItemViewModel>().First());

        nearby.IsEnabled = true;
        Assert.Equal(0, vm.ApplyEnabledToSelection(nearby, true));

        Assert.False(arc.IsEnabled);
        Assert.Equal(2, vm.BulkSelectedCount);
    }

    [Fact]
    public void Ue4ssSelection_SurvivesAReloadOfTheUe4ssRows()
    {
        var (vm, _) = BuildMixedList();
        vm.ToggleBulkSelection(vm.RootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "NearbyCrafting"));

        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        var fresh = vm.RootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "NearbyCrafting");
        Assert.True(fresh.IsSelectedForBulk);
        Assert.Same(fresh, Assert.Single(vm.BulkSelectedUe4ssRows));
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

    /// <summary>A row reused through a refresh: converting a pak to an EXMOD must update the Type column, and linking it to Nexus must re-check Endorse/Abstain (they stayed greyed out).</summary>
    [Fact]
    public void RefreshCommand_ConvertedAndLinkedMod_NotifiesTypeLabelAndEndorseCanExecute()
    {
        var harness = new TestHarness();
        var entry = harness.AddMod("ModA", isOpaquePak: true);
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        Assert.Equal("Pak", item.TypeLabel);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var endorseRechecked = false;
        item.EndorseCommand.CanExecuteChanged += (_, _) => endorseRechecked = true;

        entry.IsOpaquePak = false;
        entry.NexusModId = 42;
        vm.RefreshCommand.Execute(null);

        Assert.Same(item, vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA"));
        Assert.Contains(nameof(LibraryItemViewModel.TypeLabel), changed);
        Assert.Equal("EXMOD", item.TypeLabel);
        Assert.True(endorseRechecked);
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
    // "Check mods against game data" only looks; fixes happen through the fix window or
    // Check ▾ › Fix sure problems in every mod. (It used to rename rows silently, which renamed
    // mods' own new rows to similar game rows: Homestead's Water_Well became the game's Waterwheel.)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckModsAgainstCurrentData_OnlyLooks_TheRenameIsOfferedAsASureFix()
    {
        var harness = new TestHarness();
        const string folderName = "RenamedRowMod";
        var modFolderPath = CreateStaleAutoFixableModOnDisk(harness, folderName);
        try
        {
            var vm = harness.Build();

            await vm.CheckModsAgainstCurrentDataCommand.ExecuteAsync(null);

            Assert.Equal(StaleItemName, Assert.Single(Assert.Single(ExmodFolder.ReadPackageOnly(modFolderPath).Rows).FileItems).Name);
            Assert.Empty(harness.Repository.BackupModCalls);
            var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == folderName);
            Assert.True(item.HasModIssues);
            var rename = Assert.Single(item.Health!.Issues);
            Assert.Equal(ModIssueKind.MissingRow, rename.Kind);
            Assert.True(rename.IsSure);
            Assert.Equal(FixedItemName, rename.SuggestedRow);

            await vm.FixSureProblemsCommand.ExecuteAsync(null);

            Assert.Equal(FixedItemName, Assert.Single(Assert.Single(ExmodFolder.ReadPackageOnly(modFolderPath).Rows).FileItems).Name);
            Assert.Contains(folderName, harness.Repository.BackupModCalls);
            Assert.False(item.HasModIssues);
        }
        finally
        {
            Directory.Delete(modFolderPath, recursive: true);
        }
    }

    /// <summary>
    /// Fix sure problems tells the rest of the app the mods changed (like Apply and the editor's
    /// Save), and the row's ✎ comes from the repository's entry rather than being forced on: a
    /// converted prebuilt pak isn't marked edited by a fix (this fake never marks anything edited).
    /// </summary>
    [Fact]
    public async Task FixSureProblems_SendsLibraryChanged_AndTakesTheEditedMarkFromTheRepository()
    {
        var harness = new TestHarness();
        const string folderName = "RenamedRowMod_Message";
        var modFolderPath = CreateStaleAutoFixableModOnDisk(harness, folderName);
        var recipient = new object();
        var received = 0;
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(recipient, (_, _) => received++);
        try
        {
            var vm = harness.Build();
            await vm.CheckModsAgainstCurrentDataCommand.ExecuteAsync(null);
            var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == folderName);

            await vm.FixSureProblemsCommand.ExecuteAsync(null);

            Assert.True(received > 0);
            Assert.False(item.IsLocallyEdited);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
            Directory.Delete(modFolderPath, recursive: true);
        }
    }

    /// <summary>The fix window is modal, so a mod open in the editor (or a running check) is refused before it opens, with a reason.</summary>
    [Fact]
    public void OpenModFix_IsRefused_WhileTheModIsInTheEditor_OrACheckRuns()
    {
        var harness = new TestHarness();
        harness.AddMod("FixMe");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "FixMe");

        Assert.Null(vm.ModFixBlockedReason(item));

        vm.MarkFolderEditorOpenForTesting("FixMe");
        vm.OpenModFixCommand.Execute(item);
        Assert.Contains("open in the mod editor", vm.StatusMessage);

        vm.IsCheckingStaleness = true;
        Assert.Contains("checked or fixed", vm.ModFixBlockedReason(item));
    }

    /// <summary>
    /// However the fix window closed, the row's badge is checked again; "Keep all as they are"
    /// applies nothing, so nothing is logged as fixed and nothing is backed up.
    /// </summary>
    [Fact]
    public async Task AfterTheFixWindow_KeepAll_IsNotLoggedAsFixed_AndTheBadgeIsCheckedAgain()
    {
        var harness = new TestHarness();
        const string folderName = "RenamedRowMod_KeepAll";
        var modFolderPath = CreateStaleAutoFixableModOnDisk(harness, folderName);
        try
        {
            var vm = harness.Build();
            var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == folderName);
            Assert.Null(item.Health);

            var service = new ModFixService(harness.Repository, harness.DataFolder);
            var fixWindow = new ModFixViewModel(folderName, item.Name, service.Check(folderName), service) { Close = () => { } };
            foreach (var issue in fixWindow.Groups.SelectMany(g => g.Issues))
            {
                issue.Choose(ModIssueFix.KeepMod);
            }

            fixWindow.ApplyCommand.Execute(null);
            await vm.AfterModFixClosedAsync(item, fixWindow);

            Assert.True(fixWindow.IsApplied);
            Assert.Equal(0, fixWindow.AppliedCount);
            Assert.Empty(harness.Repository.BackupModCalls);
            Assert.DoesNotContain(harness.ActivityLog.Entries, e => e.Message.StartsWith("Fixed", StringComparison.Ordinal));
            Assert.True(item.HasModIssues);
        }
        finally
        {
            Directory.Delete(modFolderPath, recursive: true);
        }
    }

    /// <summary>A mod open in the mod editor is never written by Fix sure problems: the editor's next Save would undo it.</summary>
    [Fact]
    public async Task FixSureProblems_SkipsAModOpenInTheEditor()
    {
        var harness = new TestHarness();
        const string folderName = "RenamedRowMod_OpenInEditor";
        var modFolderPath = CreateStaleAutoFixableModOnDisk(harness, folderName);
        try
        {
            var vm = harness.Build();
            await vm.CheckModsAgainstCurrentDataCommand.ExecuteAsync(null);
            vm.MarkFolderEditorOpenForTesting(folderName);

            await vm.FixSureProblemsCommand.ExecuteAsync(null);

            Assert.Equal(StaleItemName, Assert.Single(Assert.Single(ExmodFolder.ReadPackageOnly(modFolderPath).Rows).FileItems).Name);
            Assert.DoesNotContain(folderName, harness.Repository.BackupModCalls);
            Assert.Contains("open in the mod editor", vm.StalenessCheckStatusMessage);
        }
        finally
        {
            Directory.Delete(modFolderPath, recursive: true);
        }
    }

    // Exact same scenario StaleItemFixSuggesterTests.Suggest_UnambiguousCloseMatchWithGoodFieldOverlap_CanAutoApply
    // proves triggers CanAutoApply: true (a stale item named "Stone_Pickax_MK2" (one letter short) with field
    // "RequiredMillijoules", against a base row "Stone_Pickaxe_MK2" with that same field) — reused
    // here rather than inventing a new one, since that test is the known-good reference for what
    // StaleItemFixSuggester.Suggest actually returns CanAutoApply: true for.
    private const string StaleItemName = "Stone_Pickax_MK2";
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

        // An earlier game version had the old name (so it's a real renamed game row, not the mod's own),
        // renamed in the very next one. Dated after the shipped history, or the refreshes would be older than it.
        var history = GameRowHistory.LocalFileFor(harness.DataFolder);
        File.WriteAllText(Path.Combine(baseDataDir, "D_ItemTemplate.json"), """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickax_MK2","RequiredMillijoules":100}]}""");
        GameRowHistory.Record(harness.DataFolder, history, new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero));
        File.WriteAllText(
            Path.Combine(baseDataDir, "D_ItemTemplate.json"),
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe_MK2","RequiredMillijoules":100}]}""");
        GameRowHistory.Record(harness.DataFolder, history, new DateTimeOffset(2099, 1, 8, 0, 0, 0, TimeSpan.Zero));

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
                    FileItems = [new ExmodFileItem { Name = StaleItemName, Fields = { ["RequiredMillijoules"] = JsonValue.Create(250) } }],
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
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler(FakeExmodzBytes()));
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
        // Through Downloads' Activate (like Download & extract): the download is unpacked and the
        // .EXMOD inside imported, not the .EXMODZ itself.
        Assert.Equal("Database", importCall.Source);
        Assert.Equal("cat1", importCall.CatalogEntryId);

        // The metadata carry-over must land on the NEW folder name the reimport produced, not the
        // old one that was just deleted — a real class of bug if the code ever used `folderName`
        // (captured before the delete) instead of `imported.FolderName` here.
        Assert.Contains(("ModA_v2", true, false, "great mod"), harness.Repository.UpdateMetadataCalls);
        Assert.Contains(("ModA_v2", "My Custom Name"), harness.Repository.SetDisplayNameOverrideCalls);

        // Named as the Library shows it now (the rename carried over).
        Assert.Contains("Updated 'My Custom Name' to v2.0.", vm.StatusMessage);
        Assert.Equal(1, vm.ModCount);
        Assert.Contains(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ModA_v2");
        Assert.DoesNotContain(vm.RootItems.OfType<LibraryItemViewModel>(), i => i.FolderName == "ModA_v1");
    }

    /// <summary>
    /// Regression guard, found live: "I click Update, the changelog prompt shows, but the Update
    /// badge doesn't go away and nothing tells me if it actually worked." A real mod's own FileName
    /// (and so its folder name) is typically fixed across versions — unlike the test above, which
    /// deliberately uses two DIFFERENT folder names to stress a different bug. When the folder name
    /// stays the SAME, GetOrCreateItem finds the already-cached LibraryItemViewModel and — per
    /// Reload's own fullResync contract — leaves it completely untouched unless told to fully
    /// resync. GetUpdateAsync's own success path used to call plain Reload(), so the row's own
    /// Version property stayed stuck at the pre-update value even though the reimport itself fully
    /// succeeded, and HasUpdateAvailable (LatestVersion != Version) kept reading true forever.
    /// </summary>
    [Fact]
    public async Task GetUpdateCommand_SameFolderNameAcrossVersions_RefreshesTheCachedRowsVersionAndClearsTheUpdateBadge()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: false) };
        harness.AddMod("ModA", name: "ModA", version: "1.0", source: "Database", catalogEntryId: "cat1");

        harness.DaedalusClient.Result =
        [
            new CatalogEntry(CatalogSource.Daedalus, "cat1", "ModA", "SomeAuthor", "2.0", "d", "", null, null, null, null, "http://fake.test/ModA.exmodz", []),
        ];
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler(FakeExmodzBytes()));
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => new LibraryEntry
        {
            FolderName = "ModA", Name = "ModA", Author = "SomeAuthor", Version = "2.0", Description = "d",
            FileName = "ModA", Source = source, CatalogEntryId = catalogId,
        };

        var vm = harness.Build();
        await vm.Downloads.GetOrFetchCatalogAsync();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        // Simulates an earlier CheckForUpdatesAsync scan already having found v2.0 as the latest —
        // exactly what put the "Update available" badge there in the first place.
        item.LatestVersion = "2.0";
        Assert.True(item.HasUpdateAvailable);

        await vm.GetUpdateCommand.ExecuteAsync(item);

        var refreshedItem = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        Assert.Same(item, refreshedItem); // same cached instance, reused by folder name — not replaced
        Assert.Equal("2.0", refreshedItem.Version);
        Assert.False(refreshedItem.HasUpdateAvailable);
        Assert.Contains("Updated 'ModA' to v2.0.", vm.StatusMessage);
    }

    [Fact]
    public async Task GetUpdateCommand_CatalogVersionStringDiffersFromImportedFilesOwnVersion_StillClearsTheUpdateBadge()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: false) };
        harness.AddMod("ModA", name: "ModA", version: "1.0", source: "Database", catalogEntryId: "cat1");

        harness.DaedalusClient.Result =
        [
            new CatalogEntry(CatalogSource.Daedalus, "cat1", "ModA", "SomeAuthor", "2.0", "d", "", null, null, null, null, "http://fake.test/ModA.exmodz", []),
        ];
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler(FakeExmodzBytes()));
        // Simulates a real-world data mismatch: the catalog LISTS this release as "2.0", but the
        // .EXMODZ file's own internal metadata (what Import actually reads Version from) says
        // something subtly different — e.g. the author bumped the catalog entry but forgot to bump
        // the package itself, or used a differently-formatted version tag inside the file.
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => new LibraryEntry
        {
            FolderName = "ModA", Name = "ModA", Author = "SomeAuthor", Version = "2.0.0", Description = "d",
            FileName = "ModA", Source = source, CatalogEntryId = catalogId,
        };

        var vm = harness.Build();
        await vm.Downloads.GetOrFetchCatalogAsync();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        item.LatestVersion = "2.0";
        Assert.True(item.HasUpdateAvailable);

        await vm.GetUpdateCommand.ExecuteAsync(item);

        var refreshedItem = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        Assert.Equal("2.0.0", refreshedItem.Version);
        Assert.False(refreshedItem.HasUpdateAvailable);
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
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler(FakeExmodzBytes()));
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
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler(FakeExmodzBytes()));
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => throw new FormatException("corrupt download");

        var vm = harness.Build();
        // A failed install offers Mod Organizer's manual install (a window): declined here.
        vm.Downloads.ManualInstall = (_, _, _, _) => null;
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
        Assert.Contains("already on the Downloads page", vm.StatusMessage);
    }

    /// <summary>
    /// Regression guard, found live: "when I update a Nexus mod from Library it says I already have
    /// it downloaded, which is odd — once it downloads it should already be in Library." Pending
    /// downloads are never removed except by an explicit Discard, so ANY mod ever downloaded from
    /// Nexus always has at least one PendingDownloadEntry sitting around forever. The old check
    /// matched on ModId alone, with no regard for whether that entry was already successfully
    /// activated — meaning once a mod had been downloaded via this app even once, clicking Update on
    /// it could never again open its real Nexus page for a newer file; it always redirected to
    /// Pending Downloads instead, even when the earlier download is already activated and IS the
    /// exact mod currently showing in Library. This is the control for the test above: same
    /// PendingDownloadEntry shape, but WITH a real activation that's still present, which must NOT
    /// trigger the redirect. SearchNexusFor itself isn't exercised here (the harness's own
    /// _nexusCatalogViewModel factory deliberately throws — see its own doc comment, "no test
    /// searches Nexus," building a real NexusCatalogViewModel is out of scope for this file) — that
    /// same throw is the proof the redirect branch was skipped and control genuinely reached
    /// SearchNexusFor, which is the one thing actually under test here.
    /// </summary>
    [Fact]
    public async Task GetUpdateCommand_NexusSourcedModWithOnlyAnAlreadyActivatedDownloadPresent_OpensNexusInsteadOfRedirecting()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", source: "Nexus", nexusModId: 555);
        harness.PendingDownloadStore.EntriesList.Add(new PendingDownloadEntry
        {
            ModId = 555, FileId = 1, FileName = "ModA.zip", LocalFilePath = @"C:\fake\ModA.zip",
            ActivatedFolderName = "ModA", ActivatedKind = PendingDownloadActivationKind.Library,
        });
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => vm.GetUpdateCommand.ExecuteAsync(item));

        Assert.Contains("no test searches Nexus", ex.Message);
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

    /// <summary>
    /// Regression guard, found live: "clicking Update on a Nexus mod, downloading through the page
    /// it opens, doesn't update the mod I clicked update on." GetUpdateAsync's own Nexus branch (see
    /// the test above) never downloads anything itself — it opens the mod's Nexus page and tells the
    /// user to use ITS OWN Download button, which lands in DownloadsViewModel.ActivatePendingDownloadAsync.
    /// That method only ever deleted an existing entry when THIS SPECIFIC PendingDownloadEntry had
    /// already been activated once before (item.IsInstalled) — but a mod originally installed some
    /// other way (a manual import, a migration from classic IMM, or simply a download from before
    /// Pending Downloads existed) has no such prior activation on THIS entry, even though a real
    /// Library entry for the same Nexus mod ID already exists. Without deleting that existing entry
    /// first, the reimport below just added a second, differently-named copy alongside the original —
    /// which is exactly why the original looked completely untouched.
    /// </summary>
    [Fact]
    public async Task ActivatePendingDownloadCommand_ExistingLibraryEntryLinkedToSameNexusModId_ReplacesItInsteadOfDuplicating()
    {
        var harness = new TestHarness();
        harness.AddMod("OldFolderName", name: "Some Mod", source: "Nexus", nexusModId: 555);
        harness.PendingDownloadStore.EntriesList.Add(new PendingDownloadEntry
        {
            ModId = 555, FileId = 999, FileName = "SomeMod-v2.pak", LocalFilePath = @"C:\fake\SomeMod-v2.pak",
        });
        harness.PrebuiltPakImporter.Result = new LibraryEntry
        {
            FolderName = "NewFolderName", Name = "Some Mod", Author = "Someone", Version = "2.0",
            Description = "d", FileName = "NewFolderName",
        };

        var vm = harness.Build();
        var pendingItem = vm.Downloads.PendingDownloads.Single(p => p.ModId == 555);

        await vm.Downloads.ActivatePendingDownloadCommand.ExecuteAsync(pendingItem);

        Assert.Contains("OldFolderName", harness.Repository.DeleteCalls);
        Assert.DoesNotContain(harness.Repository.GetAll(), e => e.FolderName == "OldFolderName");
        Assert.Contains(harness.Repository.GetAll(), e => e.FolderName == "NewFolderName");
    }

    // =========================================================================================
    // Test harness — one shared bundle of fakes for every dependency LibraryViewModel's own
    // (very large) constructor needs, plus a real DownloadsViewModel (itself constructed from
    // fakes) since LibraryViewModel takes that as a concrete, eagerly-needed dependency, not a
    // lazy factory. Fakes not touched by any test here throw NotSupportedException, matching
    // this project's established convention.
    // =========================================================================================

    // ---------------------------------------------------------------------------------------
    // Library review fixes: Get update records the download, Reinstall original reports
    // truthfully, sidecars survive a missing Mods folder, failed update checks are counted,
    // and Delete backs the mod up.
    // ---------------------------------------------------------------------------------------

    /// <summary>A Database pak update used to land as "IcarusStarlink_&lt;guid&gt;" from %TEMP%, unrecorded — so Reinstall original reinstalled the previous version.</summary>
    [Fact]
    public async Task GetUpdateCommand_DatabasePak_DownloadsIntoPendingDownloadsUnderTheModsNameAndRecordsIt()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: false) };
        harness.AddMod("Cool_Mod", name: "Cool Mod", version: "1.0", source: "Database", catalogEntryId: "cat9", isOpaquePak: true);
        harness.DaedalusClient.Result =
        [
            new CatalogEntry(CatalogSource.Daedalus, "cat9", "Cool Mod", "Someone", "2.0", "d", "", null, null, null, "http://fake.test/files/Cool_Mod_P.pak", null, []),
        ];
        harness.DownloadHttpClient = new HttpClient(new StaticByteResponseHandler("pak-bytes"u8.ToArray()));
        harness.PrebuiltPakImporter.Result = new LibraryEntry
        {
            FolderName = "Cool_Mod", Name = "Cool Mod", Author = "Someone", Version = "2.0", Description = "", FileName = "Cool_Mod", IsOpaquePak = true,
            Source = "Database", CatalogEntryId = "cat9",
        };
        var vm = harness.Build();
        await vm.Downloads.GetOrFetchCatalogAsync();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Cool_Mod");

        await vm.GetUpdateCommand.ExecuteAsync(item);

        var pakPath = harness.PrebuiltPakImporter.LastPakFilePath!;
        Assert.Equal("Cool Mod.pak", Path.GetFileName(pakPath));
        Assert.StartsWith(harness.PendingDownloadsDirectory, pakPath);
        var recorded = Assert.Single(harness.PendingDownloadStore.EntriesList);
        Assert.Equal("cat9", recorded.CatalogEntryId);
        Assert.Equal(pakPath, recorded.LocalFilePath);
        Assert.Contains(vm.Downloads.PendingDownloads, d => d.ActivatedItems.Any(a => a.FolderName == "Cool_Mod"));
        Assert.Contains("Updated 'Cool Mod' to v2.0.", vm.StatusMessage);
    }

    /// <summary>Reinstall original forgot the fix choices first and showed success even when the reinstall failed (Activate swallowed the error).</summary>
    [Fact]
    public async Task ReinstallOriginalCommand_WhenTheReinstallFails_SaysSoKeepsTheModAndItsFixChoices()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", source: "Nexus", nexusModId: 5, isOpaquePak: true);
        Directory.CreateDirectory(harness.PendingDownloadsDirectory);
        var pak = Path.Combine(harness.PendingDownloadsDirectory, "ModA.pak");
        File.WriteAllText(pak, "pak");
        harness.PendingDownloadStore.Add(new PendingDownloadEntry
        {
            ModId = 5, FileId = 9, FileName = "ModA.pak", LocalFilePath = pak, DownloadedAtUtc = DateTimeOffset.UtcNow,
            ActivatedItems = [new PendingDownloadActivatedItem("ModA", PendingDownloadActivationKind.Library)],
        });
        harness.PrebuiltPakImporter.ImportException = new InvalidDataException("the pak is damaged");
        Directory.CreateDirectory(harness.FixDecisionsDirectory);
        var choices = Path.Combine(harness.FixDecisionsDirectory, "ModA.json");
        File.WriteAllText(choices, "{}");
        var vm = harness.Build();
        vm.Downloads.ManualInstall = (_, _, _, _) => null;
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        await vm.ReinstallOriginalCommand.ExecuteAsync(item);

        Assert.Contains("failed", vm.StatusMessage);
        Assert.Contains("the pak is damaged", vm.StatusMessage);
        Assert.DoesNotContain(harness.ActivityLog.Entries, e => e.Kind == ActivityEntryKind.Success && e.Message.Contains("Reinstalled"));
        Assert.True(File.Exists(choices));
        Assert.Contains("ModA", harness.Repository.RestoreLatestModBackupCalls);
        Assert.Contains(harness.Repository.GetAll(), e => e.FolderName == "ModA");
    }

    /// <summary>With the game's Mods folder missing (drive unplugged), every enabled mod read as gone and all their Nexus links were purged.</summary>
    [Fact]
    public void ReloadInstalledUe4ssMods_GameModsFolderMissing_KeepsEverySidecar()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "Icarus", "Content");
        harness.Ue4ssModMetaStore.Metas["MiniMapa"] = new Ue4ssModMeta { NexusModId = 345 };
        var vm = harness.Build();

        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        Assert.Empty(harness.Ue4ssModMetaStore.DeleteCalls);
    }

    [Fact]
    public void ReloadInstalledUe4ssMods_GameModsFolderPresent_StillPurgesASidecarWithNoMod()
    {
        var harness = new TestHarness();
        var content = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "Icarus", "Content");
        Directory.CreateDirectory(Ue4ssGamePaths.ResolveModsFolder(content));
        harness.SettingsService.Current.IcarusContentPath = content;
        harness.Ue4ssModMetaStore.Metas["Gone"] = new Ue4ssModMeta { NexusModId = 1 };
        var vm = harness.Build();

        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        Assert.Contains("Gone", harness.Ue4ssModMetaStore.DeleteCalls);
    }

    [Fact]
    public async Task CheckForUpdates_WhenLookupsFail_SaysHowManyCouldNotBeChecked()
    {
        var harness = new TestHarness();
        harness.CredentialStore.ApiKey = "key";
        // No file recorded: the mod-level lookup, which this fake fails (a network error / the Nexus limit).
        harness.AddMod("A", source: "Nexus", nexusModId: 11);
        harness.AddMod("B", source: "Nexus", nexusModId: 12);
        var vm = harness.Build();

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.DoesNotContain("Everything checked is up to date", vm.UpdateCheckStatusMessage);
        Assert.Contains("Couldn't check 2 mod(s)", vm.UpdateCheckStatusMessage);
    }

    [Fact]
    public void DeleteItemCommand_BacksTheModUpFirst_AndForgetsItsFixChoices()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        Directory.CreateDirectory(harness.FixDecisionsDirectory);
        var choices = Path.Combine(harness.FixDecisionsDirectory, "ModA.json");
        File.WriteAllText(choices, "{}");
        var vm = harness.Build();
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        vm.DeleteItemCommand.Execute(item);

        Assert.Equal(["ModA"], harness.Repository.BackupModCalls);
        Assert.Contains("ModA", harness.Repository.DeleteCalls);
        Assert.False(File.Exists(choices));
        Assert.Contains("backup", harness.DialogService.ConfirmMessages.Single());
    }

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
        // Its own parent: the game row history's per-user file sits beside the Data folder (in Cache).
        public string DataFolder { get; } = Path.Combine(UniqueTempPath("DataRoot"), "Data");
        public string PendingDownloadsDirectory { get; } = UniqueTempPath("PendingDownloads");
        public string LogsDirectory { get; } = UniqueTempPath("Logs");
        public string FixDecisionsDirectory { get; } = UniqueTempPath("FixChoices");

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

        /// <summary>The fix service Library and the replacement share (as the app wires them).</summary>
        public ModFixService? FixService { get; private set; }

        public LibraryViewModel Build()
        {
            FixService = new ModFixService(Repository, DataFolder, decisionsDirectory: FixDecisionsDirectory);
            var replacement = new ModReplacementService(
                Repository, Ue4ssModRepository, Ue4ssModMetaStore, Ue4ssModStateService, SettingsService, BackupDirectory,
                DialogService, FixService, PrebuiltPakSourceStore);
            // The same download client as Library's own: a Database update downloads through Downloads.
            var downloads = new DownloadsViewModel(
                DaedalusClient, Jimk72Client, GitHubRepoDateClient, Repository, Ue4ssModRepository, Ue4ssModMetaStore,
                SettingsService, NexusApiClient, CredentialStore, PendingDownloadStore, DownloadHttpClient,
                new PerformanceTracker(SettingsService, LogsDirectory), ActivityLog, ActiveDownloadsTracker,
                PendingDownloadsDirectory, PrebuiltPakImporter, DataFolder, dialogService: DialogService, modReplacement: replacement);

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
                ThumbnailCacheDirectory, PakPreviewCacheDirectory, DialogService)
            {
                SharedFixService = FixService,
            };
        }
    }

    /// <summary>A catalog download Get update can install: Downloads' Activate unpacks it and finds an .EXMOD (the fake repository's Import does the rest).</summary>
    private static byte[] FakeExmodzBytes()
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("ModA.EXMOD").Open());
            writer.Write("{}");
        }

        return stream.ToArray();
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

        public LibraryEntry ImportPaks(IReadOnlyList<string> pakFilePaths, string name, string? source = null, int? nexusModId = null, string? catalogEntryId = null) => throw new NotSupportedException("Not exercised by these tests.");

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

        /// <summary>What GetAll reports as staged — seed it for a test that needs a Lua mod to exist.</summary>
        public List<string> Staged { get; } = [];
        public List<string> DeleteCalls { get; } = [];

        public IReadOnlyList<string> GetAll() => [.. Staged];
        public string Import(string zipFilePath, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");

        public void Delete(string folderName)
        {
            DeleteCalls.Add(folderName);
            Staged.RemoveAll(n => string.Equals(n, folderName, StringComparison.OrdinalIgnoreCase));
        }

        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) => throw new NotSupportedException("Not exercised by these tests.");
        public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUe4ssModStateService : IUe4ssModStateService
    {
        /// <summary>What GetAll reports — seed it (and set IcarusContentPath) for a test that wants UE4SS rows in the list.</summary>
        public List<Ue4ssModState> States { get; } = [];

        public IReadOnlyList<Ue4ssModState> GetAll(string gameModsFolderPath) => [.. States];
        public IReadOnlyList<Ue4ssModApplyFailure> Apply(string gameModsFolderPath, IReadOnlyDictionary<string, bool> desiredEnabledByName, string backupDirectory) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUe4ssModMetaStore : IUe4ssModMetaStore
    {
        public Dictionary<string, Ue4ssModMeta> Metas { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> ListFolderNames() => [.. Metas.Keys];
        public Ue4ssModMeta Load(string folderName) => Metas.GetValueOrDefault(folderName) ?? new();
        public void Save(string folderName, Ue4ssModMeta meta) { }
        public List<string> DeleteCalls { get; } = [];
        public void Delete(string folderName) => DeleteCalls.Add(folderName);
    }

    private sealed class FakeUe4ssLoaderInstallService : IUe4ssLoaderInstallService
    {
        public HashSet<string> BuiltIns { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Installed { get; set; } = true;

        public Ue4ssLoaderStatus GetStatus(string icarusContentPath) => Installed ? new(IsInstalled: true, InstalledVersion: "3.0.1") : new(false, null);
        public Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListUserAddedMods(string icarusContentPath) => throw new NotSupportedException("Not exercised by these tests.");
        public bool IsFrameworkOwned(string icarusContentPath, string modName) => BuiltIns.Contains(modName);
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

        /// <summary>Per-file update checks: the files (and update chain) Nexus lists for a mod.</summary>
        public Dictionary<int, NexusModFileSet> FileSets { get; } = [];

        public Task<NexusModFileSet> GetModFileSetAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            FileSets.TryGetValue(modId, out var set) ? Task.FromResult(set) : throw new HttpRequestException("no such mod");
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
        public string? ApiKey { get; set; }
        public void Save(string target, string secret) { }
        public string? Read(string target) => ApiKey;
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
        public void SetActivation(int modId, int fileId, IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<string> requirements) { }
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

        /// <summary>Thrown by ImportAsync when set (a damaged download).</summary>
        public Exception? ImportException { get; set; }

        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default)
        {
            ImportCallCount++;
            LastPakFilePath = pakFilePath;
            if (ImportException is not null)
            {
                throw ImportException;
            }

            repository.Seed(Result);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePrebuiltPakToExmodConverter : IPrebuiltPakToExmodConverter
    {
        public Task<PrebuiltPakConversionResult?> TryConvertAsync(
            string pakFilePath, string dataFolder, string unrealPakExePath, string name, string author, MergeReport report, GameValueHistory? gameHistory = null, CancellationToken cancellationToken = default) =>
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
