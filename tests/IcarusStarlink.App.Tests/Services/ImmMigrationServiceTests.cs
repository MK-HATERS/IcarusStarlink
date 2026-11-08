using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using IcarusStarlink.App.Services;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// Settings › Migrate from IMM over a real Library and a classic IMM install built in the test:
/// EXMOD and pak mods, missing and broken ones, mods already here, and how each is linked to the
/// mod database or Nexus.
/// </summary>
public sealed class ImmMigrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ImmMigration_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;
    private readonly FakeCatalogClient _daedalus = new();
    private readonly FakeCatalogClient _jimk72 = new();
    private readonly FakeNexusApiClient _nexus = new();
    private readonly FakePrebuiltPakImporter _pakImporter = new();
    private readonly ImmMigrationService _service;
    private readonly List<object> _immCache = [];

    public ImmMigrationServiceTests()
    {
        _library = new FolderLibraryRepository(
            Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"),
            NullLogger<FolderLibraryRepository>.Instance);
        _service = new ImmMigrationService(
            _library, _daedalus, _jimk72, _nexus, new FakeCredentialStore(), _pakImporter,
            new FakeSettingsService(), Path.Combine(_root, "Data"));
        Directory.CreateDirectory(ImmExtractedModsFolder);
    }

    public void Dispose()
    {
        _library.Dispose();
        Directories.TryDelete(_root);
    }

    private string ImmRoot => Path.Combine(_root, "IMM");

    private string ImmExtractedModsFolder => Path.Combine(ImmRoot, "Extracted_Mods");

    private string ListPath => Path.Combine(ImmRoot, "LastMergedMods.txt");

    private string LibraryFolder(string folderName) => Path.Combine(_root, "Extracted_Mods", folderName);

    private static ExmodPackageContents Package(string name, string author) => new(
        new ExmodPackage
        {
            Name = name, Author = author, Version = "1.0", Description = "A test mod.", FileName = name,
            Rows =
            [
                new ExmodFileRow
                {
                    CurrentFile = "Crafting-D_ProcessorRecipes.json",
                    FileItems = [new ExmodFileItem { Name = "SmelterRecipe", Fields = { ["CraftTime"] = System.Text.Json.Nodes.JsonValue.Create(5) } }],
                },
            ],
        },
        [new ExmodAssetEntry("readme.md", "Test."u8.ToArray())]);

    /// <summary>Lists the mod in classic IMM's ExtractedMods.json; with <paramref name="writeFolder"/> also writes its EXMOD folder.</summary>
    private void AddImmExmodMod(string name, string immAuthor = "Ann", string? exmodAuthor = null, bool writeFolder = true)
    {
        var folderName = name + "_Folder";
        _immCache.Add(new { name, author = immAuthor, version = "1.0", fileName = folderName });
        if (writeFolder)
        {
            ExmodFolder.Write(Path.Combine(ImmExtractedModsFolder, folderName), Package(name, exmodAuthor ?? immAuthor));
        }

        WriteImmCache();
    }

    private void AddImmFolder(string name, string fileInside)
    {
        var folderName = name + "_Folder";
        _immCache.Add(new { name, author = "Ann", version = "1.0", fileName = folderName });
        var folder = Path.Combine(ImmExtractedModsFolder, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, fileInside), [1, 2, 3]);
        WriteImmCache();
    }

    private void WriteImmCache() =>
        File.WriteAllText(Path.Combine(ImmRoot, "ExtractedMods.json"), JsonSerializer.Serialize(new { mods = _immCache }));

    private void WriteList(params string[] names) => File.WriteAllLines(ListPath, names);

    /// <summary>Puts a mod straight into the Library (as if imported some other way), with no source.</summary>
    private LibraryEntry PutInLibrary(string name, string author = "Ann")
    {
        var source = Path.Combine(_root, "Elsewhere", name);
        ExmodFolder.Write(source, Package(name, author));
        return _library.Import(source);
    }

    private Task<ImmMigrationResult> MigrateAsync(IProgress<string>? progress = null) =>
        _service.MigrateAsync(ListPath, ImmRoot, progress);

    private LibraryEntry LibraryEntryNamed(string name) => Assert.Single(_library.GetAll(), e => e.Name == name);

    private static CatalogEntry CatalogMod(string id, string name, string author) =>
        new(CatalogSource.Daedalus, id, name, author, "1.0", "", "", null, null, null, null, null, []);

    // ---------------------------------------------------------------------------------------
    // What each mod becomes
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ExmodMod_IsCopiedIn_AndLinkedToTheDatabaseWhenTheCatalogHasIt()
    {
        AddImmExmodMod("ModA");
        WriteList("ModA");
        _daedalus.Entries = [CatalogMod("daedalus:moda", "ModA", "Ann")];

        var result = await MigrateAsync();

        var mod = Assert.Single(result.Mods);
        Assert.Equal(ImmMigrationOutcome.Imported, mod.Outcome);
        Assert.Equal("Database", mod.Source);
        Assert.Equal("daedalus:moda", LibraryEntryNamed("ModA").CatalogEntryId);
        Assert.True(Directory.Exists(LibraryFolder("ModA")));
    }

    [Fact]
    public async Task NameNotInImmCache_AndFolderMissing_AreNotFoundOnDisk()
    {
        AddImmExmodMod("ModGone", writeFolder: false);
        WriteList("Ghost", "ModGone");

        var result = await MigrateAsync();

        Assert.All(result.Mods, m => Assert.Equal(ImmMigrationOutcome.NotFoundOnDisk, m.Outcome));
        Assert.Equal(2, result.MissingCount);
        Assert.Empty(_library.GetAll());
    }

    [Fact]
    public async Task FolderWithNoExmodOrPak_FailsWithTheReason()
    {
        AddImmFolder("ModX", "readme.txt");
        WriteList("ModX");

        var result = await MigrateAsync();

        var mod = Assert.Single(result.Mods);
        Assert.Equal(ImmMigrationOutcome.Failed, mod.Outcome);
        Assert.Equal("no .EXMOD or .pak inside its folder", mod.Detail);
    }

    [Fact]
    public async Task AlreadyInLibraryWithoutSource_IsLinked()
    {
        AddImmExmodMod("ModA");
        PutInLibrary("ModA");
        WriteList("ModA");
        _daedalus.Entries = [CatalogMod("daedalus:moda", "ModA", "Ann")];

        var result = await MigrateAsync();

        var mod = Assert.Single(result.Mods);
        Assert.Equal(ImmMigrationOutcome.AlreadyInLibrary, mod.Outcome);
        Assert.Equal("Database", mod.Source);
        Assert.Equal("daedalus:moda", LibraryEntryNamed("ModA").CatalogEntryId);
    }

    [Fact]
    public async Task CatalogSourceDown_IsReported_AndTheRunFinishes()
    {
        AddImmExmodMod("ModA");
        WriteList("ModA");
        _jimk72.Exception = new HttpRequestExceptionStandIn("Jimk72 is down");

        var result = await MigrateAsync();

        Assert.Equal(["Jimk72"], result.FailedCatalogSources);
        Assert.Equal(ImmMigrationOutcome.Imported, Assert.Single(result.Mods).Outcome);
    }

    [Fact]
    public async Task PakMod_GoesThroughThePrebuiltPakImporter()
    {
        AddImmFolder("ModP", "ModP.pak");
        WriteList("ModP");

        var result = await MigrateAsync();

        Assert.Equal(ImmMigrationOutcome.Imported, Assert.Single(result.Mods).Outcome);
        Assert.Equal(Path.Combine(ImmExtractedModsFolder, "ModP_Folder", "ModP.pak"), _pakImporter.LastPakPath);
    }

    [Fact]
    public async Task NoCatalogMatch_ExactNexusTitle_IsLinked()
    {
        AddImmExmodMod("ModA");
        WriteList("ModA");
        _nexus.SearchResults = [new NexusModInfo(123, "ModA", "Ann", null, "1.0", null)];

        var result = await MigrateAsync();

        Assert.Equal("Nexus", Assert.Single(result.Mods).Source);
        Assert.Equal(123, LibraryEntryNamed("ModA").NexusModId);
    }

    // ---------------------------------------------------------------------------------------
    // Regressions
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regression: one mod already in the Library whose Library_Meta file was locked (OneDrive,
    /// antivirus) stopped the whole migration with "Migration failed": no results, no merge list.
    /// </summary>
    [Fact]
    public async Task ExistingModsSidecarLocked_TheRunStillFinishes()
    {
        AddImmExmodMod("ModA");
        AddImmExmodMod("ModB");
        var existing = PutInLibrary("ModA");
        WriteList("ModA", "ModB");
        _daedalus.Entries = [CatalogMod("daedalus:moda", "ModA", "Ann")];

        ImmMigrationResult result;
        using (new FileStream(Path.Combine(_root, "Library_Meta", existing.FolderName + ".json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await MigrateAsync();
        }

        var modA = Assert.Single(result.Mods, m => m.ListName == "ModA");
        Assert.Equal(ImmMigrationOutcome.AlreadyInLibrary, modA.Outcome);
        Assert.Contains("couldn't link", modA.Display);
        Assert.Equal(ImmMigrationOutcome.Imported, Assert.Single(result.Mods, m => m.ListName == "ModB").Outcome);
    }

    /// <summary>Regression: a name listed twice (real IMM lists do this) was brought over once and then counted as "already here".</summary>
    [Fact]
    public async Task DuplicateListLines_AreMigratedOnce()
    {
        AddImmExmodMod("ModA");
        WriteList("ModA", "ModA");

        var result = await MigrateAsync();

        Assert.Equal(ImmMigrationOutcome.Imported, Assert.Single(result.Mods).Outcome);
        Assert.Equal(0, result.AlreadyPresentCount);
    }

    /// <summary>
    /// Regression: every mod was copied in on the UI thread back to back, so "Migrating N of M"
    /// never showed and the window stopped responding until the end.
    /// </summary>
    [Fact]
    public async Task Migrate_ShowsEachModsProgressBeforeImportingIt()
    {
        AddImmExmodMod("ModA");
        AddImmExmodMod("ModB");
        WriteList("ModA", "ModB");
        _daedalus.Entries = [CatalogMod("daedalus:moda", "ModA", "Ann"), CatalogMod("daedalus:modb", "ModB", "Ann")];

        var seen = new List<(string Message, bool ModBAlreadyCopied)>();
        var loop = new MessageLoopContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(loop);
        Task<ImmMigrationResult> run;
        try
        {
            var progress = new Progress<string>(message => seen.Add((message, Directory.Exists(LibraryFolder("ModB")))));
            run = MigrateAsync(progress);
            loop.RunUntil(run);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await run;

        var modBLine = Assert.Single(seen, s => s.Message == "Migrating 2 of 2: ModB");
        Assert.False(modBLine.ModBAlreadyCopied, "ModB's progress line only showed after ModB had been copied in");
    }

    /// <summary>Regression: a pak mod that came over as a prebuilt pak (no game data yet) only said "brought over", hiding why it won't merge field by field.</summary>
    [Fact]
    public async Task PakModWithImportWarnings_ShowsTheWarningOnItsLine()
    {
        AddImmFolder("ModP", "ModP.pak");
        WriteList("ModP");
        _pakImporter.Warnings = ["Couldn't convert 'ModP' into an editable mod — run \"Refresh game data\" in Settings first. Imported as a prebuilt pak instead."];

        var result = await MigrateAsync();

        Assert.Contains("Refresh game data", Assert.Single(result.Mods).Display);
        Assert.Equal(1, result.WarningCount);
    }

    /// <summary>Regression: with classic IMM's author unknown, a mod was linked to the first catalog mod of that name, even when two authors have one.</summary>
    [Fact]
    public async Task SameNameByTwoAuthors_UnknownAuthor_IsNotLinkedToEither()
    {
        AddImmExmodMod("ModA", immAuthor: "Unknown");
        WriteList("ModA");
        _daedalus.Entries = [CatalogMod("daedalus:ann", "ModA", "Ann"), CatalogMod("daedalus:bob", "ModA", "Bob")];

        var result = await MigrateAsync();

        Assert.Null(Assert.Single(result.Mods).Source);
        Assert.Null(LibraryEntryNamed("ModA").CatalogEntryId);
    }

    [Fact]
    public async Task OneCatalogEntryWithThatName_UnknownAuthor_StillLinks()
    {
        AddImmExmodMod("ModA", immAuthor: "Unknown");
        WriteList("ModA");
        _daedalus.Entries = [CatalogMod("daedalus:ann", "ModA", "Ann")];

        var result = await MigrateAsync();

        Assert.Equal("Database", Assert.Single(result.Mods).Source);
        Assert.Equal("daedalus:ann", LibraryEntryNamed("ModA").CatalogEntryId);
    }

    /// <summary>Regression: of two Nexus mods with the same title, the first was linked whatever its author.</summary>
    [Fact]
    public async Task TwoExactNexusTitles_LinksTheOneByTheSameAuthor()
    {
        AddImmExmodMod("ModA", immAuthor: "Ann");
        WriteList("ModA");
        _nexus.SearchResults = [new NexusModInfo(1, "ModA", "Bob", null, "1.0", null), new NexusModInfo(2, "ModA", "Ann", null, "1.0", null)];

        await MigrateAsync();

        Assert.Equal(2, LibraryEntryNamed("ModA").NexusModId);
    }

    /// <summary>Regression: with no known author, a title two Nexus mods share was linked to the first.</summary>
    [Fact]
    public async Task TwoExactNexusTitles_NoKnownAuthor_IsNotLinked()
    {
        AddImmExmodMod("ModA", immAuthor: "Unknown", exmodAuthor: "");
        WriteList("ModA");
        _nexus.SearchResults = [new NexusModInfo(1, "ModA", "Bob", null, "1.0", null), new NexusModInfo(2, "ModA", "Ann", null, "1.0", null)];

        var result = await MigrateAsync();

        Assert.Null(Assert.Single(result.Mods).Source);
        Assert.Null(LibraryEntryNamed("ModA").NexusModId);
    }

    // ---------------------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------------------

    /// <summary>A one-thread message loop, like the window's: posted work runs in order on the thread that calls RunUntil.</summary>
    private sealed class MessageLoopContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;

        public void RunUntil(Task task)
        {
            task.ContinueWith(_ => _queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }

    private sealed class HttpRequestExceptionStandIn(string message) : Exception(message);

    private sealed class FakeCatalogClient : IDaedalusCatalogClient, IJimk72CatalogClient
    {
        public IReadOnlyList<CatalogEntry> Entries { get; set; } = [];
        public Exception? Exception { get; set; }

        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) =>
            Exception is { } ex ? Task.FromException<IReadOnlyList<CatalogEntry>>(ex) : Task.FromResult(Entries);
    }

    private sealed class FakeNexusApiClient : INexusApiClient
    {
        public IReadOnlyList<NexusModInfo> SearchResults { get; set; } = [];

        public Task<IReadOnlyList<NexusModInfo>> SearchModsAsync(string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default) =>
            Task.FromResult(SearchResults);

        public Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(
            string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<NexusModInfo>> GetModListAsync(string apiKey, string gameDomain, NexusModList list, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NexusModPage> ListAllModsAsync(string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NexusEndorsementStatus> SetEndorsementAsync(
            string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePrebuiltPakImporter : IPrebuiltPakImporter
    {
        public string? LastPakPath { get; private set; }
        public IReadOnlyList<string> Warnings { get; set; } = [];

        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default)
        {
            LastPakPath = pakFilePath;
            var modName = Path.GetFileNameWithoutExtension(pakFilePath);
            return Task.FromResult(new LibraryEntry
            {
                FolderName = modName, Name = modName, Author = "Unknown", Version = "", Description = "",
                FileName = Path.GetFileName(pakFilePath), Source = source, CatalogEntryId = catalogEntryId, ImportWarnings = Warnings,
            });
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public void Save(string target, string secret)
        {
        }

        public string? Read(string target) => "test-key";

        public void Delete(string target)
        {
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public bool Save() => true;
    }
}
