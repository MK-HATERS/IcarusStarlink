using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.PakIO.Tests.Import;

public class PrebuiltPakImporterTests : IDisposable
{
    private readonly string _extractedModsDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _metaDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_meta");
    private readonly string _backupsDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_backups");
    private readonly string _pakFilePath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + ".pak");

    public PrebuiltPakImporterTests() => File.WriteAllText(_pakFilePath, "fake pak bytes");

    private FolderLibraryRepository CreateRepository() =>
        new(_extractedModsDir, _metaDir, _backupsDir, NullLogger<FolderLibraryRepository>.Instance);

    /// <summary>Returns a canned result regardless of input, or null to simulate "conversion isn't possible right now" — the real converter's own logic is exercised separately by PrebuiltPakToExmodConverterTests.</summary>
    private sealed class FakeConverter(ExmodPackageContents? result) : IPrebuiltPakToExmodConverter
    {
        public string? LastName { get; private set; }
        public string? LastAuthor { get; private set; }

        public Task<ExmodPackageContents?> TryConvertAsync(
            string pakFilePath, string dataFolder, string unrealPakExePath, string name, string author,
            MergeReport report, CancellationToken cancellationToken = default)
        {
            LastName = name;
            LastAuthor = author;
            return Task.FromResult(result);
        }
    }

    private static ExmodPackageContents BuildConvertedPackage() => new(
        new ExmodPackage { Name = "Converted", Author = "SomeAuthor", Version = "1.0", Description = "d", FileName = "Converted" },
        []);

    [Fact]
    public async Task ImportAsync_ConversionSucceeds_RegistersARealEditableEntry()
    {
        using var repo = CreateRepository();
        var importer = new PrebuiltPakImporter(new FakeConverter(BuildConvertedPackage()), repo, repo);

        var entry = await importer.ImportAsync(_pakFilePath, "SomeDataFolder", "SomeUnrealPak.exe");

        Assert.False(entry.IsOpaquePak);
        Assert.Equal("Converted", entry.Name);
        Assert.Single(repo.GetAll());
    }

    [Fact]
    public async Task ImportAsync_ConversionFails_FallsBackToOpaqueImport()
    {
        using var repo = CreateRepository();
        var importer = new PrebuiltPakImporter(new FakeConverter(null), repo, repo);

        var entry = await importer.ImportAsync(_pakFilePath, "SomeDataFolder", "SomeUnrealPak.exe");

        Assert.True(entry.IsOpaquePak);
        Assert.Single(repo.GetAll());
    }

    [Fact]
    public async Task ImportAsync_ProvenanceTags_FlowThroughOnConversionSuccess()
    {
        using var repo = CreateRepository();
        var importer = new PrebuiltPakImporter(new FakeConverter(BuildConvertedPackage()), repo, repo);

        var entry = await importer.ImportAsync(_pakFilePath, "SomeDataFolder", "SomeUnrealPak.exe", source: "Nexus", nexusModId: 42);

        Assert.Equal("Nexus", entry.Source);
        Assert.Equal(42, entry.NexusModId);
    }

    [Fact]
    public async Task ImportAsync_NoNameOrAuthorGiven_DefaultsToPakFileNameAndUnknown()
    {
        using var repo = CreateRepository();
        var fakeConverter = new FakeConverter(BuildConvertedPackage());
        var importer = new PrebuiltPakImporter(fakeConverter, repo, repo);

        await importer.ImportAsync(_pakFilePath, "SomeDataFolder", "SomeUnrealPak.exe");

        Assert.Equal(Path.GetFileNameWithoutExtension(_pakFilePath), fakeConverter.LastName);
        Assert.Equal("Unknown", fakeConverter.LastAuthor);
    }

    [Fact]
    public async Task ImportAsync_RealNameAndAuthorGiven_PassedThroughToTheConverter()
    {
        using var repo = CreateRepository();
        var fakeConverter = new FakeConverter(BuildConvertedPackage());
        var importer = new PrebuiltPakImporter(fakeConverter, repo, repo);

        await importer.ImportAsync(_pakFilePath, "SomeDataFolder", "SomeUnrealPak.exe", name: "Real Title", author: "Real Author");

        Assert.Equal("Real Title", fakeConverter.LastName);
        Assert.Equal("Real Author", fakeConverter.LastAuthor);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _extractedModsDir, _metaDir, _backupsDir })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        if (File.Exists(_pakFilePath))
        {
            File.Delete(_pakFilePath);
        }
    }
}
