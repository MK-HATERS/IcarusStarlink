using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    /// <summary>
    /// Regression: the installed merge's manifest lists an EXMOD by its declared name, so a renamed
    /// mod (ticked and installed) kept reading "Needs Merge &amp; Install" and never left the pending bar.
    /// </summary>
    [Fact]
    public void InstallStatus_ARenamedExmod_IsInGameByItsDeclaredName()
    {
        var installed = new HashSet<string>(["MkHaters Bags"], StringComparer.OrdinalIgnoreCase);

        Assert.True(LibraryViewModel.IsInInstalledMerge(false, [], "MkHaters Bags", "Bags (mine)", "MkHaters_Bags", installed));
        Assert.False(LibraryViewModel.IsInInstalledMerge(false, [], "Other Mod", "Bags (mine)", "MkHaters_Bags", installed));
        // An opaque pak is still matched by its pak file names only.
        Assert.True(LibraryViewModel.IsInInstalledMerge(true, ["MkHaters Bags.pak"], null, "Renamed", "Folder", installed));
    }

    /// <summary>
    /// Regression: the convert worked (the .pak became an editable mod), but marking it converted
    /// in its locked Library info file then said "Couldn't convert" and told no other page.
    /// </summary>
    [Fact]
    public async Task ConvertToExmodCommand_LibraryInfoFileLockedAfterTheWrite_StillSaysConverted()
    {
        var harness = new TestHarness();
        var root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_Convert");
        var modFolder = Path.Combine(root, "Extracted_Mods", "Bags");
        Directory.CreateDirectory(modFolder);
        File.WriteAllBytes(Path.Combine(modFolder, "Bags.pak"), [1, 2, 3]);
        var repository = new FolderLibraryRepository(
            Path.Combine(root, "Extracted_Mods"), Path.Combine(root, "Library_Meta"), Path.Combine(root, "Backups", "Mods"),
            NullLogger<FolderLibraryRepository>.Instance);
        repository.UpdateMetadata("Bags", isPinned: false, isFavorite: false, notes: "");
        var vm = BuildOver(harness, repository, new FixedPrebuiltPakToExmodConverter("Bags"));
        var item = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Bags");

        try
        {
            using (new FileStream(Path.Combine(root, "Library_Meta", "Bags.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await vm.ConvertToExmodCommand.ExecuteAsync(item);
            }

            Assert.StartsWith("Converted 'Bags'", vm.StatusMessage);
            Assert.Contains(harness.ActivityLog.Entries, e => e.Kind == ActivityEntryKind.Success && e.Message.Contains("Converted 'Bags'"));
            Assert.False(Assert.Single(repository.GetAll()).IsOpaquePak);
        }
        finally
        {
            repository.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The harness's page over another repository and pak converter (the rest are the harness's fakes).</summary>
    private static LibraryViewModel BuildOver(TestHarness h, ILibraryRepository repository, IPrebuiltPakToExmodConverter converter)
    {
        var fixService = new ModFixService(repository, h.DataFolder, decisionsDirectory: h.FixDecisionsDirectory);
        var replacement = new ModReplacementService(
            repository, h.Ue4ssModRepository, h.Ue4ssModMetaStore, h.Ue4ssModStateService, h.SettingsService, h.BackupDirectory,
            h.DialogService, fixService, h.PrebuiltPakSourceStore);
        var downloads = new DownloadsViewModel(
            h.DaedalusClient, h.Jimk72Client, h.GitHubRepoDateClient, repository, h.Ue4ssModRepository, h.Ue4ssModMetaStore,
            h.SettingsService, h.NexusApiClient, h.CredentialStore, h.PendingDownloadStore, h.DownloadHttpClient,
            new PerformanceTracker(h.SettingsService, h.LogsDirectory), h.ActivityLog, h.ActiveDownloadsTracker,
            h.PendingDownloadsDirectory, h.PrebuiltPakImporter, h.DataFolder, dialogService: h.DialogService, modReplacement: replacement);

        return new LibraryViewModel(
            repository, h.Ue4ssModRepository, h.Ue4ssModStateService, h.Ue4ssModMetaStore, h.Ue4ssLoaderInstallService,
            h.SettingsService, h.UnrealPakService, h.TextureDecoder, h.StaticMeshDecoder, h.SkeletalMeshDecoder, h.SoundDecoder,
            h.MaterialDecoder, h.OpaquePakAssetPreviewService, h.NexusApiClient, h.CredentialStore,
            _ => throw new NotSupportedException("Not exercised by these tests."),
            h.ActivityLog, h.DownloadHttpClient, h.PendingDownloadStore, h.ModVersionComparer, downloads,
            () => throw new NotSupportedException("Not exercised by these tests."),
            h.ActiveDownloadsTracker,
            () => throw new NotSupportedException("Not exercised by these tests."),
            h.BackupDirectory, h.DataFolder, h.PrebuiltPakImporter, converter, h.PrebuiltPakSourceStore,
            h.ThumbnailCacheDirectory, h.PakPreviewCacheDirectory, h.DialogService)
        {
            SharedFixService = fixService,
        };
    }

    /// <summary>Converts any pak into the same blank EXMOD with placeholder metadata.</summary>
    private sealed class FixedPrebuiltPakToExmodConverter(string fileName) : IPrebuiltPakToExmodConverter
    {
        public Task<PrebuiltPakConversionResult?> TryConvertAsync(
            string pakFilePath, string dataFolder, string unrealPakExePath, string name, string author, MergeReport report,
            GameValueHistory? gameHistory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<PrebuiltPakConversionResult?>(new PrebuiltPakConversionResult(
                new ExmodPackageContents(new ExmodPackage { Name = name, Author = "Unknown", Version = "1.0", Description = "", FileName = fileName, Rows = [] }, []),
                HasAuthorDeclaredMetadata: false));
    }
}
