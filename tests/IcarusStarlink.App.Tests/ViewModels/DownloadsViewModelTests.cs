using System.IO;
using System.IO.Compression;
using System.Net.Http;
using IcarusStarlink.App.Utilities;
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
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.Storage.Library;
using IcarusStarlink.Storage.Nexus;
using IcarusStarlink.Storage.Ue4ss;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// End-to-end through the REAL storage layer (Library repository, UE4SS staging, pending-download
/// store, UE4SS meta store) with only the network and UnrealPak faked — the Downloads view model
/// had no tests at all, and three real bugs in one week lived in exactly this activation path.
/// The fixture zips mirror the layouts of real Nexus downloads (see the audit memory): a
/// game-folder bundle with a UE4SS mod plus two paks, and a plain zip wrapping an EXMODZ.
/// </summary>
public sealed class DownloadsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"Downloads_{Guid.NewGuid():N}");

    public DownloadsViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Harness
    {
        public FolderLibraryRepository Library { get; }
        public Ue4ssModRepository Ue4ss { get; }
        public Ue4ssModMetaStore Ue4ssMeta { get; }
        public PendingDownloadStore PendingStore { get; }
        public FakePrebuiltPakImporter PakImporter { get; }
        public FakeNexusApiClient NexusApi { get; } = new();
        public ActivityLog ActivityLog { get; } = new();
        public string PendingDownloadsDirectory { get; }

        public Harness(string root)
        {
            Library = new FolderLibraryRepository(Path.Combine(root, "Extracted_Mods"), Path.Combine(root, "Library_Meta"), Path.Combine(root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
            Ue4ss = new Ue4ssModRepository(Path.Combine(root, "Staged_UE4SS"));
            Ue4ssMeta = new Ue4ssModMetaStore(Path.Combine(root, "Ue4ss_Meta"), NullLogger<Ue4ssModMetaStore>.Instance);
            PendingStore = new PendingDownloadStore(root, NullLogger<PendingDownloadStore>.Instance);
            PakImporter = new FakePrebuiltPakImporter(Library);
            PendingDownloadsDirectory = Path.Combine(root, "Pending_Downloads");
            Directory.CreateDirectory(PendingDownloadsDirectory);
        }

        public DownloadsViewModel Build()
        {
            var settings = new FakeSettingsService();
            return new DownloadsViewModel(
                new FakeCatalogClient(), new FakeCatalogClient(), new FakeGitHubRepoDateClient(),
                Library, Ue4ss, Ue4ssMeta, settings, NexusApi, new FakeCredentialStore(), PendingStore,
                new HttpClient(), new PerformanceTracker(settings, Path.Combine(PendingDownloadsDirectory, "..", "Perf")),
                ActivityLog, new ActiveDownloadsTracker(), PendingDownloadsDirectory, PakImporter, Path.Combine(PendingDownloadsDirectory, "..", "Data"));
        }

        /// <summary>A downloaded file as the real nxm:// pipeline leaves it: on disk under Pending_Downloads and recorded in the store, never activated.</summary>
        public PendingDownloadItemViewModel AddDownload(string fileName, Action<ZipArchive> writeEntries, int modId = 361, int fileId = 2739)
        {
            var path = Path.Combine(PendingDownloadsDirectory, fileName);
            using (var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            {
                writeEntries(archive);
            }

            PendingStore.Add(new PendingDownloadEntry { ModId = modId, FileId = fileId, FileName = fileName, LocalFilePath = path, DownloadedAtUtc = DateTimeOffset.UtcNow });
            return new PendingDownloadItemViewModel(PendingStore.Entries.Single(e => e.ModId == modId && e.FileId == fileId), Library, Ue4ss);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static void WriteWirelessPowerLayout(ZipArchive archive)
    {
        WriteEntry(archive, "README_WirelessPower.txt", "WirelessPower\n\nREQUIREMENTS\n- UE4SS (experimental build): https://x\n- ArcFission Library 1.2.0 or newer (separate download)\n\nINSTALL\n1. etc\n");
        WriteEntry(archive, "Icarus/Binaries/Win64/ue4ss/Mods/WirelessPower/enabled.txt", "");
        WriteEntry(archive, "Icarus/Binaries/Win64/ue4ss/Mods/WirelessPower/Scripts/main.lua", "print('wp')");
        WriteEntry(archive, "Icarus/Content/Paks/mods/ZZ_WirelessPower_Icon_P.pak", "pak1");
        WriteEntry(archive, "Icarus/Content/Paks/mods/ZZ_WirelessPower_Model_P.pak", "pak2");
    }

    [Fact]
    public async Task Activate_GameFolderBundle_StagesTheUe4ssModImportsBothPaksRecordsAllPartsAndRequirements()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        var item = harness.AddDownload("WirelessPower 1.0.0 361 1 2026-10-12T13-49Z UAfrx4UUt.zip", WriteWirelessPowerLayout);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Contains("WirelessPower", harness.Ue4ss.GetAll());
        Assert.True(File.Exists(Path.Combine(harness.Ue4ss.GetFolderPath("WirelessPower"), "Scripts", "main.lua")));
        Assert.Equal(2, harness.PakImporter.ImportedPaks.Count);
        Assert.Equal(361, harness.Ue4ssMeta.Load("WirelessPower").NexusModId);

        var stored = harness.PendingStore.Entries.Single();
        Assert.Equal(3, stored.ActivatedItems.Count);
        Assert.Contains(stored.ActivatedItems, i => i.Kind == PendingDownloadActivationKind.Ue4ssMod && i.FolderName == "WirelessPower");
        Assert.Equal(2, stored.ActivatedItems.Count(i => i.Kind == PendingDownloadActivationKind.Library));
        Assert.Contains(stored.Requirements, r => r.StartsWith("ArcFission Library"));

        Assert.Contains("NOT installed", vm.PendingDownloadStatusMessage);
        Assert.Contains(harness.ActivityLog.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message.Contains("ArcFission Library"));
        Assert.Contains("Needs:", item.RequirementsSummary);
    }

    [Fact]
    public async Task Activate_RequirementAlreadyStaged_IsReportedAsSatisfied()
    {
        var harness = new Harness(_root);
        Directory.CreateDirectory(Path.Combine(_root, "Staged_UE4SS", "ArcFissionLibrary", "Scripts"));
        File.WriteAllText(Path.Combine(_root, "Staged_UE4SS", "ArcFissionLibrary", "Scripts", "main.lua"), "-- lib");
        var vm = harness.Build();
        var item = harness.AddDownload("WirelessPower.zip", WriteWirelessPowerLayout);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.DoesNotContain("NOT installed", vm.PendingDownloadStatusMessage);
        Assert.Contains("ArcFission Library 1.2.0 or newer (separate download) ✓", vm.PendingDownloadStatusMessage);
    }

    /// <summary>The real "doubled in the UE4SS tab" report: the first activation was applied into the game folder, so a Reinstall saw its own name as taken and landed as "_2".</summary>
    [Fact]
    public async Task Reinstall_WhenTheEarlierCopyIsAppliedInTheGame_ReusesItsOwnNameInsteadOfASuffixedCopy()
    {
        var harness = new Harness(_root);
        var gameMods = Path.Combine(_root, "Game", "Content", "Binaries", "Win64", "ue4ss", "Mods");
        var settings = new FakeSettingsService();
        settings.Current.IcarusContentPath = Path.Combine(_root, "Game", "Content");
        var vm = new DownloadsViewModel(
            new FakeCatalogClient(), new FakeCatalogClient(), new FakeGitHubRepoDateClient(),
            harness.Library, harness.Ue4ss, harness.Ue4ssMeta, settings, harness.NexusApi, new FakeCredentialStore(), harness.PendingStore,
            new HttpClient(), new PerformanceTracker(settings, Path.Combine(_root, "Perf")),
            harness.ActivityLog, new ActiveDownloadsTracker(), harness.PendingDownloadsDirectory, harness.PakImporter, Path.Combine(_root, "Data"));
        var item = harness.AddDownload("WirelessPower.zip", WriteWirelessPowerLayout);
        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);
        Assert.True(item.IsInstalled);

        // "Apply" on the UE4SS tab: the staged copy now also lives in the game's Mods folder.
        Directory.CreateDirectory(Path.Combine(gameMods, "WirelessPower"));
        File.WriteAllText(Path.Combine(gameMods, "WirelessPower", "enabled.txt"), "");

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Equal(["WirelessPower"], harness.Ue4ss.GetAll());
        Assert.True(item.IsInstalled);
    }

    [Fact]
    public async Task Activate_ZipWrappingAnExmodz_LandsAsALibraryMod()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        var exmodJson = """{"name":"Steadfast","author":"Ferani","version":"1.0","description":"D","fileName":"Ferani_Steadfast_Structures","Rows":[]}""";
        byte[] innerExmodz;
        using (var inner = new MemoryStream())
        {
            using (var innerArchive = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(innerArchive, "Extracted Mods/Ferani_Steadfast_Structures.EXMOD", exmodJson);
            }

            innerExmodz = inner.ToArray();
        }

        var item = harness.AddDownload("Ferani_Steadfast_Structures_EXMODZ 339 1 2026-09-25T13-25Z qdbjUuqbO.zip", archive =>
        {
            using var stream = archive.CreateEntry("Ferani_Steadfast_Structures.EXMODZ").Open();
            stream.Write(innerExmodz);
        }, modId: 339, fileId: 2471);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        var entry = Assert.Single(harness.Library.GetAll());
        Assert.Equal("Steadfast", entry.Name);
        Assert.Equal(339, entry.NexusModId);
        Assert.Equal("1", entry.NexusVersion);
        Assert.True(item.IsInstalled);
    }

    [Fact]
    public void PendingList_HidesFullyInstalledDownloadsUnlessAsked()
    {
        var harness = new Harness(_root);
        harness.AddDownload("a.zip", a => WriteEntry(a, "Scripts/main.lua", "x"), modId: 1, fileId: 1);
        harness.AddDownload("b.zip", a => WriteEntry(a, "Scripts/main.lua", "x"), modId: 2, fileId: 2);
        Directory.CreateDirectory(Path.Combine(_root, "Staged_UE4SS", "ModB"));
        harness.PendingStore.SetActivation(2, 2, [new PendingDownloadActivatedItem("ModB", PendingDownloadActivationKind.Ue4ssMod)], []);
        var vm = harness.Build();

        Assert.Equal(2, vm.PendingDownloads.Count);
        Assert.Single(vm.VisiblePendingDownloads);
        Assert.Equal(1, vm.InstalledDownloadCount);

        vm.ShowInstalledDownloads = true;

        Assert.Equal(2, vm.VisiblePendingDownloads.Count);
    }

    // --- Fakes: only the network and UnrealPak; everything else above is the real storage layer ---

    /// <summary>Stands in for UnrealPak only: the pak is imported into the REAL Library repository as an opaque entry, exactly as the real importer does when conversion isn't possible.</summary>
    private sealed class FakePrebuiltPakImporter(ILibraryRepository library) : IPrebuiltPakImporter
    {
        public List<string> ImportedPaks { get; } = [];

        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default)
        {
            ImportedPaks.Add(Path.GetFileName(pakFilePath));
            return Task.FromResult(library.ImportPak(pakFilePath, source: source, nexusModId: nexusModId));
        }
    }

    private sealed class FakeNexusApiClient : INexusApiClient
    {
        public Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default) => Task.FromResult<NexusUserInfo?>(null);
        public Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            Task.FromResult<NexusModInfo?>(new NexusModInfo(modId, $"Nexus Mod {modId}", "author", "summary", "1", null));
        public Task<IReadOnlyList<NexusModInfo>> GetModListAsync(string apiKey, string gameDomain, NexusModList list, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NexusModInfo>> SearchModsAsync(string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusModPage> ListAllModsAsync(string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusEndorsementStatus> SetEndorsementAsync(string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public void Save(string target, string secret) { }
        public string? Read(string target) => "a-nexus-key";
        public void Delete(string target) { }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public bool Save() => true;
    }

    private sealed class FakeCatalogClient : IDaedalusCatalogClient, IJimk72CatalogClient
    {
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CatalogEntry>>([]);
    }

    private sealed class FakeGitHubRepoDateClient : IGitHubRepoDateClient
    {
        public Task<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>> FetchPushedDatesAsync(IReadOnlyCollection<(string Owner, string Repo)> repos, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>>(new Dictionary<(string, string), DateTimeOffset>());
    }
}
