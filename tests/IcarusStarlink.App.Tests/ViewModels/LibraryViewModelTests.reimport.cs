using System.IO;
using System.IO.Compression;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Tests.ViewModels;

// Importing a mod that is already in the Library: Replace it / Keep both / Cancel.
public sealed partial class LibraryViewModelTests
{
    /// <summary>A dropped folder holding one .EXMOD named <paramref name="fileName"/>.</summary>
    private static string DroppedExmodFolder(string fileName)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), fileName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName + ".EXMOD"), "{}");
        return folder;
    }

    private static LibraryEntry ImportedEntry(string folderName, string fileName, string? source, int? nexusModId) => new()
    {
        FolderName = folderName, Name = folderName, Author = "Someone", Version = "2.0", Description = "D", FileName = fileName,
        Source = source, NexusModId = nexusModId, ImportedAtUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>Regression: dropping a mod that is already in the Library silently added a "_2" copy that kept none of the old copy's pin, notes or Nexus link.</summary>
    [Fact]
    public async Task ImportPaths_SameExmodFileName_AsksAndReplaceKeepsMeta()
    {
        var harness = new TestHarness();
        harness.AddMod("MyMod", isPinned: true, notes: "keep me", source: "Nexus", nexusModId: 123);
        harness.DialogService.ReplaceAnswer = ReplaceExistingAnswer.Replace;
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => ImportedEntry("MyMod", "MyMod", source, nexusId);
        var folder = DroppedExmodFolder("MyMod");
        try
        {
            var vm = harness.Build();

            await vm.ImportPaths([folder]);

            Assert.Contains("'MyMod' (data mod)", Assert.Single(harness.DialogService.ReplaceExistingMessages));
            Assert.Contains("MyMod", harness.Repository.BackupModCalls);
            var entry = Assert.Single(harness.Repository.GetAll());
            Assert.Equal("2.0", entry.Version);
            Assert.True(entry.IsPinned);
            Assert.Equal("keep me", entry.Notes);
            Assert.Equal("Nexus", entry.Source);
            Assert.Equal(123, entry.NexusModId);
            Assert.Equal("Imported 1 mod.", vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task ImportPaths_KeepBoth_AddsSecondCopy()
    {
        var harness = new TestHarness();
        harness.AddMod("MyMod", isPinned: true, notes: "keep me");
        harness.DialogService.ReplaceAnswer = ReplaceExistingAnswer.KeepBoth;
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => ImportedEntry("MyMod_2", "MyMod", source, nexusId);
        var folder = DroppedExmodFolder("MyMod");
        try
        {
            var vm = harness.Build();

            await vm.ImportPaths([folder]);

            Assert.Single(harness.DialogService.ReplaceExistingMessages);
            Assert.Empty(harness.Repository.BackupModCalls);
            Assert.Empty(harness.Repository.DeleteCalls);
            Assert.Equal(["MyMod", "MyMod_2"], harness.Repository.GetAll().Select(e => e.FolderName).Order());
            Assert.True(harness.Repository.GetAll().Single(e => e.FolderName == "MyMod").IsPinned);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public async Task ImportPaths_CancelOnTheQuestion_ChangesNothing()
    {
        var harness = new TestHarness();
        harness.AddMod("MyMod");
        harness.DialogService.ReplaceAnswer = ReplaceExistingAnswer.Cancel;
        var folder = DroppedExmodFolder("MyMod");
        try
        {
            var vm = harness.Build();

            await vm.ImportPaths([folder]);

            Assert.Single(harness.DialogService.ReplaceExistingMessages);
            Assert.Empty(harness.Repository.ImportCalls);
            Assert.Empty(harness.Repository.DeleteCalls);
            Assert.Equal("MyMod", Assert.Single(harness.Repository.GetAll()).FolderName);
            Assert.Equal("Import cancelled — nothing was changed.", vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>Two copies already: the user keeps several on purpose, so the import adds another without asking which to replace.</summary>
    [Fact]
    public async Task ImportPaths_TwoMatches_DoesNotAsk()
    {
        var harness = new TestHarness();
        harness.AddMod("MyMod");
        harness.AddMod("MyMod_2").FileName = "MyMod";
        harness.DialogService.ReplaceAnswer = ReplaceExistingAnswer.Replace;
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => ImportedEntry("MyMod_3", "MyMod", source, nexusId);
        var folder = DroppedExmodFolder("MyMod");
        try
        {
            var vm = harness.Build();

            await vm.ImportPaths([folder]);

            Assert.Empty(harness.DialogService.ReplaceExistingMessages);
            Assert.Empty(harness.Repository.DeleteCalls);
            Assert.Equal(3, harness.Repository.GetAll().Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>Deliberate variants ("Stacks_2x" beside "Stacks_5x") are different mods, not copies.</summary>
    [Fact]
    public async Task ImportPaths_OtherVariantOfTheSameMod_DoesNotAsk()
    {
        var harness = new TestHarness();
        var existing = harness.AddMod("Stacks_5x");
        existing.VariantGroup = "Stacks";
        existing.Variant = "5x";
        harness.DialogService.ReplaceAnswer = ReplaceExistingAnswer.Replace;
        harness.Repository.ImportHandler = (path, source, nexusId, catalogId) => ImportedEntry("Stacks_2x", "Stacks_2x", source, nexusId);
        var folder = DroppedExmodFolder("Stacks_2x");
        try
        {
            var vm = harness.Build();

            await vm.ImportPaths([folder]);

            Assert.Empty(harness.DialogService.ReplaceExistingMessages);
            Assert.Equal(["Stacks_2x", "Stacks_5x"], harness.Repository.GetAll().Select(e => e.FolderName).Order());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    /// <summary>The UE4SS half: an archive holding a UE4SS mod of the same folder name as a staged one replaces it (backed up, deleted, imported again under its name).</summary>
    [Fact]
    public async Task ImportPaths_SameUe4ssModName_AsksAndReplaceSwapsTheStagedCopy()
    {
        var harness = new TestHarness();
        harness.Ue4ssModRepository.Staged.Add("CoolMod");
        Directory.CreateDirectory(Path.Combine(harness.Ue4ssModRepository.StagingRoot, "CoolMod", "Scripts"));
        File.WriteAllText(Path.Combine(harness.Ue4ssModRepository.StagingRoot, "CoolMod", "Scripts", "main.lua"), "print('old')");
        harness.Ue4ssModMetaStore.Metas["CoolMod"] = new() { NexusModId = 77 };
        harness.Ue4ssModRepository.Result = "CoolMod";
        harness.DialogService.ReplaceAnswer = ReplaceExistingAnswer.Replace;
        var zipDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(zipDirectory);
        var zipPath = Path.Combine(zipDirectory, "CoolMod.zip");
        using (var archive = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("Scripts/main.lua").Open();
            stream.Write("print('new')"u8.ToArray());
        }

        try
        {
            var vm = harness.Build();

            await vm.ImportPaths([zipPath]);

            Assert.Contains("'CoolMod' (UE4SS mod)", Assert.Single(harness.DialogService.ReplaceExistingMessages));
            Assert.Equal(["CoolMod"], harness.Ue4ssModRepository.DeleteCalls);
            Assert.Contains("CoolMod", harness.Ue4ssModMetaStore.DeleteCalls);
            Assert.Equal(1, harness.Ue4ssModRepository.ImportFromFolderCallCount);
            Assert.True(Directory.Exists(Path.Combine(harness.BackupDirectory, "UE4SS")), "the old copy is backed up first");
            Assert.Contains(harness.ActivityLog.Entries, e => e.Message.StartsWith("Replaced 'CoolMod' (UE4SS mod)", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(zipDirectory, recursive: true);
        }
    }

    /// <summary>A bare .pak with the same pak file as an imported opaque pak: Replace goes through the pak importer with the old Nexus link.</summary>
    [Fact]
    public async Task ImportPaths_SamePakFile_ReplaceKeepsTheNexusLink()
    {
        var harness = new TestHarness();
        var existing = harness.AddMod("Cool_P", isOpaquePak: true, isPinned: true, source: "Nexus", nexusModId: 55);
        existing.PakFileNames = ["Cool_P.pak"];
        harness.DialogService.ReplaceAnswer = ReplaceExistingAnswer.Replace;
        var pakDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pakDirectory);
        var pakPath = Path.Combine(pakDirectory, "Cool_P.pak");
        File.WriteAllBytes(pakPath, TestPaks.Bytes(1, 2, 3, 4));
        harness.PrebuiltPakImporter.Result = new LibraryEntry
        {
            FolderName = "Cool_P", Name = "Cool_P", Author = "Unknown", Version = "1.0", Description = "", FileName = "Cool_P", IsOpaquePak = true,
        };
        try
        {
            var vm = harness.Build();

            await vm.ImportPaths([pakPath]);

            Assert.Single(harness.DialogService.ReplaceExistingMessages);
            Assert.Equal(["Cool_P"], harness.Repository.DeleteCalls);
            Assert.Equal(1, harness.PrebuiltPakImporter.ImportCallCount);
            Assert.Equal(55, harness.PrebuiltPakImporter.LastNexusModId);
            Assert.True(Assert.Single(harness.Repository.GetAll()).IsPinned);
        }
        finally
        {
            Directory.Delete(pakDirectory, recursive: true);
        }
    }
}
