using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    // ---- Keep this version, and the version history ----

    [Fact]
    public async Task KeepThisVersion_LeavesTheModOutOfUpdateAll_AndTurningItOffBringsItBack()
    {
        var (vm, harness) = BuildTwoOutdatedNexusRows();
        var modA = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");

        vm.ToggleKeepVersionCommand.Execute(modA);

        var (folder, kept) = Assert.Single(harness.Repository.SetKeptVersionCalls);
        Assert.Equal("ModA", folder);
        Assert.Equal("2.0", kept?.SkippedVersion);
        Assert.False(modA.HasUpdateAvailable);
        Assert.True(modA.IsVersionKept);

        await vm.UpdateAllCommand.ExecuteAsync(null);
        Assert.Equal([2], harness.NexusApiClient.FileSetCalls);

        vm.ToggleKeepVersionCommand.Execute(modA);

        Assert.Null(harness.Repository.SetKeptVersionCalls[^1].Kept);
        Assert.True(modA.HasUpdateAvailable);
        Assert.False(modA.IsVersionKept);
        await vm.UpdateAllCommand.ExecuteAsync(null);
        Assert.Contains(1, harness.NexusApiClient.FileSetCalls);
    }

    /// <summary>A Library reload rebuilds the row from the entry: the switch stays on.</summary>
    [Fact]
    public void KeepThisVersion_StaysOnAfterALibraryReload()
    {
        var (vm, _) = BuildTwoOutdatedNexusRows();
        var modA = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        vm.ToggleKeepVersionCommand.Execute(modA);

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        var reloaded = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        Assert.True(reloaded.IsVersionKept);
        Assert.False(reloaded.HasUpdateAvailable);
    }

    [Fact]
    public async Task KeepThisVersion_OnAUe4ssMod_IsSavedInItsInfo_AndUpdateAllLeavesItAlone()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("CoolLua", IsEnabled: true));
        harness.Ue4ssModMetaStore.Metas["CoolLua"] = new Ue4ssModMeta { NexusModId = 11, NexusVersion = "1.0" };
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var row = vm.Ue4ssMods.Single(r => r.Name == "CoolLua");
        row.LatestVersion = "2.0";

        vm.ToggleKeepUe4ssVersionCommand.Execute(row);

        Assert.Equal("2.0", harness.Ue4ssModMetaStore.Metas["CoolLua"].KeptVersion?.SkippedVersion);
        Assert.False(row.HasUpdateAvailable);
        await vm.UpdateAllCommand.ExecuteAsync(null);
        Assert.Contains("Nothing to update", vm.StatusMessage);

        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        Assert.True(vm.Ue4ssMods.Single(r => r.Name == "CoolLua").IsVersionKept);

        vm.ToggleKeepUe4ssVersionCommand.Execute(vm.Ue4ssMods.Single(r => r.Name == "CoolLua"));
        Assert.Null(harness.Ue4ssModMetaStore.Metas["CoolLua"].KeptVersion);
    }

    private static (LibraryViewModel Vm, TestHarness Harness, ModVersionHistoryViewModel History) OpenHistoryWithOneOldVersion(bool confirm)
    {
        var harness = new TestHarness { DialogService = new FakeDialogService(confirmResult: confirm) };
        harness.AddMod("ModA");
        harness.Repository.VersionHistory["ModA"] =
            [new ModBackupInfo("ModA_old", new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), ModBackupReasons.BeforeReplace, "1.0", 77)];
        var vm = harness.Build();
        ModVersionHistoryViewModel? shown = null;
        vm.ShowVersionHistoryWindow = history => shown = history;

        vm.ShowVersionHistoryCommand.Execute(vm.RootItems.OfType<LibraryItemViewModel>().Single());

        Assert.NotNull(shown);
        return (vm, harness, shown);
    }

    [Fact]
    public void VersionHistory_ListsEachKeptCopy_WithWhyAndWhichVersion()
    {
        var (_, _, history) = OpenHistoryWithOneOldVersion(confirm: true);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(ModBackupReasons.BeforeReplace, entry.ReasonText);
        Assert.Equal("v1.0", entry.VersionText);
        Assert.Equal("Nexus file 77", entry.FileText);
        Assert.Same(entry, history.SelectedEntry);
    }

    /// <summary>Putting an older version back asks first, then backs up the current copy (it tops the list) before the restore.</summary>
    [Fact]
    public async Task VersionHistory_PutBack_AsksFirst_ThenRestoresAfterBackingUpTheCurrentCopy()
    {
        var (vm, harness, history) = OpenHistoryWithOneOldVersion(confirm: true);

        await history.RestoreCommand.ExecuteAsync(null);

        Assert.Contains("backs up the copy you have now first", Assert.Single(harness.DialogService.ConfirmMessages));
        Assert.Equal([("ModA", "ModA_old")], harness.Repository.RestoreModBackupCalls);
        Assert.Equal(2, history.Entries.Count);
        Assert.Equal(ModBackupReasons.BeforeRestore, history.Entries[0].ReasonText);
        Assert.Contains("Put back", vm.StatusMessage);
    }

    [Fact]
    public async Task VersionHistory_PutBack_Cancelled_ChangesNothing()
    {
        var (_, harness, history) = OpenHistoryWithOneOldVersion(confirm: false);

        await history.RestoreCommand.ExecuteAsync(null);

        Assert.Single(harness.DialogService.ConfirmMessages);
        Assert.Empty(harness.Repository.RestoreModBackupCalls);
        Assert.Single(history.Entries);
    }

    [Fact]
    public async Task VersionHistory_PutBack_WhileTheModIsOpenInTheEditor_RefusesWithoutAsking()
    {
        var (vm, harness, history) = OpenHistoryWithOneOldVersion(confirm: true);
        vm.MarkFolderEditorOpenForTesting("ModA");

        await history.RestoreCommand.ExecuteAsync(null);

        Assert.Empty(harness.DialogService.ConfirmMessages);
        Assert.Empty(harness.Repository.RestoreModBackupCalls);
        Assert.Contains("open in the mod editor", vm.StatusMessage);
    }
}
