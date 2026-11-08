using System.IO;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;
using Microsoft.Extensions.Logging.Abstractions;
using IcarusStarlink.Storage.Nexus;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Keeping Pending_Downloads tidy: stray .part files, each download's file version, older versions and files no download points at.</summary>
public sealed partial class DownloadsViewModelTests
{
    private string PartFile(Harness harness, string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(harness.PendingDownloadsDirectory, name);
        File.WriteAllText(path, "partial bytes");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    /// <summary>A crash or kill mid-download leaves its .part file behind; only old ones go, never one a download still writes.</summary>
    [Fact]
    public async Task PartSweep_RemovesOldPartFiles_KeepsRecentAndActiveOnes()
    {
        var harness = new Harness(_root);
        var now = DateTime.UtcNow;
        var old = PartFile(harness, "Old.zip.0123abcd.part", now.AddDays(-3));
        var recent = PartFile(harness, "Recent.zip.4567abcd.part", now.AddHours(-2));
        var active = PartFile(harness, "Active.zip.89abcdef.part", now.AddDays(-3));
        var notPart = PartFile(harness, "Kept.zip", now.AddDays(-30));
        var vm = harness.Build();

        int removed;
        using (new FileStream(active, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            removed = await vm.SweepStrayPartFilesAsync(new DateTimeOffset(now));
        }

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(notPart));
    }

    [Fact]
    public async Task Activate_NexusDownload_RecordsTheFilesVersionOnTheDownload()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        // The fake Nexus lists file 1 of every mod as version "1".
        var item = harness.AddDownload("WirelessPower.zip", WriteWirelessPowerLayout, modId: 361, fileId: 1);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Equal("1", harness.PendingStore.Entries.Single().FileVersion);
        Assert.Equal("1", item.FileVersion);
        Assert.Equal("1", new PendingDownloadStore(_root, NullLogger<PendingDownloadStore>.Instance).Entries.Single().FileVersion);
    }

    /// <summary>Downloads recorded before the version was kept take it from the installed copy of the same Nexus file.</summary>
    [Fact]
    public void PendingList_OldEntryWithoutVersion_TakesItFromTheInstalledCopyOfTheSameFile()
    {
        var harness = new Harness(_root);
        var folder = LibraryModFromNexusFile(harness, modId: 500, fileId: 20); // records version "2.0"
        AddActivatedDownload(harness, "Homestead 2.zip", 500, 20, DateTimeOffset.UtcNow, new PendingDownloadActivatedItem(folder, PendingDownloadActivationKind.Library));
        AddActivatedDownload(harness, "Homestead 1.zip", 500, 10, DateTimeOffset.UtcNow.AddDays(-1), new PendingDownloadActivatedItem(folder, PendingDownloadActivationKind.Library));
        AddActivatedDownload(harness, "Pet Commander.zip", 375, 3188, DateTimeOffset.UtcNow, new PendingDownloadActivatedItem("PetCommander", PendingDownloadActivationKind.Ue4ssMod));
        harness.Ue4ssMeta.Save("PetCommander", new Ue4ssModMeta { NexusModId = 375, NexusFileId = 3188, NexusFileVersion = "3.1" });

        var vm = harness.Build();

        Assert.Equal("2.0", vm.PendingDownloads.Single(p => p.FileId == 20).FileVersion);
        Assert.Equal("3.1", vm.PendingDownloads.Single(p => p.FileId == 3188).FileVersion);
        // The other file of the mod is a different version: never given the installed one's.
        Assert.Null(vm.PendingDownloads.Single(p => p.FileId == 10).FileVersion);
        Assert.Equal("2.0", harness.PendingStore.Entries.Single(e => e.FileId == 20).FileVersion);
    }

    /// <summary>Pet Commander with files 3000 → 3048 → 3188 (3188 installed) and a separate variant 3100 that once used the same folder.</summary>
    private Harness PetCommanderWithThreeVersions(bool confirm)
    {
        var harness = PetCommanderUpdatedTo31(new FakeDialogService(confirmResult: confirm));
        harness.NexusApi.FileSet = new NexusModFileSet(
            [NexusFile(3188), NexusFile(3100)],
            [new NexusFileUpdate(3000, 3048), new NexusFileUpdate(3048, 3188)]);
        var start = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        AddActivatedDownload(harness, "Pet Commander 1.zip", 375, 3000, start, new PendingDownloadActivatedItem("PetCommander", PendingDownloadActivationKind.Ue4ssMod));
        AddActivatedDownload(harness, "Pet Commander Lite.zip", 375, 3100, start.AddDays(2), new PendingDownloadActivatedItem("PetCommander", PendingDownloadActivationKind.Ue4ssMod));
        return harness;
    }

    [Fact]
    public async Task DiscardOlderVersions_DeletesOnlyOlderVersionsOfTheInstalledFile_AfterAsking()
    {
        var harness = PetCommanderWithThreeVersions(confirm: true);
        var vm = harness.Build();
        var path = (int fileId) => harness.PendingStore.Entries.Single(e => e.FileId == fileId).LocalFilePath;
        var olderPaths = new[] { path(3000), path(3048) };
        var newerPath = path(3188);
        var variantPath = path(3100);
        var size = BackupCleanup.FormatSize(olderPaths.Sum(p => new FileInfo(p).Length));

        await vm.DiscardOlderVersionsCommand.ExecuteAsync(null);

        var question = Assert.Single(harness.Dialogs!.ConfirmMessages);
        Assert.Contains("2 files", question);
        Assert.Contains(size, question);
        Assert.All(olderPaths, p => Assert.False(File.Exists(p)));
        Assert.True(File.Exists(newerPath));
        Assert.True(File.Exists(variantPath));
        Assert.Equal([3100, 3188], harness.PendingStore.Entries.Select(e => e.FileId).Order());
    }

    [Fact]
    public async Task DiscardOlderVersions_No_DeletesNothing()
    {
        var harness = PetCommanderWithThreeVersions(confirm: false);
        var vm = harness.Build();

        await vm.DiscardOlderVersionsCommand.ExecuteAsync(null);

        Assert.Single(harness.Dialogs!.ConfirmMessages);
        Assert.Equal(4, harness.PendingStore.Entries.Count);
        Assert.All(harness.PendingStore.Entries, e => Assert.True(File.Exists(e.LocalFilePath)));
    }

    [Fact]
    public async Task RemoveUnusedFiles_DeletesOnlyFilesNoDownloadPointsAt_AfterAsking()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: true) };
        var tracked = harness.AddDownload("Tracked.zip", WriteWirelessPowerLayout);
        var stray = Path.Combine(harness.PendingDownloadsDirectory, "Stray.zip");
        File.WriteAllText(stray, "left over");
        var part = PartFile(harness, "Running.zip.0123abcd.part", DateTime.UtcNow);
        var vm = harness.Build();

        Assert.Equal(1, vm.UnusedFileCount);
        await vm.RemoveUnusedFilesCommand.ExecuteAsync(null);

        Assert.Contains("1 file", Assert.Single(harness.Dialogs!.ConfirmMessages));
        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(tracked.LocalFilePath));
        Assert.True(File.Exists(part));
        Assert.Equal(0, vm.UnusedFileCount);
    }

    [Fact]
    public async Task RemoveUnusedFiles_No_DeletesNothing()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: false) };
        var stray = Path.Combine(harness.PendingDownloadsDirectory, "Stray.zip");
        File.WriteAllText(stray, "left over");
        var vm = harness.Build();

        await vm.RemoveUnusedFilesCommand.ExecuteAsync(null);

        Assert.True(File.Exists(stray));
    }
}
