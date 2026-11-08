using System.IO;
using System.IO.Compression;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Older downloads of a mod that was updated since (the real Pet Commander case: files 3048 and 3188 of mod 375).</summary>
public sealed partial class DownloadsViewModelTests
{
    /// <summary>A download recorded in the store at a given time, with what it installed.</summary>
    private void AddActivatedDownload(Harness harness, string fileName, int modId, int fileId, DateTimeOffset downloadedAt, params PendingDownloadActivatedItem[] items)
    {
        var path = Path.Combine(harness.PendingDownloadsDirectory, fileName);
        using (var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
        {
            WriteEntry(archive, "PetCommander/Scripts/main.lua", $"-- file {fileId}");
        }

        harness.PendingStore.Add(new PendingDownloadEntry { ModId = modId, FileId = fileId, FileName = fileName, LocalFilePath = path, DownloadedAtUtc = downloadedAt });
        harness.PendingStore.SetActivation(modId, fileId, items, []);
    }

    /// <summary>Pet Commander 2.x (file 3048: data half + Lua half) and 3.1 (file 3188: the Lua half only, same folder), 3.1 installed.</summary>
    private Harness PetCommanderUpdatedTo31(FakeDialogService? dialogs)
    {
        var harness = new Harness(_root) { Dialogs = dialogs };
        harness.NexusApi.FileSet = new NexusModFileSet([NexusFile(3188)], [new NexusFileUpdate(3048, 3188)]);
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        AddActivatedDownload(harness, "Pet Commander 2.zip", 375, 3048, start,
            new PendingDownloadActivatedItem("Pet Commander Data", PendingDownloadActivationKind.Library),
            new PendingDownloadActivatedItem("PetCommander", PendingDownloadActivationKind.Ue4ssMod));
        AddActivatedDownload(harness, "Pet Commander 3.1.zip", 375, 3188, start.AddDays(10),
            new PendingDownloadActivatedItem("PetCommander", PendingDownloadActivationKind.Ue4ssMod));
        var staged = Path.Combine(_root, "Staged_UE4SS", "PetCommander", "Scripts");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "main.lua"), "-- 3.1");
        harness.Ue4ssMeta.Save("PetCommander", new Ue4ssModMeta { NexusModId = 375, NexusFileId = 3188 });
        return harness;
    }

    /// <summary>Regression: the older download read "Removed from Library" and showed by default, though a newer version of the mod is installed.</summary>
    [Fact]
    public void PendingList_OlderDownloadOfAnUpdatedMod_ReadsOlderVersionAndIsHidden()
    {
        var vm = PetCommanderUpdatedTo31(dialogs: null).Build();
        var older = vm.PendingDownloads.Single(p => p.FileId == 3048);
        var newer = vm.PendingDownloads.Single(p => p.FileId == 3188);

        Assert.True(older.IsSuperseded);
        Assert.Equal("Older version", older.StateLabel);
        Assert.False(newer.IsSuperseded);
        Assert.DoesNotContain(older, vm.VisiblePendingDownloads);

        vm.ShowInstalledDownloads = true;

        Assert.Contains(older, vm.VisiblePendingDownloads);
    }

    /// <summary>Regression: Reinstall on the older download swapped the installed newer version for it without asking.</summary>
    [Fact]
    public async Task Reinstall_OlderDownloadOverANewerVersion_AsksAndNoKeepsTheNewer()
    {
        var harness = PetCommanderUpdatedTo31(new FakeDialogService(confirmResult: false));
        var vm = harness.Build();

        var result = await vm.ActivateAsync(vm.PendingDownloads.Single(p => p.FileId == 3048));

        Assert.True(result.Cancelled);
        Assert.Contains(harness.Dialogs!.ConfirmMessages, m => m.Contains("older"));
        Assert.Equal(3188, harness.Ue4ssMeta.Load("PetCommander").NexusFileId);
        Assert.Equal("-- 3.1", File.ReadAllText(Path.Combine(harness.Ue4ss.GetFolderPath("PetCommander"), "Scripts", "main.lua")));
    }

    /// <summary>With nobody to ask, an older version is never put over a newer one.</summary>
    [Fact]
    public async Task Reinstall_OlderDownloadOverANewerVersion_WithNoDialog_IsRefused()
    {
        var harness = PetCommanderUpdatedTo31(dialogs: null);
        var vm = harness.Build();

        var result = await vm.ActivateAsync(vm.PendingDownloads.Single(p => p.FileId == 3048));

        Assert.True(result.Cancelled);
        Assert.Equal(3188, harness.Ue4ssMeta.Load("PetCommander").NexusFileId);
    }

    [Fact]
    public async Task Reinstall_OlderDownloadOverANewerVersion_YesInstallsIt()
    {
        var harness = PetCommanderUpdatedTo31(new FakeDialogService(confirmResult: true));
        var vm = harness.Build();

        var result = await vm.ActivateAsync(vm.PendingDownloads.Single(p => p.FileId == 3048));

        Assert.True(result.Succeeded);
        Assert.Equal(3048, harness.Ue4ssMeta.Load("PetCommander").NexusFileId);
    }

    private string LibraryModFromNexusFile(Harness harness, int modId, int fileId)
    {
        var pak = Path.Combine(_root, "Source", "Homestead_P.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(pak)!);
        File.WriteAllBytes(pak, TestPaks.Bytes("homestead"));
        var folder = harness.Library.ImportPak(pak, source: "Nexus", nexusModId: modId).FolderName;
        harness.Library.SetNexusFile(folder, fileId, "2.0");
        return folder;
    }

    /// <summary>Regression: Reinstall original took the newest kept download listing the folder, which could be an older file of the mod.</summary>
    [Fact]
    public void FindOriginalDownload_PrefersTheDownloadOfTheInstalledFile()
    {
        var harness = new Harness(_root);
        var folder = LibraryModFromNexusFile(harness, modId: 500, fileId: 20);
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        AddActivatedDownload(harness, "Homestead 2.zip", 500, 20, start, new PendingDownloadActivatedItem(folder, PendingDownloadActivationKind.Library));
        AddActivatedDownload(harness, "Homestead 1.zip", 500, 10, start.AddDays(1), new PendingDownloadActivatedItem(folder, PendingDownloadActivationKind.Library));
        var vm = harness.Build();

        Assert.Equal(20, vm.FindOriginalDownload(folder)?.FileId);
    }

    /// <summary>Regression: with the installed file's download discarded, Reinstall original installed an older file in its place.</summary>
    [Fact]
    public void FindOriginalDownload_TheInstalledFilesDownloadWasDiscarded_FindsNone()
    {
        var harness = new Harness(_root);
        var folder = LibraryModFromNexusFile(harness, modId: 500, fileId: 20);
        AddActivatedDownload(harness, "Homestead 1.zip", 500, 10, DateTimeOffset.UtcNow, new PendingDownloadActivatedItem(folder, PendingDownloadActivationKind.Library));
        var vm = harness.Build();

        Assert.Null(vm.FindOriginalDownload(folder));
    }

    /// <summary>A copy with no recorded file (installed before Starlink recorded it) still finds its download as before.</summary>
    [Fact]
    public void FindOriginalDownload_NoRecordedFile_FindsTheDownloadListingTheFolder()
    {
        var harness = new Harness(_root);
        var pak = Path.Combine(_root, "Source", "Old_P.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(pak)!);
        File.WriteAllBytes(pak, TestPaks.Bytes("old"));
        var folder = harness.Library.ImportPak(pak, source: "Nexus", nexusModId: 500).FolderName;
        AddActivatedDownload(harness, "Old.zip", 500, 10, DateTimeOffset.UtcNow, new PendingDownloadActivatedItem(folder, PendingDownloadActivationKind.Library));
        var vm = harness.Build();

        Assert.Equal(10, vm.FindOriginalDownload(folder)?.FileId);
    }
}
