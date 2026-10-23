using System.IO;
using System.IO.Compression;
using System.Net.Http;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
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
[Collection(MessengerCollection.Name)]
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

        public FakeDialogService? Dialogs { get; set; }

        /// <summary>The game's Content folder for the built view model's settings — null leaves the game path unset.</summary>
        public string? IcarusContentPath { get; set; }

        /// <summary>Both catalog sources (Daedalus and Jimk72) — a test can swap in one that counts fetches.</summary>
        public FakeCatalogClient Catalog { get; set; } = new();

        /// <summary>What any HTTP download returns — a catalog download test hands it the archive bytes.</summary>
        public byte[]? HttpResponseBytes { get; set; }

        public DownloadsViewModel Build()
        {
            var settings = new FakeSettingsService();
            settings.Current.IcarusContentPath = IcarusContentPath;
            var httpClient = HttpResponseBytes is null ? new HttpClient() : new HttpClient(new FixedBytesHandler(HttpResponseBytes));
            return new DownloadsViewModel(
                Catalog, Catalog, new FakeGitHubRepoDateClient(),
                Library, Ue4ss, Ue4ssMeta, settings, NexusApi, new FakeCredentialStore(), PendingStore,
                httpClient, new PerformanceTracker(settings, Path.Combine(PendingDownloadsDirectory, "..", "Perf")),
                ActivityLog, new ActiveDownloadsTracker(), PendingDownloadsDirectory, PakImporter, Path.Combine(PendingDownloadsDirectory, "..", "Data"),
                dialogService: Dialogs);
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

    /// <summary>
    /// Windows sends every game's nxm:// links to one app. A Stalker link reaching Starlink (no
    /// other manager to pass it to) used to be downloaded and installed as an Icarus mod.
    /// </summary>
    [Fact]
    public async Task FetchAndDownload_LinkForAnotherGame_IsRefusedAndNothingIsDownloaded()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();

        await vm.FetchAndDownloadAsync("nxm://stalker2heartofchornobyl/mods/12/files/34?key=k&expires=1");

        Assert.Contains("another game (stalker2heartofchornobyl)", vm.PendingDownloadStatusMessage);
        Assert.Empty(harness.PendingStore.Entries);
        Assert.Contains(harness.ActivityLog.Entries, entry => entry.Kind == ActivityEntryKind.Warning);
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
        Assert.Empty(harness.PakImporter.ImportedPaks);
        Assert.Equal(2, Assert.Single(harness.Library.GetAll()).PakFileNames.Count);
        Assert.Equal(361, harness.Ue4ssMeta.Load("WirelessPower").NexusModId);

        var stored = harness.PendingStore.Entries.Single();
        Assert.Equal(2, stored.ActivatedItems.Count);
        Assert.Contains(stored.ActivatedItems, i => i.Kind == PendingDownloadActivationKind.Ue4ssMod && i.FolderName == "WirelessPower");
        Assert.Equal(1, stored.ActivatedItems.Count(i => i.Kind == PendingDownloadActivationKind.Library));
        Assert.Contains(stored.Requirements, r => r.StartsWith("ArcFission Library"));

        Assert.Contains("NOT installed", vm.PendingDownloadStatusMessage);
        Assert.Contains(harness.ActivityLog.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message.Contains("ArcFission Library"));
        Assert.Contains("Needs:", item.RequirementsSummary);
    }

    /// <summary>A download Starlink can't sort out (loose .uasset files next to a Lua mod) — the import fails and only THEN the manual install opens, with the reason, pre-filled with Starlink's guesses.</summary>
    private static void WriteUnrecognisable(ZipArchive archive)
    {
        WriteEntry(archive, "Stray/BP_Thing.uasset", "asset");
        WriteEntry(archive, "LuaPart/Scripts/main.lua", "print('lua')");
        WriteEntry(archive, "readme.txt", "hi");
    }

    [Fact]
    public async Task Activate_Fails_ThenManualInstall_InstallsWhatTheUserKept()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        string? reasonShown = null;
        vm.ManualInstall = (root, name, reason, guesses) => { reasonShown = reason; return [.. guesses]; };
        var item = harness.AddDownload("Odd Mod 1.0.zip", WriteUnrecognisable);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Contains(".uasset", reasonShown);
        Assert.Contains("LuaPart", harness.Ue4ss.GetAll());
        Assert.Equal(361, harness.Ue4ssMeta.Load("LuaPart").NexusModId);
        Assert.Equal(2739, harness.Ue4ssMeta.Load("LuaPart").NexusFileId);
        Assert.Contains(harness.PendingStore.Entries.Single().ActivatedItems, i => i.FolderName == "LuaPart" && i.Kind == PendingDownloadActivationKind.Ue4ssMod);
        Assert.StartsWith("Installed manually", vm.PendingDownloadStatusMessage);
    }

    [Fact]
    public async Task Activate_Fails_AndManualInstallCancelled_KeepsTheFailureAndInstallsNothing()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        vm.ManualInstall = (_, _, _, _) => null;
        var item = harness.AddDownload("Odd Mod 1.0.zip", WriteUnrecognisable);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.StartsWith("Import failed", vm.PendingDownloadStatusMessage);
        Assert.Empty(harness.Ue4ss.GetAll());
        Assert.Empty(harness.PendingStore.Entries.Single().ActivatedItems);
    }

    /// <summary>Choosing nothing when asked which alternative to install is the user's decision, not a failure — no manual install pops up.</summary>
    [Fact]
    public async Task Activate_CancellingTheChoiceOfAlternatives_DoesNotOpenManualInstall()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        var manualOpened = false;
        vm.ManualInstall = (_, _, _, _) => { manualOpened = true; return null; };
        vm.ChooseArchive = (_, _) => null;
        var item = harness.AddDownload("Two.zip", archive =>
        {
            foreach (var inner in new[] { "A.zip", "B.zip" })
            {
                var bytes = new MemoryStream();
                using (var innerZip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteEntry(innerZip, "Scripts/main.lua", "print('x')");
                }

                using var entry = archive.CreateEntry(inner).Open();
                entry.Write(bytes.ToArray());
            }
        });

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.False(manualOpened);
        Assert.Contains("cancelled", vm.PendingDownloadStatusMessage);
    }

    private static void WriteTwoAlternatives(ZipArchive archive)
    {
        foreach (var inner in new[] { "A.zip", "B.zip" })
        {
            var bytes = new MemoryStream();
            using (var innerZip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(innerZip, "Scripts/main.lua", "print('x')");
            }

            using var entry = archive.CreateEntry(inner).Open();
            entry.Write(bytes.ToArray());
        }
    }

    /// <summary>A real review finding: Reinstall deleted the installed mod first, so cancelling the "which one?" question left the user with nothing.</summary>
    [Fact]
    public async Task Reinstall_CancellingTheChoiceOfAlternatives_KeepsTheInstalledMod()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        vm.ManualInstall = (_, _, _, _) => null;
        var item = harness.AddDownload("Two.zip", WriteTwoAlternatives);
        vm.ChooseArchive = (_, _) => 0;
        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);
        var installed = Assert.Single(harness.Ue4ss.GetAll());

        vm.ChooseArchive = (_, _) => null;
        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Contains("cancelled", vm.PendingDownloadStatusMessage);
        Assert.Equal([installed], harness.Ue4ss.GetAll());
        Assert.True(item.IsInstalled);
    }

    /// <summary>The real report: both of WirelessPower's paks were renamed to the Nexus title, so the Library showed three rows called "Wireless Power" and the user read the extras as a dependency fetched behind their back.</summary>
    [Fact]
    public async Task Activate_GameFolderBundle_LandsAsOneLibraryModHoldingBothPaks()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        var item = harness.AddDownload("WirelessPower 1.0.0 361 1 2026-10-12T13-49Z UAfrx4UUt.zip", WriteWirelessPowerLayout);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        var entry = Assert.Single(harness.Library.GetAll());
        Assert.Equal("Nexus Mod 361", entry.Name);
        Assert.Equal("WirelessPower", entry.FolderName);
        Assert.Equal(["ZZ_WirelessPower_Icon_P.pak", "ZZ_WirelessPower_Model_P.pak"], entry.PakFileNames);
        Assert.True(File.Exists(Path.Combine(harness.Library.GetFolderPath("WirelessPower"), "ZZ_WirelessPower_Model_P.pak")));
        Assert.Equal("Library mod + UE4SS mod", item.PartsSummary);
    }

    /// <summary>A mod organizer resolves a dependency at download time with a question. Yes runs the normal download pipeline for the match Nexus search returned.</summary>
    [Fact]
    public async Task Activate_WithAMissingRequirement_OffersToDownloadItAndYesStartsThatDownload()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: true) };
        harness.NexusApi.SearchResults.Add(new NexusModInfo(999, "ArcFission Tools", "someone", "", "1", null));
        harness.NexusApi.SearchResults.Add(new NexusModInfo(356, "ArcFission Library", "someone", "", "1.2.2", null));
        var vm = harness.Build();
        var item = harness.AddDownload("WirelessPower.zip", WriteWirelessPowerLayout);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        var question = Assert.Single(harness.Dialogs.ConfirmMessages);
        Assert.Contains("needs ArcFission Library 1.2.0 or newer", question);
        Assert.Contains("Download 'ArcFission Library' (Nexus mod #356)", question);
        Assert.Equal(["ArcFission Library"], harness.NexusApi.SearchQueries);
        Assert.Equal([356], harness.NexusApi.FileSetCalls);
    }

    [Fact]
    public async Task Activate_WithAMissingRequirement_DeclinedDownloadsNothingAndSaysSo()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: false) };
        harness.NexusApi.SearchResults.Add(new NexusModInfo(356, "ArcFission Library", "someone", "", "1.2.2", null));
        var vm = harness.Build();
        var item = harness.AddDownload("WirelessPower.zip", WriteWirelessPowerLayout);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Single(harness.Dialogs.ConfirmMessages);
        Assert.Empty(harness.NexusApi.FileSetCalls);
        Assert.Contains("Skipped 'ArcFission Library'", vm.PendingDownloadStatusMessage);
        Assert.True(item.CanGetRequirement, "the row keeps its Get button so the user can come back to it");
    }

    /// <summary>No match at all on Nexus: nothing is asked, and the row's own Get button is the fallback (which searches by name).</summary>
    [Fact]
    public async Task Activate_WithAMissingRequirementNexusDoesNotKnow_AsksNothing()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: true) };
        harness.NexusApi.SearchResults.Add(new NexusModInfo(5, "Something Unrelated", "someone", "", "1", null));
        var vm = harness.Build();
        var item = harness.AddDownload("WirelessPower.zip", WriteWirelessPowerLayout);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Empty(harness.Dialogs.ConfirmMessages);
        Assert.Empty(harness.NexusApi.FileSetCalls);
        Assert.Equal("Get 'ArcFission Library'", item.GetRequirementLabel);
    }

    /// <summary>The "find ArcFission to delete it too" misreading: once the download's parts are gone from the Library, the row says "Removed from Library" and offers Reinstall — not a dependency fetch.</summary>
    [Fact]
    public async Task AfterItsPartsAreDeleted_TheRowOffersReinstallNotTheMissingRequirement()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        var item = harness.AddDownload("WirelessPower.zip", WriteWirelessPowerLayout);
        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);
        Assert.True(item.CanGetRequirement);

        foreach (var entry in harness.Library.GetAll().ToList())
        {
            harness.Library.Delete(entry.FolderName);
        }

        harness.Ue4ss.Delete("WirelessPower");
        item.Refresh();

        Assert.True(item.WasDeleted);
        Assert.Equal("Removed from Library", item.StateLabel);
        Assert.Equal("Reinstall", item.ActionLabel);
        Assert.False(item.CanGetRequirement);
    }

    [Theory]
    [InlineData("ArcFission Library", "ArcFission Library", 356)]
    [InlineData("arcfission library", "ArcFission Library", 356)]
    [InlineData("ArcFission", "ArcFission Tools", 999)]
    [InlineData("Nothing Like It", null, 0)]
    public void PickRequirementMatch_PrefersAnExactNameThenAContainingOne(string requirement, string? expectedName, int expectedId)
    {
        IReadOnlyList<NexusModInfo> results =
        [
            new NexusModInfo(999, "ArcFission Tools", "a", "", "1", null),
            new NexusModInfo(356, "ArcFission Library", "a", "", "1", null),
        ];

        var match = DownloadsViewModel.PickRequirementMatch(results, requirement);

        Assert.Equal(expectedName, match?.Name);
        Assert.Equal(expectedId, match?.ModId ?? 0);
    }

    /// <summary>Nexus lists hidden/removed mods with no name; one of those used to crash the match, or be "contained" in any requirement.</summary>
    [Fact]
    public void PickRequirementMatch_SkipsNamelessMods()
    {
        IReadOnlyList<NexusModInfo> results =
        [
            new NexusModInfo(1, null!, "a", "", "1", null),
            new NexusModInfo(356, "ArcFission Library", "a", "", "1", null),
        ];

        Assert.Equal(356, DownloadsViewModel.PickRequirementMatch(results, "ArcFission")?.ModId);
        Assert.Null(DownloadsViewModel.PickRequirementMatch([results[0]], "ArcFission"));
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

    /// <summary>An IMM Database download lands on the Downloads page like a Nexus one (recorded under Pending_Downloads, source "Database") and is imported as one mod linked to its catalog entry.</summary>
    [Fact]
    public async Task CatalogDownload_LandsOnTheDownloadsPageAndImportsAsOneModLinkedToTheCatalogEntry()
    {
        var exmodJson = """{"name":"Steadfast","author":"Ferani","version":"1.0","description":"D","fileName":"Ferani_Steadfast_Structures","Rows":[]}""";
        byte[] exmodz;
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, "Extracted Mods/Ferani_Steadfast_Structures.EXMOD", exmodJson);
            }

            exmodz = stream.ToArray();
        }

        var harness = new Harness(_root) { HttpResponseBytes = exmodz };
        var vm = harness.Build();
        var catalogEntry = new CatalogEntry(
            CatalogSource.Daedalus, "daedalus:steadfast", "Steadfast Structures", "Ferani", "1.0", "D", "", null, null, null,
            PakUrl: null, ExmodzUrl: "https://github.com/ferani/mods/blob/main/Steadfast.EXMODZ", Categories: []);
        vm.SelectedCatalogEntry = new CatalogEntryViewModel(catalogEntry, null, null);

        await vm.DownloadAndExtractCommand.ExecuteAsync(null);

        var stored = Assert.Single(harness.PendingStore.Entries);
        Assert.Equal(PendingDownloadEntry.DatabaseSource, stored.Source);
        Assert.Equal("daedalus:steadfast", stored.CatalogEntryId);
        Assert.Equal(0, stored.ModId);
        Assert.True(File.Exists(stored.LocalFilePath));
        Assert.StartsWith(harness.PendingDownloadsDirectory, stored.LocalFilePath);

        var item = Assert.Single(vm.PendingDownloads);
        Assert.Equal("Database", item.Source);
        Assert.Equal("Steadfast Structures", item.DisplayName);
        Assert.True(item.IsInstalled);
        Assert.Equal("Reinstall", item.ActionLabel);

        var library = Assert.Single(harness.Library.GetAll());
        Assert.Equal("Steadfast", library.Name);
        Assert.Equal("Database", library.Source);
        Assert.Equal("daedalus:steadfast", library.CatalogEntryId);
        Assert.Null(library.NexusModId);
        Assert.Contains("Downloaded and imported 'Steadfast Structures'", vm.CatalogStatusMessage);
    }

    /// <summary>The non-modal "More details" window passes its own row: the download is that mod, not whatever the grid has selected now.</summary>
    [Fact]
    public async Task CatalogDownload_GivenARow_DownloadsThatRowNotTheSelectedOne()
    {
        var exmodJson = """{"name":"Steadfast","author":"Ferani","version":"1.0","description":"D","fileName":"Ferani_Steadfast_Structures","Rows":[]}""";
        byte[] exmodz;
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, "Extracted Mods/Ferani_Steadfast_Structures.EXMOD", exmodJson);
            }

            exmodz = stream.ToArray();
        }

        var harness = new Harness(_root) { HttpResponseBytes = exmodz };
        var vm = harness.Build();
        var selected = new CatalogEntry(
            CatalogSource.Daedalus, "daedalus:other", "Other Mod", "Someone", "1.0", "D", "", null, null, null,
            PakUrl: null, ExmodzUrl: "https://example.com/Other.EXMODZ", Categories: []);
        var shownInWindow = new CatalogEntry(
            CatalogSource.Daedalus, "daedalus:steadfast", "Steadfast Structures", "Ferani", "1.0", "D", "", null, null, null,
            PakUrl: null, ExmodzUrl: "https://example.com/Steadfast.EXMODZ", Categories: []);
        vm.SelectedCatalogEntry = new CatalogEntryViewModel(selected, null, null);

        await vm.DownloadAndExtractCommand.ExecuteAsync(new CatalogEntryViewModel(shownInWindow, null, null));

        var stored = Assert.Single(harness.PendingStore.Entries);
        Assert.Equal("daedalus:steadfast", stored.CatalogEntryId);
    }

    /// <summary>Reinstalling a catalog download replaces the existing copy linked to the same catalog entry rather than adding a "_2" twin.</summary>
    [Fact]
    public async Task CatalogDownload_ReinstallReplacesTheExistingCopyInsteadOfDoublingIt()
    {
        var exmodJson = """{"name":"Steadfast","author":"Ferani","version":"1.0","description":"D","fileName":"Ferani_Steadfast_Structures","Rows":[]}""";
        byte[] exmodz;
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(archive, "Extracted Mods/Ferani_Steadfast_Structures.EXMOD", exmodJson);
            }

            exmodz = stream.ToArray();
        }

        var harness = new Harness(_root) { HttpResponseBytes = exmodz };
        var vm = harness.Build();
        var catalogEntry = new CatalogEntry(
            CatalogSource.Daedalus, "daedalus:steadfast", "Steadfast Structures", "Ferani", "1.0", "D", "", null, null, null,
            PakUrl: null, ExmodzUrl: "https://example.com/Steadfast.EXMODZ", Categories: []);
        vm.SelectedCatalogEntry = new CatalogEntryViewModel(catalogEntry, null, null);
        await vm.DownloadAndExtractCommand.ExecuteAsync(null);
        var item = Assert.Single(vm.PendingDownloads);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Single(harness.Library.GetAll());
        Assert.Single(harness.PendingStore.Entries);
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

    /// <summary>A mod deleted elsewhere (Library) comes back out from under "Show installed" as removed once the Library says it changed.</summary>
    [Fact]
    public void PendingList_ModDeletedElsewhere_ReappearsAfterLibraryChanged()
    {
        var harness = new Harness(_root);
        harness.AddDownload("b.zip", a => WriteEntry(a, "Scripts/main.lua", "x"), modId: 2, fileId: 2);
        var stagedFolder = Path.Combine(_root, "Staged_UE4SS", "ModB");
        Directory.CreateDirectory(stagedFolder);
        harness.PendingStore.SetActivation(2, 2, [new PendingDownloadActivatedItem("ModB", PendingDownloadActivationKind.Ue4ssMod)], []);
        var vm = harness.Build();
        Assert.Empty(vm.VisiblePendingDownloads);

        Directory.Delete(stagedFolder);
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());

        var row = Assert.Single(vm.VisiblePendingDownloads);
        Assert.True(row.WasDeleted);
        Assert.Equal(0, vm.InstalledDownloadCount);
    }

    /// <summary>
    /// The real Minimap V1.0.5 case: a Library mod plus a UE4SS mod, the UE4SS half ENABLED — so it
    /// sits in the game's Mods folder, not staging. The row said "Removed from Library" because only
    /// staging was checked.
    /// </summary>
    [Fact]
    public void PendingRow_EnabledUe4ssHalfInTheGameFolder_CountsAsInstalled()
    {
        var harness = new Harness(_root) { IcarusContentPath = Path.Combine(_root, "Game", "Icarus", "Content") };
        harness.AddDownload("Minimap V1.0.5.zip", a => WriteEntry(a, "README_EN.txt", "x"), modId: 345, fileId: 2737);
        Directory.CreateDirectory(Path.Combine(_root, "Game", "Icarus", "Binaries", "Win64", "ue4ss", "Mods", "MiniMapa"));
        harness.PendingStore.SetActivation(345, 2737, [new PendingDownloadActivatedItem("MiniMapa", PendingDownloadActivationKind.Ue4ssMod)], []);
        var vm = harness.Build();

        var item = Assert.Single(vm.PendingDownloads);
        Assert.True(item.IsInstalled);
        Assert.False(item.WasDeleted);
        Assert.Equal("Installed", item.StateLabel);
    }

    [Fact]
    public void RequirementsSummary_SkipsEmptyStoredLines()
    {
        var harness = new Harness(_root);
        harness.AddDownload("Minimap V1.0.5.zip", a => WriteEntry(a, "README_EN.txt", "x"), modId: 345, fileId: 2737);
        harness.PendingStore.SetActivation(345, 2737, [], ["", "UE4SS for Icarus (Lua mods).", "  "]);
        var vm = harness.Build();

        Assert.Equal("Needs: UE4SS for Icarus (Lua mods).", Assert.Single(vm.PendingDownloads).RequirementsSummary);
    }

    /// <summary>A website download's name keeps Nexus's "-modid-version-id-unixtime" tail; the row shows the upload name.</summary>
    [Fact]
    public void PendingRow_ManualNexusDownloadName_DropsTheNumericTail()
    {
        var harness = new Harness(_root);
        var item = harness.AddDownload("Cheat S5-II and Larkwell Haulier underwears-59-2-3-25-146718-1767981001.zip", a => WriteEntry(a, "x.pak", "p"), modId: 59, fileId: 798);

        Assert.Equal("Cheat S5-II and Larkwell Haulier underwears", item.DisplayName);
    }

    /// <summary>The row used to show "Synthetic Miasma V1 1 376 1.1 2026-10-22T10-52Z AHkozS8Ru": a version after the mod id wasn't recognised.</summary>
    [Fact]
    public void PendingRow_NexusAppNameWithADottedVersion_DropsTheTail()
    {
        var harness = new Harness(_root);
        var item = harness.AddDownload("Synthetic Miasma V1 1 376 1.1 2026-10-22T10-52Z AHkozS8Ru.zip", a => WriteEntry(a, "x.pak", "p"), modId: 376, fileId: 1);

        Assert.Equal("Synthetic Miasma V1 1", item.DisplayName);
    }

    [Fact]
    public void DownloadsEmptyText_NothingDownloaded_PointsAtDatabase()
    {
        var vm = new Harness(_root).Build();

        Assert.StartsWith("Nothing downloaded yet", vm.DownloadsEmptyText);
    }

    /// <summary>Once installed, the row shows the name the Library shows (a rename included), not the file's.</summary>
    [Fact]
    public async Task PendingRow_Installed_UsesTheLibraryName()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        var item = harness.AddDownload("Homestead 223 1.2 2026-10-22T10-52Z AbCdEf.zip", WriteSinglePak, modId: 223, fileId: 900);
        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);
        var folder = Assert.Single(harness.Library.GetAll()).FolderName;
        harness.Library.SetDisplayNameOverride(folder, "Homestead (mine)");

        item.Refresh();

        Assert.Equal("Homestead (mine)", item.DisplayName);
    }

    // --- Replacing a mod (ModReplacementService through Activate/Reinstall) ---

    private static void WriteSinglePak(ZipArchive archive) => WriteEntry(archive, "Ferani_Homestead_Expanded_P.pak", "pak v1");

    /// <summary>The real Ferani_Homestead_Expanded case: it was edited (✎) and Nexus mod 223, and a Reinstall replaced it without a word.</summary>
    [Fact]
    public async Task Reinstall_OfAnEditedMod_AsksFirst_AndNoKeepsTheEditedCopy()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: false) };
        var vm = harness.Build();
        var item = harness.AddDownload("Ferani_Homestead_Expanded_EXMODZ 223 1.7.zip", WriteSinglePak, modId: 223, fileId: 900);
        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);
        var folder = Assert.Single(harness.Library.GetAll()).FolderName;
        harness.Library.MarkLocallyEdited(folder);

        var result = await vm.ActivateAsync(item);

        Assert.True(result.Cancelled);
        Assert.Contains(harness.Dialogs!.ConfirmMessages, m => m.Contains("changes made in Starlink"));
        var kept = Assert.Single(harness.Library.GetAll());
        Assert.Equal(folder, kept.FolderName);
        Assert.True(kept.IsLocallyEdited);
        Assert.Contains("Kept your edited copy", vm.PendingDownloadStatusMessage);
    }

    [Fact]
    public async Task Reinstall_KeepsPinNotesAndRename_AndBacksTheOldCopyUp()
    {
        var harness = new Harness(_root);
        var vm = harness.Build();
        var item = harness.AddDownload("Homestead.zip", WriteSinglePak, modId: 223, fileId: 900);
        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);
        var folder = Assert.Single(harness.Library.GetAll()).FolderName;
        harness.Library.UpdateMetadata(folder, isPinned: true, isFavorite: false, notes: "mine");
        harness.Library.SetDisplayNameOverride(folder, "Homestead (mine)");

        var result = await vm.ActivateAsync(item);

        Assert.True(result.Succeeded);
        var replaced = Assert.Single(harness.Library.GetAll());
        Assert.True(replaced.IsPinned);
        Assert.Equal("mine", replaced.Notes);
        Assert.Equal("Homestead (mine)", replaced.Name);
        Assert.True(harness.Library.HasModBackup(folder));
    }

    /// <summary>Reinstalling an ENABLED UE4SS mod used to leave the old copy running in the game; a later Disable then made "MiniMapa_2".</summary>
    [Fact]
    public async Task Reinstall_OfAnEnabledUe4ssMod_ReplacesTheCopyInTheGame()
    {
        var harness = new Harness(_root) { IcarusContentPath = Path.Combine(_root, "Game", "Icarus", "Content") };
        var vm = harness.Build();
        var item = harness.AddDownload("MiniMapa.zip", a => WriteEntry(a, "MiniMapa/Scripts/main.lua", "new"), modId: 345, fileId: 2737);
        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);
        Assert.Equal(["MiniMapa"], harness.Ue4ss.GetAll());

        // Enabled: moved into the game's Mods folder (what Apply does), holding an older script.
        var gameMods = Ue4ssGamePaths.ResolveModsFolder(harness.IcarusContentPath);
        Directory.CreateDirectory(Path.Combine(gameMods, "MiniMapa", "Scripts"));
        File.WriteAllText(Path.Combine(gameMods, "MiniMapa", "Scripts", "main.lua"), "old");
        harness.Ue4ss.Delete("MiniMapa");

        var result = await vm.ActivateAsync(item);

        Assert.True(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(Path.Combine(gameMods, "MiniMapa", "Scripts", "main.lua")));
        Assert.Equal(["MiniMapa"], harness.Ue4ss.ListInstalledInGame(gameMods));
        Assert.Empty(harness.Ue4ss.GetAll());
        Assert.Equal(345, harness.Ue4ssMeta.Load("MiniMapa").NexusModId);
    }

    /// <summary>The real Minimap README asks for "Icarus Mod Manager", and its heading underline was stored as a blank requirement — neither is a download to get.</summary>
    [Fact]
    public async Task Activate_ModManagerRequirement_IsSatisfiedAndNotOffered()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: true) };
        var vm = harness.Build();
        var item = harness.AddDownload("Minimap V1.0.5.zip", a =>
        {
            WriteEntry(a, "README_EN.txt", "Minimap\n\nREQUIREMENTS\n-------------\n- UE4SS for Icarus (Lua mods).\n- Icarus Mod Manager (for the .EXMODZ file with the map icon rows).\n\nINSTALL\n1. etc\n");
            WriteEntry(a, "MiniMapa/Scripts/main.lua", "x");
        }, modId: 345, fileId: 2737);

        await vm.ActivatePendingDownloadCommand.ExecuteAsync(item);

        Assert.Empty(item.MissingRequirementNames);
        Assert.False(item.CanGetRequirement);
        Assert.Contains("Icarus Mod Manager (for the .EXMODZ file with the map icon rows). ✓", vm.PendingDownloadStatusMessage);
        Assert.DoesNotContain("NOT installed", vm.PendingDownloadStatusMessage);
        Assert.Empty(harness.Dialogs!.ConfirmMessages);
        Assert.DoesNotContain(harness.PendingStore.Entries.Single().Requirements, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void StoredBlankRequirements_AreDroppedWhenTheStoreLoads()
    {
        var harness = new Harness(_root);
        harness.AddDownload("Minimap V1.0.5.zip", a => WriteEntry(a, "README_EN.txt", "x"), modId: 345, fileId: 2737);
        harness.PendingStore.SetActivation(345, 2737, [], ["UE4SS for Icarus (Lua mods)."]);
        var json = Path.Combine(_root, "pending_downloads.json");
        File.WriteAllText(json, File.ReadAllText(json).Replace("\"UE4SS for Icarus (Lua mods).\"", "\"\", \"UE4SS for Icarus (Lua mods).\", \"  \""));

        var reloaded = new PendingDownloadStore(_root, NullLogger<PendingDownloadStore>.Instance);

        Assert.Equal(["UE4SS for Icarus (Lua mods)."], reloaded.Entries.Single().Requirements);
    }

    private static NexusModFile NexusFile(int id) => new(id, $"file{id}.zip", $"File {id}", "1", "MAIN", IsPrimary: false);

    /// <summary>
    /// A mod offering two files side by side (a main file and an optional add-on): installing the
    /// second used to replace every copy of the mod, deleting the first.
    /// </summary>
    [Fact]
    public async Task Activate_SecondFileOfTheSameMod_KeepsTheFirst()
    {
        var harness = new Harness(_root);
        harness.NexusApi.FileSet = new NexusModFileSet([NexusFile(10), NexusFile(20)], []);
        var vm = harness.Build();
        var first = harness.AddDownload("Main.zip", a => WriteEntry(a, "MainPart/Scripts/main.lua", "x"), modId: 400, fileId: 10);
        await vm.ActivateAsync(first);
        var second = harness.AddDownload("Addon.zip", a => WriteEntry(a, "AddonPart/Scripts/main.lua", "x"), modId: 400, fileId: 20);

        var result = await vm.ActivateAsync(second);

        Assert.True(result.Succeeded);
        Assert.Equal(["AddonPart", "MainPart"], harness.Ue4ss.GetAll().Order());
    }

    /// <summary>The same file's newer version (Nexus's file_updates: 10 → 30) still replaces the copy it updates.</summary>
    [Fact]
    public async Task Activate_NewerVersionOfAnInstalledFile_ReplacesIt()
    {
        var harness = new Harness(_root);
        harness.NexusApi.FileSet = new NexusModFileSet([NexusFile(20), NexusFile(30)], [new NexusFileUpdate(10, 30)]);
        var vm = harness.Build();
        var old = harness.AddDownload("Main.zip", a => WriteEntry(a, "MainOld/Scripts/main.lua", "x"), modId: 400, fileId: 10);
        await vm.ActivateAsync(old);
        var addon = harness.AddDownload("Addon.zip", a => WriteEntry(a, "AddonPart/Scripts/main.lua", "x"), modId: 400, fileId: 20);
        await vm.ActivateAsync(addon);
        var newer = harness.AddDownload("Main v2.zip", a => WriteEntry(a, "MainNew/Scripts/main.lua", "x"), modId: 400, fileId: 30);

        var result = await vm.ActivateAsync(newer);

        Assert.True(result.Succeeded);
        Assert.Equal(["AddonPart", "MainNew"], harness.Ue4ss.GetAll().Order());
    }

    /// <summary>A copy installed before Starlink recorded its Nexus file could be any of the mod's files: the user is asked, and No keeps it.</summary>
    [Fact]
    public async Task Activate_CopyWithNoRecordedFile_AsksAndNoKeepsIt()
    {
        var harness = new Harness(_root) { Dialogs = new FakeDialogService(confirmResult: false) };
        harness.NexusApi.FileSet = new NexusModFileSet([NexusFile(10), NexusFile(20)], []);
        Directory.CreateDirectory(Path.Combine(_root, "Staged_UE4SS", "OldCopy", "Scripts"));
        harness.Ue4ssMeta.Save("OldCopy", new Ue4ssModMeta { NexusModId = 400 });
        var vm = harness.Build();
        var item = harness.AddDownload("Addon.zip", a => WriteEntry(a, "AddonPart/Scripts/main.lua", "x"), modId: 400, fileId: 20);

        await vm.ActivateAsync(item);

        Assert.Contains(harness.Dialogs.ConfirmMessages, m => m.Contains("OldCopy"));
        Assert.Equal(["AddonPart", "OldCopy"], harness.Ue4ss.GetAll().Order());
    }

    /// <summary>Another download landing reloads the list; the row an Activate holds must still be the row on the page, or it keeps saying "Activate".</summary>
    [Fact]
    public void PendingList_Reload_KeepsTheRowsOfUnchangedDownloads()
    {
        var harness = new Harness(_root);
        harness.AddDownload("a.zip", a => WriteEntry(a, "Scripts/main.lua", "x"), modId: 1, fileId: 1);
        harness.AddDownload("b.zip", a => WriteEntry(a, "Scripts/main.lua", "x"), modId: 2, fileId: 2);
        var vm = harness.Build();
        var rowA = vm.PendingDownloads.Single(p => p.ModId == 1);

        vm.DiscardPendingDownloadCommand.Execute(vm.PendingDownloads.Single(p => p.ModId == 2));

        Assert.Same(rowA, Assert.Single(vm.PendingDownloads));
    }

    /// <summary>Only Nexus's download-link rejection is "you may need Premium": any other failure says what went wrong instead of opening the website.</summary>
    [Fact]
    public async Task FetchAndDownload_OtherFailure_IsNotReportedAsNeedingPremium()
    {
        var harness = new Harness(_root);
        harness.NexusApi.DownloadLinksError = new InvalidOperationException("Something else broke.");
        var vm = harness.Build();

        await vm.FetchAndDownloadAsync("nxm://icarus/mods/12/files/34");

        Assert.Equal("Download failed: Something else broke.", vm.PendingDownloadStatusMessage);
    }

    /// <summary>A Database pak used to get author "Unknown" when installed from the Downloads page, but the real author through Library's Get update.</summary>
    [Fact]
    public async Task CatalogDownload_Pak_IsImportedWithTheCatalogAuthor()
    {
        var harness = new Harness(_root) { HttpResponseBytes = [1, 2, 3] };
        var vm = harness.Build();
        var catalogEntry = new CatalogEntry(
            CatalogSource.Jimk72, "jimk72:stacks", "Bigger Stacks", "jimk72", "1.0", "D", "", null, null, null,
            PakUrl: "https://example.com/Stacks.pak", ExmodzUrl: null, Categories: []);

        await vm.DownloadAndExtractCommand.ExecuteAsync(new CatalogEntryViewModel(catalogEntry, null, null));

        Assert.Equal(["jimk72"], harness.PakImporter.Authors);
    }

    /// <summary>Two callers asking for the catalog while the first load is still running share that load instead of fetching again.</summary>
    [Fact]
    public async Task GetOrFetchCatalog_WhileTheFirstLoadRuns_FetchesOnce()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<CatalogEntry>>();
        var harness = new Harness(_root) { Catalog = new FakeCatalogClient { Pending = pending } };
        var vm = harness.Build();
        var fetchesByConstructor = harness.Catalog.FetchCount;

        var first = vm.GetOrFetchCatalogAsync();
        var second = vm.GetOrFetchCatalogAsync();
        pending.SetResult([new CatalogEntry(CatalogSource.Daedalus, "daedalus:x", "X", "A", "1", "D", "", null, null, null, PakUrl: null, ExmodzUrl: null, Categories: [])]);
        await Task.WhenAll(first, second);

        Assert.Equal(fetchesByConstructor, harness.Catalog.FetchCount);
        Assert.NotEmpty(await first);
    }

    /// <summary>The catalog row's Outdated uses the same version rule as the Nexus cards: "1.0" installed against a listed "1" is up to date.</summary>
    [Theory]
    [InlineData("1", "1.0", false)]
    [InlineData("1.2", "1.1", true)]
    public void CatalogRow_Outdated_ComparesVersionsNotStrings(string listed, string installed, bool expectedOutdated)
    {
        var entry = new CatalogEntry(CatalogSource.Daedalus, "daedalus:x", "X", "A", listed, "D", "", null, null, null, PakUrl: null, ExmodzUrl: null, Categories: []);

        Assert.Equal(expectedOutdated, new CatalogEntryViewModel(entry, installed, null).IsOutdated);
    }

    // --- Fakes: only the network and UnrealPak; everything else above is the real storage layer ---

    /// <summary>Stands in for UnrealPak only: the pak is imported into the REAL Library repository as an opaque entry, exactly as the real importer does when conversion isn't possible.</summary>
    private sealed class FakePrebuiltPakImporter(ILibraryRepository library) : IPrebuiltPakImporter
    {
        public List<string> ImportedPaks { get; } = [];

        /// <summary>The author passed with each import, in order.</summary>
        public List<string?> Authors { get; } = [];

        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default)
        {
            ImportedPaks.Add(Path.GetFileName(pakFilePath));
            Authors.Add(author);
            return Task.FromResult(library.ImportPak(pakFilePath, source: source, nexusModId: nexusModId));
        }
    }

    private sealed class FakeNexusApiClient : INexusApiClient
    {
        public Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default) => Task.FromResult<NexusUserInfo?>(null);
        /// <summary>What GetDownloadLinksAsync throws; unset, it isn't supported.</summary>
        public Exception? DownloadLinksError { get; set; }

        public Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default) =>
            throw (DownloadLinksError ?? new NotSupportedException());
        public Task<NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            Task.FromResult<NexusModInfo?>(new NexusModInfo(modId, $"Nexus Mod {modId}", "author", "summary", "1", null));
        public Task<IReadOnlyList<NexusModInfo>> GetModListAsync(string apiKey, string gameDomain, NexusModList list, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        /// <summary>What SearchModsAsync returns for any query; the dependency offer picks from these.</summary>
        public List<NexusModInfo> SearchResults { get; } = [];
        public List<string> SearchQueries { get; } = [];
        /// <summary>Mod IDs whose files were asked for — the dependency offer's "Yes" lands here (the download itself needs a real CDN, so it stops right after).</summary>
        public List<int> GetModFilesCalls { get; } = [];

        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default)
        {
            GetModFilesCalls.Add(modId);
            return Task.FromResult<IReadOnlyList<NexusModFile>>([new NexusModFile(1, $"mod{modId}.zip", "Main", "1", "MAIN", IsPrimary: true)]);
        }

        /// <summary>What GetModFileSetAsync answers; unset, one main file.</summary>
        public NexusModFileSet? FileSet { get; set; }

        /// <summary>Mod IDs whose files were listed to START a download (the dependency offer's "Yes" lands here). Recording an activated download's file version uses GetModFilesAsync instead.</summary>
        public List<int> FileSetCalls { get; } = [];

        public Task<NexusModFileSet> GetModFileSetAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default)
        {
            FileSetCalls.Add(modId);
            return Task.FromResult(FileSet ?? new NexusModFileSet([new NexusModFile(1, $"mod{modId}.zip", "Main", "1", "MAIN", IsPrimary: true)], []));
        }

        public Task<IReadOnlyList<NexusModInfo>> SearchModsAsync(string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default)
        {
            SearchQueries.Add(searchText);
            return Task.FromResult<IReadOnlyList<NexusModInfo>>([.. SearchResults]);
        }
        public Task<NexusModPage> ListAllModsAsync(string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusEndorsementStatus> SetEndorsementAsync(string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedBytesHandler(byte[] bytes) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class FakeDialogService(bool confirmResult) : IDialogService
    {
        public List<string> ConfirmMessages { get; } = [];

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            ConfirmMessages.Add(message);
            return confirmResult;
        }

        public RenamePromptResult PromptRename(string currentName, string description = "", string? resetValue = null, string resetLabel = "", string resetTooltip = "", string title = "", string fieldLabel = "") =>
            throw new NotSupportedException("Not exercised by these tests.");
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
        /// <summary>When set, every fetch waits for it — so a test can ask again while a fetch is still running.</summary>
        public TaskCompletionSource<IReadOnlyList<CatalogEntry>>? Pending { get; set; }

        public int FetchCount { get; private set; }

        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default)
        {
            FetchCount++;
            return Pending?.Task ?? Task.FromResult<IReadOnlyList<CatalogEntry>>([]);
        }
    }

    private sealed class FakeGitHubRepoDateClient : IGitHubRepoDateClient
    {
        public Task<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>> FetchPushedDatesAsync(IReadOnlyCollection<(string Owner, string Repo)> repos, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>>(new Dictionary<(string, string), DateTimeOffset>());
    }
}
