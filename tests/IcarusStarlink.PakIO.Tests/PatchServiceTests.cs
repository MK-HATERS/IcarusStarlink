using IcarusStarlink.Core.Patches;
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

        await _service.ExportAsync(manifest, PatchBundle.Empty, path);

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("Weekend Build", text);
        Assert.DoesNotContain("PK", text[..2]); // not a zip
    }

    [Fact]
    public async Task ExportAsync_ThenImportAsync_RoundTripsGameplayOptions()
    {
        var manifest = new PatchManifest
        {
            ProfileName = "Weekend Build",
            Mods = [],
            Options = new GameplayOptions { XpBoost = XpBoostLevel.Level1, SlotsMultiplier = 2, UnlimitedAmmo = true },
        };
        var path = Path.Combine(_dir, "patch.json");
        await _service.ExportAsync(manifest, PatchBundle.Empty, path);

        var result = await _service.ImportAsync(path);

        Assert.Equal(XpBoostLevel.Level1, result.Manifest.Options.XpBoost);
        Assert.Equal(2, result.Manifest.Options.SlotsMultiplier);
        Assert.True(result.Manifest.Options.UnlimitedAmmo);
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
        await _service.ExportAsync(manifest, PatchBundle.Empty, path);

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

        await _service.ExportAsync(manifest, PatchBundle.Of(bundled), path);

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
        await _service.ExportAsync(manifest, PatchBundle.Of(bundled), path);

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

    /// <summary>A hand-edited or foreign manifest with no mod list (or a null entry) is a corrupt patch, not a crash later on.</summary>
    [Theory]
    [InlineData("""{"ProfileName":"x","Mods":null}""")]
    [InlineData("""{"ProfileName":"x","Mods":[null]}""")]
    [InlineData("""{"ProfileName":"x","Mods":[],"Options":null}""")]
    public async Task ImportAsync_ManifestWithNullParts_ThrowsFormatException(string json)
    {
        var path = Path.Combine(_dir, "nulls.json");
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<FormatException>(() => _service.ImportAsync(path));
    }

    [Fact]
    public async Task ImportAsync_CorruptJsonFile_ThrowsFormatException()
    {
        var path = Path.Combine(_dir, "corrupt.json");
        await File.WriteAllTextAsync(path, "{ not valid json");

        await Assert.ThrowsAsync<FormatException>(() => _service.ImportAsync(path));
    }

    [Fact]
    public async Task ImportAsync_CorruptZipWithNoValidCentralDirectory_ThrowsFormatExceptionNotRawInvalidDataException()
    {
        // Same technique as ExmodzArchiveTests' own corrupt-zip case: a real local file header
        // signature (so ImportAsync's own header sniff routes this to ReadZipPatch) with no End Of
        // Central Directory record after it — ZipArchive's constructor throws InvalidDataException
        // for this, which ReadZipPatch must normalize to FormatException like every other
        // untrusted-archive read in this codebase, rather than let the raw framework type escape.
        var path = Path.Combine(_dir, "corrupt.zip");
        await File.WriteAllBytesAsync(path, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]);

        await Assert.ThrowsAsync<FormatException>(() => _service.ImportAsync(path));
    }

    /// <summary>
    /// Format 2 (2026-10-27): a pak mod from nowhere travels as its pak files, a UE4SS mod with no
    /// Nexus source as its folder (without its logs and enabled.txt), each with its ids and the
    /// UE4SS mod's config text in the manifest; both come back extracted to a temp folder the
    /// result removes when disposed.
    /// </summary>
    [Fact]
    public async Task ExportAsync_ThenImportAsync_FormatTwo_RoundTripsPaksUe4ssModsAndConfigs()
    {
        var pakFolder = Path.Combine(_dir, "paks");
        Directory.CreateDirectory(pakFolder);
        File.WriteAllBytes(Path.Combine(pakFolder, "Model_P.pak"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(pakFolder, "Icons_P.pak"), [4, 5]);
        var ue4ssFolder = Path.Combine(_dir, "MkHatersTools");
        Directory.CreateDirectory(Path.Combine(ue4ssFolder, "Scripts"));
        File.WriteAllText(Path.Combine(ue4ssFolder, "Scripts", "main.lua"), "local VERSION = \"1.5.0\"");
        File.WriteAllText(Path.Combine(ue4ssFolder, "config.lua"), "return { MaxScale = 2.0 }");
        File.WriteAllText(Path.Combine(ue4ssFolder, "MkHatersTools.log"), "noise");
        File.WriteAllText(Path.Combine(ue4ssFolder, "enabled.txt"), "");
        var manifest = new PatchManifest
        {
            FormatVersion = PatchManifest.CurrentFormatVersion,
            ProfileName = "Server",
            Mods =
            [
                new PatchModEntry { FolderName = "Nexus_Mod", Name = "Nexus Mod", Author = "A", Version = "2", Bundled = false, NexusModId = 361, NexusFileId = 1200 },
                new PatchModEntry { FolderName = "My_Pak", Name = "My Pak", Author = "Me", Version = "1", Bundled = true, IsPak = true, PakFileNames = ["Model_P.pak", "Icons_P.pak"] },
            ],
            Ue4ssMods =
            [
                new PatchUe4ssEntry
                {
                    Name = "MkHatersTools", Version = "1.5.0", Bundled = true,
                    Configs = [new PatchConfigFile { Path = "config.lua", Text = "return { MaxScale = 2.0 }" }],
                },
            ],
            IncludesConfigs = true,
        };
        var bundle = new PatchBundle(
            new Dictionary<string, ExmodPackageContents>(),
            new Dictionary<string, IReadOnlyList<string>> { ["My_Pak"] = [Path.Combine(pakFolder, "Model_P.pak"), Path.Combine(pakFolder, "Icons_P.pak")] },
            new Dictionary<string, string> { ["MkHatersTools"] = ue4ssFolder });
        var path = Path.Combine(_dir, "patch.zip");

        await _service.ExportAsync(manifest, bundle, path);

        string extractedRoot;
        using (var result = await _service.ImportAsync(path))
        {
            Assert.Equal(2, result.Manifest.FormatVersion);
            Assert.Equal(361, result.Manifest.Mods[0].NexusModId);
            Assert.Equal(1200, result.Manifest.Mods[0].NexusFileId);
            var paks = Assert.Single(result.BundledPakFolders).Value;
            Assert.Equal(["Icons_P.pak", "Model_P.pak"], Directory.GetFiles(paks).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(paks, "Model_P.pak")));
            var ue4ss = Assert.Single(result.BundledUe4ssFolders).Value;
            Assert.Equal("MkHatersTools", Path.GetFileName(ue4ss));
            Assert.Equal("local VERSION = \"1.5.0\"", File.ReadAllText(Path.Combine(ue4ss, "Scripts", "main.lua")));
            Assert.False(File.Exists(Path.Combine(ue4ss, "MkHatersTools.log")));
            Assert.False(File.Exists(Path.Combine(ue4ss, "enabled.txt")));
            var config = Assert.Single(Assert.Single(result.Manifest.Ue4ssMods).Configs);
            Assert.Equal(("config.lua", "return { MaxScale = 2.0 }"), (config.Path, config.Text));
            extractedRoot = result.ExtractedRoot!;
            Assert.True(Directory.Exists(extractedRoot));
        }

        Assert.False(Directory.Exists(extractedRoot));
    }

    /// <summary>A patch written before format 2 (no FormatVersion, no UE4SS list) still reads, as format 1.</summary>
    [Fact]
    public async Task ImportAsync_FormatOneJson_ReadsAsFormatOne()
    {
        var path = Path.Combine(_dir, "old.json");
        await File.WriteAllTextAsync(path, "{\"ProfileName\":\"Old\",\"Mods\":[{\"FolderName\":\"A\",\"Name\":\"A\",\"Author\":\"B\",\"Version\":\"1\",\"Bundled\":false}],\"Options\":{}}");

        using var result = await _service.ImportAsync(path);

        Assert.Equal(1, result.Manifest.FormatVersion);
        Assert.Empty(result.Manifest.Ue4ssMods);
        Assert.Null(result.Manifest.Mods[0].NexusModId);
        Assert.Empty(result.Manifest.Mods[0].PakFileNames);
        Assert.Null(result.ExtractedRoot);
    }

    /// <summary>A bundled entry whose path climbs out of its folder (a crafted archive) is refused.</summary>
    [Fact]
    public async Task ImportAsync_BundledEntryEscapingItsFolder_ThrowsFormatException()
    {
        var manifest = new PatchManifest
        {
            FormatVersion = 2, ProfileName = "Bad",
            Ue4ssMods = [new PatchUe4ssEntry { Name = "Evil", Bundled = true }],
        };
        var path = Path.Combine(_dir, "evil.zip");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(path), System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            {
                w.Write(System.Text.Json.JsonSerializer.Serialize(manifest));
            }

            using (var w = new StreamWriter(zip.CreateEntry("BundledUe4ss/Evil/../../escape.lua").Open()))
            {
                w.Write("x");
            }
        }

        var ex = await Assert.ThrowsAsync<FormatException>(() => _service.ImportAsync(path));
        Assert.Contains("outside its folder", ex.Message);
    }

    /// <summary>A manifest name that is a path (a crafted patch) is refused before anything is extracted or written: the names decide folders on both sides of an import.</summary>
    [Theory]
    [InlineData("{\"FormatVersion\":2,\"ProfileName\":\"Bad\",\"Mods\":[],\"Options\":{},\"Ue4ssMods\":[{\"Name\":\"..\\\\..\\\\evil\",\"Bundled\":true}]}")]
    [InlineData("{\"FormatVersion\":2,\"ProfileName\":\"Bad\",\"Mods\":[{\"FolderName\":\"../up\",\"Name\":\"A\",\"Author\":\"B\",\"Version\":\"1\",\"Bundled\":true,\"IsPak\":true}],\"Options\":{}}")]
    [InlineData("{\"FormatVersion\":2,\"ProfileName\":\"Bad\",\"Mods\":[{\"FolderName\":\"Ok\",\"Name\":\"A\",\"Author\":\"B\",\"Version\":\"1\",\"Bundled\":true,\"IsPak\":true,\"PakFileNames\":[\"C:\\\\Windows\\\\x.pak\"]}],\"Options\":{}}")]
    public async Task ImportAsync_AManifestNameThatIsAPath_ThrowsFormatException(string json)
    {
        var path = Path.Combine(_dir, "names.json");
        await File.WriteAllTextAsync(path, json);

        var ex = await Assert.ThrowsAsync<FormatException>(() => _service.ImportAsync(path));
        Assert.Contains("isn't a plain name", ex.Message);
    }

    /// <summary>
    /// Review 2026-10-28: a patch's settings are written into UE4SS mods the player runs (game and
    /// server) with no question. A "config" that is a script, or Lua that runs code, is taken out
    /// when the patch is read and named; plain settings stay.
    /// </summary>
    [Fact]
    public async Task ImportAsync_Ue4ssConfigsThatAreCode_AreTakenOutAndNamed()
    {
        var manifest = new PatchManifest
        {
            FormatVersion = 2, ProfileName = "Mixed",
            Ue4ssMods =
            [
                new PatchUe4ssEntry
                {
                    Name = "Tools",
                    Configs =
                    [
                        new PatchConfigFile { Path = "config.lua", Text = "return {\n    MaxScale = 2.0, -- biggest\n    Name = \"x\",\n}\n" },
                        new PatchConfigFile { Path = "config/Pets.ini", Text = "[Pets]\nSpeed=2\n" },
                        new PatchConfigFile { Path = "Scripts/main.lua", Text = "print('hi')" },
                        new PatchConfigFile { Path = "settings.lua", Text = "os.execute('calc') return {}" },
                    ],
                },
            ],
        };
        var path = Path.Combine(_dir, "configs.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(manifest));

        using var result = await _service.ImportAsync(path);

        var mod = Assert.Single(result.Manifest.Ue4ssMods);
        Assert.Equal(["config.lua", "config/Pets.ini"], mod.Configs.Select(c => c.Path));
        Assert.Equal(["Scripts/main.lua", "settings.lua"], mod.RefusedConfigPaths);
    }

    /// <summary>
    /// Review 2026-10-28: a Lua config that is data as a whole could still carry code into the
    /// mod's own file, because the import writes the patch's values into that file line by line,
    /// and a value going into a table setting is written as Lua. A string holding Lua, and lines
    /// hidden in a long comment the line reader takes as settings, are refused.
    /// </summary>
    [Theory]
    [InlineData("return {\n    Keys = \"{ os.execute('calc') }\",\n}\n")]
    [InlineData("return {\n--[==[\n    Keys = { os.execute('calc') },\n--]==]\n}\n")]
    [InlineData("return {\n    Speed = 1, --[[\n    Keys = { os.execute('calc') },\n--]]\n}\n")]
    public void IsSafe_DataLuaWhoseValuesWouldWriteCodeIntoTheMod_IsRefused(string text)
    {
        var modFolder = Path.Combine(_dir, "TrustedMod");
        Directory.CreateDirectory(modFolder);
        File.WriteAllText(Path.Combine(modFolder, "config.lua"), "return {\n    Speed = 1,\n    Keys = { \"F1\" },\n}\n");

        Assert.False(PatchConfigSafety.IsSafe(new PatchConfigFile { Path = "config.lua", Text = text }));

        // What it would have done: the call lands in the mod's own config.lua.
        IcarusStarlink.Core.Ue4ss.Ue4ssModConfigCarryOver.ApplyValues(modFolder, "config.lua", text, Path.Combine(_dir, "backups"));
        Assert.Contains("os.execute", File.ReadAllText(Path.Combine(modFolder, "config.lua")));
    }

    [Fact]
    public void IsSafe_DataLuaWithTableAndStringValues_IsKept()
    {
        Assert.True(PatchConfigSafety.IsSafe(new PatchConfigFile
        {
            Path = "config.lua",
            Text = "return {\n    Keys = { \"F1\", \"F2\" }, -- keys\n    Name = \"Bob {the builder}\",\n    Nested = { A = 1, B = { true } },\n}\n",
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
