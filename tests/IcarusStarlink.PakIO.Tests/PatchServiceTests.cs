using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.PakIO.Tests;

public class PatchServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly PatchService _service = new();

    public PatchServiceTests() => Directory.CreateDirectory(_dir);

    private static ExmodPackageContents MakeExmod(string fileName, string name = "Some Mod", string author = "Author") =>
        new(
            new ExmodPackage { Name = name, Author = author, Version = "1.0", Description = "D", FileName = fileName },
            [new ExmodAssetEntry("readme.md", System.Text.Encoding.UTF8.GetBytes($"# {name}"))]);

    private static PatchModEntry MakeEntry(string folderName, bool bundled, string name = "Some Mod", string author = "Author") =>
        new() { FolderName = folderName, Name = name, Author = author, Version = "1.0", Bundled = bundled };

    [Fact]
    public async Task ExportAsync_NoBundledMods_WritesPlainJsonFile()
    {
        var manifest = new PatchManifest { ProfileName = "Weekend Build", Mods = [MakeEntry("ModA", bundled: false)] };
        var path = Path.Combine(_dir, "patch.json");

        await _service.ExportAsync(manifest, new Dictionary<string, ExmodPackageContents>(), path);

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("Weekend Build", text);
        Assert.DoesNotContain("PK", text[..2]); // not a zip
    }

    [Fact]
    public async Task ExportAsync_ThenImportAsync_JsonPatch_RoundTripsManifestWithNoBundledMods()
    {
        var manifest = new PatchManifest
        {
            ProfileName = "Weekend Build",
            Mods = [MakeEntry("ModA", bundled: false, name: "Mod A"), MakeEntry("ModB", bundled: false, name: "Mod B")],
        };
        var path = Path.Combine(_dir, "patch.json");
        await _service.ExportAsync(manifest, new Dictionary<string, ExmodPackageContents>(), path);

        var result = await _service.ImportAsync(path);

        Assert.Equal("Weekend Build", result.Manifest.ProfileName);
        Assert.Equal(["Mod A", "Mod B"], result.Manifest.Mods.Select(m => m.Name));
        Assert.Empty(result.BundledMods);
    }

    [Fact]
    public async Task ExportAsync_ThenImportAsync_ZipPatch_RoundTripsBundledModContent()
    {
        var manifest = new PatchManifest { ProfileName = "Weekend Build", Mods = [MakeEntry("Local_Edit", bundled: true, name: "Local Edit")] };
        var bundled = new Dictionary<string, ExmodPackageContents> { ["Local_Edit"] = MakeExmod("Local_Edit", "Local Edit") };
        var path = Path.Combine(_dir, "patch.zip");

        await _service.ExportAsync(manifest, bundled, path);

        var result = await _service.ImportAsync(path);
        Assert.Equal("Weekend Build", result.Manifest.ProfileName);
        var contents = Assert.Single(result.BundledMods).Value;
        Assert.Equal("Local Edit", contents.Package.Name);
        Assert.Equal("Local_Edit", contents.Package.FileName);
        var readme = Assert.Single(contents.Assets);
        Assert.Equal("# Local Edit", System.Text.Encoding.UTF8.GetString(readme.Content));
    }

    [Fact]
    public async Task ExportAsync_MixOfBundledAndReferencedMods_ImportSeparatesThemCorrectly()
    {
        var manifest = new PatchManifest
        {
            ProfileName = "Mixed",
            Mods = [MakeEntry("Catalog_Mod", bundled: false, name: "Catalog Mod"), MakeEntry("Local_Mod", bundled: true, name: "Local Mod")],
        };
        var bundled = new Dictionary<string, ExmodPackageContents> { ["Local_Mod"] = MakeExmod("Local_Mod", "Local Mod") };
        var path = Path.Combine(_dir, "patch.zip");
        await _service.ExportAsync(manifest, bundled, path);

        var result = await _service.ImportAsync(path);

        Assert.Equal(2, result.Manifest.Mods.Count);
        Assert.Single(result.BundledMods);
        Assert.True(result.Manifest.Mods.Single(m => m.FolderName == "Local_Mod").Bundled);
        Assert.False(result.Manifest.Mods.Single(m => m.FolderName == "Catalog_Mod").Bundled);
    }

    [Fact]
    public async Task ImportAsync_ZipMissingManifestEntry_ThrowsFormatException()
    {
        var path = Path.Combine(_dir, "bad.zip");
        using (var stream = File.Create(path))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var entryStream = entry.Open();
            entryStream.Write("hello"u8);
        }

        await Assert.ThrowsAsync<FormatException>(() => _service.ImportAsync(path));
    }

    [Fact]
    public async Task ImportAsync_ZipMissingABundledModEntryTheManifestReferences_ThrowsFormatException()
    {
        var manifest = new PatchManifest { ProfileName = "P", Mods = [MakeEntry("Missing_Mod", bundled: true)] };
        var path = Path.Combine(_dir, "bad.zip");
        using (var stream = File.Create(path))
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("manifest.json");
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(System.Text.Json.JsonSerializer.Serialize(manifest));
        }

        await Assert.ThrowsAsync<FormatException>(() => _service.ImportAsync(path));
    }

    [Fact]
    public async Task ImportAsync_CorruptJsonFile_ThrowsFormatException()
    {
        var path = Path.Combine(_dir, "corrupt.json");
        await File.WriteAllTextAsync(path, "{ not valid json");

        await Assert.ThrowsAsync<FormatException>(() => _service.ImportAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
