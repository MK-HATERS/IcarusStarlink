using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Deleting linked halves, importing a folder, the open details windows, the click selection after a reload, and the notice.</summary>
public sealed partial class LibraryViewModelTests
{
    // ---------------------------------------------------------------------------------------
    // Halves linked by name: Delete asks which halves go, from either panel.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: MkHaters Bags (EXMOD, no Nexus ID) and MkHatersBags (UE4SS) show as linked, but
    /// deleting the data row only asked "Delete 'MkHaters Bags' from your Library?" and left the
    /// Lua half in place.
    /// </summary>
    [Fact]
    public void DeleteItemCommand_OnANameLinkedPair_AsksWhichHalvesGo()
    {
        var (vm, harness) = BuildPanels();
        harness.Ue4ssModRepository.Staged.Add("MkHatersBags");
        var bags = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.Name == "MkHaters Bags");

        vm.DeleteItemCommand.Execute(bags);

        var question = Assert.Single(harness.DialogService.ConfirmMessages);
        Assert.Contains("Delete both halves", question);
        Assert.Contains("MkHatersBags", question);
        Assert.Contains("MkHaters_Bags", harness.Repository.DeleteCalls);
        Assert.Equal(["MkHatersBags"], harness.Ue4ssModRepository.DeleteCalls);
    }

    /// <summary>The same pair deleted from the UE4SS panel asks the same question and takes the data half too.</summary>
    [Fact]
    public void DeleteUe4ssModCommand_OnANameLinkedRow_AsksWhichHalvesGo()
    {
        var (vm, harness) = BuildPanels();
        harness.Ue4ssModRepository.Staged.Add("MkHatersBags");
        var luaBags = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MkHatersBags");

        vm.DeleteUe4ssModCommand.Execute(luaBags);

        var question = Assert.Single(harness.DialogService.ConfirmMessages);
        Assert.Contains("Delete both halves", question);
        Assert.Contains("MkHaters_Bags", harness.Repository.DeleteCalls);
        Assert.Equal(["MkHatersBags"], harness.Ue4ssModRepository.DeleteCalls);
    }

    // ---------------------------------------------------------------------------------------
    // Importing an unpacked folder.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: a dropped folder holding an .EXMOD went straight to the EXMOD import, so the
    /// download's Lua half (MyMod\Scripts\main.lua) never became a UE4SS row, though the same
    /// content as an archive gave both rows.
    /// </summary>
    [Fact]
    public async Task ImportPaths_FolderWithAnExmodAndALuaHalf_StagesTheLuaHalfToo()
    {
        var harness = new TestHarness();
        var folderPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folderPath, "Extracted Mods"));
        File.WriteAllText(Path.Combine(folderPath, "Extracted Mods", "ImportedMod.EXMOD"), "{}");
        Directory.CreateDirectory(Path.Combine(folderPath, "MyMod", "Scripts"));
        File.WriteAllText(Path.Combine(folderPath, "MyMod", "Scripts", "main.lua"), "print('hi')");
        try
        {
            harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => new LibraryEntry
            {
                FolderName = "ImportedMod", Name = "Imported Mod", Author = "Someone", Version = "1.0", Description = "D", FileName = "ImportedMod",
            };
            var vm = harness.Build();

            await vm.ImportPaths([folderPath]);

            Assert.Equal(1, harness.Ue4ssModRepository.ImportFromFolderCallCount);
            Assert.Equal("MyMod", harness.Ue4ssModRepository.LastFallbackName);
            Assert.Equal(folderPath, Assert.Single(harness.Repository.ImportCalls).SourcePath);
            Assert.True(File.Exists(Path.Combine(folderPath, "MyMod", "Scripts", "main.lua")), "the user's own folder must be left as it was");
            Assert.Contains("Apply UE4SS changes", vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(folderPath, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Details windows and details loading.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: a details window showing a row other than the list's selected one (reached
    /// with Next) went blank after any Library change: the change cleared every row's Files and
    /// Readme, and only the selected row was read again.
    /// </summary>
    [Fact]
    public void DetailWindow_ShowingAnUnselectedRow_IsFilledAgainAfterALibraryChange()
    {
        var harness = new TestHarness();
        harness.AddMod("A");
        harness.AddMod("B", isOpaquePak: true);
        harness.Repository.AssetPathsByFolder["B"] = ["x.uasset"];
        harness.Repository.ReadmeByFolder["B"] = "readme";
        var vm = harness.Build();
        var a = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "A");
        var b = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "B");
        vm.SelectedItem = a;
        vm.DetailWindowShows(b);
        b.EnsureDetailsLoaded();

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        Assert.Equal(["x.uasset"], b.AssetPaths);
        Assert.Equal("readme", b.ReadmeContent);
    }

    /// <summary>
    /// Regression: clicking or arrowing onto a row read its whole folder, parsed its .EXMOD and
    /// started the change summary, though only the details window shows any of it.
    /// </summary>
    [Fact]
    public void SelectingARow_DoesNotLoadItsDetails()
    {
        var harness = new TestHarness();
        harness.AddMod("A");
        harness.Repository.AssetPathsByFolder["A"] = ["x.uasset"];
        var vm = harness.Build();
        var a = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "A");

        vm.SelectedItem = a;

        Assert.Equal(0, harness.Repository.ListFolderFilesCallCount);
        Assert.Empty(a.AssetPaths);
    }

    // ---------------------------------------------------------------------------------------
    // Rows the search hides, and the click selection after a reload.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: a change made while the search hid a mod (an update from Downloads) left its
    /// cached row with the old version once the search was cleared.
    /// </summary>
    [Fact]
    public void FullResync_UpdatesARowTheSearchHides()
    {
        var harness = new TestHarness();
        var entryA = harness.AddMod("ModA", name: "Alpha", version: "1.0");
        harness.AddMod("ModB", name: "Beta");
        var vm = harness.Build();
        var itemA = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        vm.SearchText = "Beta";
        entryA.Version = "2.0";

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        Assert.DoesNotContain(itemA, vm.DataRootItems);
        Assert.Equal("2.0", itemA.Version);
    }

    /// <summary>Regression: a mod removed outside the app and dropped by Refresh stayed selected, and Delete then failed on it.</summary>
    [Fact]
    public void Refresh_DropsARemovedModFromTheSelection()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA");
        harness.AddMod("ModB");
        var vm = harness.Build();
        vm.SelectAllForBulkCommand.Execute("data");
        harness.Repository.Delete("ModA");

        vm.RefreshCommand.Execute(null);

        Assert.Equal(["ModB"], vm.BulkSelectedItems.Select(i => i.FolderName));
    }

    /// <summary>Regression: Ctrl+A then a filter chip kept every hidden row selected, so Space or Delete acted on mods the list no longer showed.</summary>
    [Fact]
    public void FilterChip_DropsHiddenRowsFromTheSelection()
    {
        var harness = new TestHarness();
        var itemsBefore = harness.AddMod("ModA");
        harness.AddMod("ModB", isFavorite: true);
        var vm = harness.Build();
        vm.SelectAllForBulkCommand.Execute("data");
        var modA = vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == itemsBefore.FolderName);

        vm.SelectFilterCommand.Execute(LibraryViewModel.FilterFavourites);

        Assert.Equal(["ModB"], vm.BulkSelectedItems.Select(i => i.FolderName));
        Assert.False(modA.IsSelectedForBulk);
    }

    /// <summary>Deleting several mods names them in the question, not only how many.</summary>
    [Fact]
    public void DeleteItemCommand_SeveralMods_NamesThemInTheQuestion()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: false) };
        harness.AddMod("ModA", name: "Alpha");
        harness.AddMod("ModB", name: "Beta");
        harness.AddMod("ModC", name: "Gamma");
        var vm = harness.Build();
        vm.SelectAllForBulkCommand.Execute("data");

        vm.DeleteItemCommand.Execute(null);

        var question = Assert.Single(harness.DialogService.ConfirmMessages);
        Assert.Contains("Delete 3 mods", question);
        Assert.Contains("Alpha", question);
        Assert.Contains("Beta", question);
        Assert.Contains("Gamma", question);
    }

    /// <summary>Past five mods the question names the first five and says how many more.</summary>
    [Fact]
    public void DeleteItemCommand_ManyMods_NamesFiveAndCountsTheRest()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: false) };
        foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta" })
        {
            harness.AddMod(name);
        }

        var vm = harness.Build();
        vm.SelectAllForBulkCommand.Execute("data");

        vm.DeleteItemCommand.Execute(null);

        var question = Assert.Single(harness.DialogService.ConfirmMessages);
        Assert.Contains("Delete 7 mods", question);
        Assert.Contains("and 2 more", question);
    }

    // ---------------------------------------------------------------------------------------
    // The notice.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: an answer equal to the last one ("Nothing to update", clicked again after the
    /// notice faded) showed nothing, so the button looked dead.
    /// </summary>
    [Fact]
    public void Notice_TheSameMessageAgainAfterItWasDismissed_ShowsAgain()
    {
        var vm = new TestHarness().Build();
        vm.StatusMessage = "Nothing to update";
        vm.DismissNoticeCommand.Execute(null);

        vm.StatusMessage = "Nothing to update";

        Assert.Equal("Nothing to update", vm.Notice);
    }

    /// <summary>The same for the UE4SS panel's answers ("Sign in with your Nexus API key…").</summary>
    [Fact]
    public void Notice_TheSameUe4ssMessageAgainAfterItWasDismissed_ShowsAgain()
    {
        var vm = new TestHarness().Build();
        vm.Ue4ssStatusMessage = "Sign in first";
        vm.DismissNoticeCommand.Execute(null);

        vm.Ue4ssStatusMessage = "Sign in first";

        Assert.Equal("Sign in first", vm.Notice);
    }

    // ---------------------------------------------------------------------------------------
    // Delete's result line and the warning colour.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: deleting WirelessPower (both halves) while its pak was locked said "Deleted 0 of
    /// 2", though the UE4SS half was gone, and showed it in the normal colour.
    /// </summary>
    [Fact]
    public void DeleteItemCommand_PackageWhoseDataHalfFails_CountsTheDeletedUe4ssHalf()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("WirelessPower", name: "Wireless Power", source: "Nexus", nexusModId: 361, isOpaquePak: true);
        harness.Ue4ssModRepository.Staged.Add("WirelessPower");
        harness.Ue4ssModMetaStore.Metas["WirelessPower"] = new Ue4ssModMeta { NexusModId = 361, NexusName = "Wireless Power" };
        harness.Repository.DeleteException = ("WirelessPower", new IOException("locked"));
        var vm = harness.Build();

        vm.DeleteItemCommand.Execute(vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "WirelessPower"));

        Assert.Equal(["WirelessPower"], harness.Ue4ssModRepository.DeleteCalls);
        Assert.Contains("Deleted 1 of 2", vm.StatusMessage);
        Assert.True(vm.IsNoticeWarning);
    }

    /// <summary>Regression: "Mods are being checked or fixed… Try again when that's done." showed in the normal colour.</summary>
    [Fact]
    public void DeleteItemCommand_WhileModsAreBeingChecked_RefusalShowsAsAWarning()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA");
        var vm = harness.Build();
        vm.IsCheckingStaleness = true;

        vm.DeleteItemCommand.Execute(vm.DataRootItems.OfType<LibraryItemViewModel>().Single());

        Assert.Empty(harness.Repository.DeleteCalls);
        Assert.Contains("being checked", vm.Notice);
        Assert.True(vm.IsNoticeWarning);
    }

    // ---------------------------------------------------------------------------------------
    // Importing: closing the "which one?" chooser.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: closing the "which one?" chooser (Minimap.EXMODZ and Minimap_PTBR.EXMODZ in one
    /// zip) read "Imported 0 of 1 — …: Import cancelled …" and logged an amber "Import failed".
    /// </summary>
    [Fact]
    public async Task ImportPaths_ChooserClosed_SaysCancelledNotFailed()
    {
        var harness = new TestHarness();
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.zip");
        using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "Minimap.EXMODZ", "Minimap_PTBR.EXMODZ" })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(EmptyZipBytes());
            }
        }

        try
        {
            var vm = harness.Build();
            vm.Downloads.ChooseArchive = (_, _) => null;

            await vm.ImportPaths([zipPath]);

            Assert.Equal("Import cancelled — nothing was changed.", vm.StatusMessage);
            Assert.DoesNotContain(harness.ActivityLog.Entries, e => e.Kind == IcarusStarlink.Core.Activity.ActivityEntryKind.Warning);
            Assert.Empty(harness.Repository.ImportCalls);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    /// <summary>
    /// Regression: with imports unpacking off the UI thread the window stays usable, and a second
    /// drop while the first import waited on its "which one?" chooser started a second import
    /// beside it (two choosers, one shared result line).
    /// </summary>
    [Fact]
    public async Task ImportPaths_WhileAnotherImportRuns_SaysToWait()
    {
        var harness = new TestHarness();
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.zip");
        using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "Minimap.EXMODZ", "Minimap_PTBR.EXMODZ" })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(EmptyZipBytes());
            }
        }

        try
        {
            var vm = harness.Build();
            Task? second = null;
            string? statusDuringTheFirst = null;
            vm.Downloads.ChooseArchive = (_, _) =>
            {
                // The second drop lands while the first import still waits on this chooser.
                second = vm.ImportPaths([Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.pak")]);
                statusDuringTheFirst = vm.StatusMessage;
                return null;
            };

            await vm.ImportPaths([zipPath]);

            Assert.NotNull(second);
            Assert.True(second!.IsCompleted);
            Assert.Contains("still running", statusDuringTheFirst);
            Assert.Equal(0, harness.PrebuiltPakImporter.ImportCallCount);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    private static byte[] EmptyZipBytes()
    {
        using var memory = new MemoryStream();
        using (new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        return memory.ToArray();
    }

    // ---------------------------------------------------------------------------------------
    // Which library checks block which.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: the silent update check at launch greyed out every Check item (one shared
    /// CanRunLibraryCheck), with nothing saying which check was running.
    /// </summary>
    [Fact]
    public void LibraryChecks_TheUpdateCheckDoesNotBlockModPasses()
    {
        var vm = new TestHarness().Build();

        vm.IsCheckingForUpdates = true;

        Assert.True(vm.CanRunModPass);
        Assert.True(vm.CanCheckEndorsements);
        Assert.False(vm.CanCheckForUpdates);
        Assert.False(vm.CanUpdateAll);
        Assert.Equal("Checking for updates…", vm.LibraryChecksRunningText);
    }

    /// <summary>Two checks at once are both named; none running shows no line.</summary>
    [Fact]
    public void LibraryChecksRunningText_NamesEachRunningCheck_AndIsNullWhenNoneRuns()
    {
        var vm = new TestHarness().Build();
        vm.IsCheckingForUpdates = false;
        Assert.Null(vm.LibraryChecksRunningText);

        vm.IsCheckingStaleness = true;
        vm.IsCheckingEndorsements = true;

        Assert.Equal("Working on mods against the game data… · Checking endorsements…", vm.LibraryChecksRunningText);
        Assert.False(vm.CanRunModPass);
        Assert.False(vm.CanUpdateAll);
        Assert.True(vm.CanCheckForUpdates);
        Assert.False(vm.CanCheckEndorsements);
    }

    // ---------------------------------------------------------------------------------------
    // The UE4SS panel: Del with several rows, and the endorse thumb.
    // ---------------------------------------------------------------------------------------

    /// <summary>Regression: Del with two UE4SS rows selected did nothing and said nothing.</summary>
    [Fact]
    public void DeleteSelectedUe4ssMods_TwoSelected_SaysOneAtATime()
    {
        var (vm, harness) = BuildPanels();
        var rows = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Where(r => r.Name is "ArcFissionLibrary" or "MiniMapa").ToList();
        vm.SetBulkSelectionAnchor(rows[0]);
        vm.ToggleBulkSelection(rows[1]);

        vm.DeleteSelectedUe4ssMods();

        Assert.Empty(harness.Ue4ssModRepository.DeleteCalls);
        Assert.Empty(harness.DialogService.ConfirmMessages);
        Assert.Contains("one UE4SS mod at a time", vm.Ue4ssStatusMessage);
    }

    /// <summary>One UE4SS row selected: Del deletes it, after the usual question.</summary>
    [Fact]
    public void DeleteSelectedUe4ssMods_OneSelected_DeletesIt()
    {
        var (vm, harness) = BuildPanels();
        var arc = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "ArcFissionLibrary");
        harness.Ue4ssModRepository.Staged.Add("ArcFissionLibrary");
        vm.SetBulkSelectionAnchor(arc);

        vm.DeleteSelectedUe4ssMods();

        Assert.Single(harness.DialogService.ConfirmMessages);
        Assert.Equal(["ArcFissionLibrary"], harness.Ue4ssModRepository.DeleteCalls);
    }

    /// <summary>Regression: an endorsed UE4SS mod's thumb stayed clickable and did nothing.</summary>
    [Fact]
    public void EndorseUe4ssMod_AlreadyEndorsed_CantRun()
    {
        var (vm, _) = BuildPanels();
        var miniMapa = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MiniMapa");
        Assert.True(vm.EndorseUe4ssModCommand.CanExecute(miniMapa));

        miniMapa.EndorsementStatus = IcarusStarlink.Catalog.Nexus.NexusEndorsementStatus.Endorsed;

        Assert.False(vm.EndorseUe4ssModCommand.CanExecute(miniMapa));
    }

    /// <summary>Regression: a UE4SS endorse that failed only set the page's status line, so a player on another page never saw it.</summary>
    [Fact]
    public async Task EndorseUe4ssMod_NexusFails_IsLoggedAsAWarning()
    {
        var (vm, harness) = BuildPanels();
        harness.CredentialStore.ApiKey = "key";
        var miniMapa = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MiniMapa");

        // The fake Nexus client throws on GetModInfoAsync, so the endorse fails.
        await vm.EndorseUe4ssModCommand.ExecuteAsync(miniMapa);

        Assert.StartsWith("Couldn't endorse 'MiniMapa'", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains(harness.ActivityLog.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message.StartsWith("Couldn't endorse 'MiniMapa'", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // Delete: what goes with the mod, and what the result line says.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: with the mod's fix-choices file held open (an editor, antivirus), the mod was
    /// deleted but the Library said "Deleted 0 of 1".
    /// </summary>
    [Fact]
    public void DeleteItemCommand_FixChoicesFileLocked_StillReportsTheDelete()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        Directory.CreateDirectory(harness.FixDecisionsDirectory);
        var choices = Path.Combine(harness.FixDecisionsDirectory, "ModA.json");
        File.WriteAllText(choices, "{}");
        var vm = harness.Build();

        using (new FileStream(choices, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            vm.DeleteItemCommand.Execute(vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA"));
        }

        Assert.Contains("ModA", harness.Repository.DeleteCalls);
        Assert.Contains("Deleted 'ModA'", vm.StatusMessage);
        Assert.DoesNotContain("of 1", vm.StatusMessage);
    }

    /// <summary>Regression: a deleted mod's cached Nexus picture stayed, so a different mod later imported under the same folder name showed it.</summary>
    [Fact]
    public void DeleteItemCommand_AlsoForgetsTheCachedNexusPicture()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA", source: "Nexus", nexusModId: 5);
        Directory.CreateDirectory(harness.ThumbnailCacheDirectory);
        var picture = Path.Combine(harness.ThumbnailCacheDirectory, "ModA.img");
        File.WriteAllBytes(picture, [1, 2, 3]);
        var vm = harness.Build();

        vm.DeleteItemCommand.Execute(vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA"));

        Assert.Contains("ModA", harness.Repository.DeleteCalls);
        Assert.False(File.Exists(picture));
    }

    /// <summary>
    /// Regression: every part-failed delete ended "Close whatever is using its files, then try
    /// again", also when nothing was locked (here: the mod is no longer in the Library).
    /// </summary>
    [Fact]
    public void DeleteItemCommand_FailsForAReasonThatIsNotALock_DoesNotSayToCloseAnything()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        harness.Repository.DeleteException = ("ModA", new InvalidOperationException("No library entry named 'ModA'."));
        var vm = harness.Build();

        vm.DeleteItemCommand.Execute(vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA"));

        Assert.Contains("Deleted 0 of 1", vm.StatusMessage);
        Assert.DoesNotContain("Close whatever", vm.StatusMessage);
    }

    /// <summary>A locked file still gets the next step.</summary>
    [Fact]
    public void DeleteItemCommand_FileLocked_SaysToCloseWhatIsUsingIt()
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: true) };
        harness.AddMod("ModA");
        harness.Repository.DeleteException = ("ModA", new IOException("file is locked by another process"));
        var vm = harness.Build();

        vm.DeleteItemCommand.Execute(vm.DataRootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA"));

        Assert.Contains("Close whatever is using its files", vm.StatusMessage);
    }

    /// <summary>
    /// Regression: with two UE4SS rows linked by name to one data mod (MkHatersBags and
    /// MkHaters_Bags beside MkHaters Bags), Delete on one of them offered the data mod and the
    /// OTHER UE4SS row, not the one clicked.
    /// </summary>
    [Fact]
    public void DeleteUe4ssModCommand_TwoRowsLinkedByNameToOneDataMod_DeletesTheRowClicked()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.AddMod("MkHaters_Bags", name: "MkHaters Bags");
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("MkHatersBags", IsEnabled: false));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("MkHaters_Bags", IsEnabled: false));
        harness.Ue4ssModRepository.Staged.AddRange(["MkHatersBags", "MkHaters_Bags"]);
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var data = vm.DataRootItems.OfType<LibraryItemViewModel>().Single();
        var rows = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().ToList();
        var notTheDataModsPartner = rows.Single(r => !ReferenceEquals(r, data.LinkedPartner));
        var thePartner = rows.Single(r => ReferenceEquals(r, data.LinkedPartner));

        vm.DeleteUe4ssModCommand.Execute(notTheDataModsPartner);

        Assert.Contains(notTheDataModsPartner.Name, harness.Ue4ssModRepository.DeleteCalls);
        Assert.DoesNotContain(thePartner.Name, harness.Ue4ssModRepository.DeleteCalls);
    }

    // ---------------------------------------------------------------------------------------
    // The UE4SS panel: search, Nexus link, minimum UE4SS version.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: the UE4SS panel needed the whole phrase ("minimap icons" found nothing beside
    /// "Minimap and Map Icons"), while every word of a search narrows the data panel.
    /// </summary>
    [Fact]
    public void Ue4ssSearch_TwoWords_ShowsOnlyRowsWithBothWords()
    {
        var (vm, _) = BuildPanels();

        vm.SearchText = "minimap icons";
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var shown = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Select(r => r.Name).ToList();

        Assert.Equal(["MiniMapa"], shown);

        vm.SearchText = "minimap bags";
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        Assert.Empty(vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>());
    }

    /// <summary>
    /// Regression: re-linking a UE4SS mod to another Nexus ID kept the old mod's file and its
    /// "Update available", which the new mod's files could never clear.
    /// </summary>
    [Fact]
    public async Task LinkUe4ssModToNexusCore_ToADifferentId_ForgetsTheOldFileAndUpdate()
    {
        var (vm, harness) = BuildPanels();
        var meta = harness.Ue4ssModMetaStore.Metas["MiniMapa"];
        meta.NexusFileId = 4242;
        meta.NexusFileVersion = "1.7";
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var miniMapa = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MiniMapa");
        miniMapa.LatestVersion = "2.0";
        miniMapa.LatestNexusFileId = 5000;
        miniMapa.NewestOnNexus = "2.0";

        await vm.LinkUe4ssModToNexusCoreAsync(miniMapa, 22);

        Assert.Equal(22, meta.NexusModId);
        Assert.Null(meta.NexusFileId);
        Assert.Null(meta.NexusFileVersion);
        var relinked = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MiniMapa");
        Assert.Null(relinked.LatestVersion);
        Assert.Null(relinked.LatestNexusFileId);
        Assert.Null(relinked.NewestOnNexus);
        Assert.False(relinked.HasUpdateAvailable);
    }

    /// <summary>Linking again to the same Nexus ID keeps the file and the update found for it.</summary>
    [Fact]
    public async Task LinkUe4ssModToNexusCore_ToTheSameId_KeepsTheFileAndUpdate()
    {
        var (vm, harness) = BuildPanels();
        var meta = harness.Ue4ssModMetaStore.Metas["MiniMapa"];
        meta.NexusFileId = 4242;
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var miniMapa = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MiniMapa");
        miniMapa.LatestNexusFileId = 5000;

        await vm.LinkUe4ssModToNexusCoreAsync(miniMapa, 11);

        Assert.Equal(4242, meta.NexusFileId);
        Assert.Equal(5000, vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MiniMapa").LatestNexusFileId);
    }

    /// <summary>Regression: a minimum version typed as "v3.0" read "now requires UE4SS vv3.0+".</summary>
    [Fact]
    public void SetMinUe4ssVersion_TypedWithAV_DoesNotDoubleTheV()
    {
        var (vm, harness) = BuildPanels();
        harness.DialogService.PromptRenameResult = new RenamePromptResult(false, "v3.0");
        var miniMapa = vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == "MiniMapa");

        vm.SetMinUe4ssVersionCommand.Execute(miniMapa);

        Assert.Contains("now requires UE4SS v3.0+", vm.Ue4ssStatusMessage);
        Assert.DoesNotContain("vv", vm.Ue4ssStatusMessage);
    }

    // ---------------------------------------------------------------------------------------
    // Details windows: the preview is let go when no window shows the row.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: rows are cached for the session, and a closed details window (or one moved on
    /// with Prev/Next) left the row's decoded image, mesh and material in memory.
    /// </summary>
    [Fact]
    public void DetailWindowLeaves_NoOtherWindowShowsTheRow_ReleasesItsPreview()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA");
        var vm = harness.Build();
        var item = vm.DataRootItems.OfType<LibraryItemViewModel>().Single();
        vm.DetailWindowShows(item);
        vm.DetailWindowShows(item);
        item.SelectedAssetPath = "readme.txt";

        vm.DetailWindowLeaves(item);

        Assert.Equal("readme.txt", item.SelectedAssetPath); // the second window still shows it

        vm.DetailWindowLeaves(item);

        Assert.Null(item.SelectedAssetPath);
        Assert.False(vm.IsShownInDetailWindow(item));
    }
}
