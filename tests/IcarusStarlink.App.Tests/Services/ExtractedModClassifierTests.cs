using System.IO;
using System.IO.Compression;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// ExtractedModClassifier.ClassifyAndImport is the one place that decides what an already-extracted
/// archive actually IS — an EXMOD-shaped mod, a bare prebuilt .pak, or (the catch-all) a UE4SS mod —
/// purely from what's actually sitting on disk, never from the original archive's own file extension
/// or the dialog filter the user picked. It's shared by LibraryViewModel's manual "Import archive…"
/// and DownloadsViewModel's Nexus Activate flow, so a bug here is a bug in both places at once.
/// Fixtures build real folder trees (a real EXMOD via ExmodFolder.Write, a real bare .pak file, a
/// folder with neither) rather than mocking the filesystem — the classifier's own detection
/// (Directory.EnumerateFiles/GetFiles) is real I/O, so a fake would just be testing the fake.
/// </summary>
public sealed class ExtractedModClassifierTests : IDisposable
{
    private readonly string _extractedDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public ExtractedModClassifierTests() => Directory.CreateDirectory(_extractedDir);

    public void Dispose()
    {
        foreach (var folder in _scratchFolders.Append(_extractedDir))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup only — never the point of these tests.
            }
        }
    }

    private static ExmodPackageContents BuildRealExmod(string fileName = "Faster_Processors") => new(
        ExmodJson.Parse($$"""
            {
                "name": "Faster Processors", "author": "Someone", "version": "1.0", "description": "D",
                "fileName": "{{fileName}}",
                "Rows": [
                    {"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                     "File_Items": [{"Name": "SmelterRecipe", "CraftTime": 5}]}
                ]
            }
            """),
        []);

    /// <summary>
    /// The real shape two live Nexus downloads had (ArcFissionLibrary, WirelessPower): the zip
    /// mirrors the game folder. The UE4SS mod must be staged under ITS OWN folder name (not the
    /// download's file name), BOTH paks under Content\Paks must be imported (they belong
    /// together — the old "2 .pak files — ambiguous" refusal was wrong here), and the README's
    /// REQUIREMENTS section must come back so the user is told what else to download.
    /// </summary>
    [Fact]
    public async Task ClassifyAndImport_GameFolderLayoutBundle_StagesTheUe4ssModByNameAndImportsEveryPak()
    {
        var scripts = Path.Combine(_extractedDir, "Icarus", "Binaries", "Win64", "ue4ss", "Mods", "WirelessPower", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "main.lua"), "print('hi')");
        var paks = Path.Combine(_extractedDir, "Icarus", "Content", "Paks", "mods");
        Directory.CreateDirectory(paks);
        File.WriteAllBytes(Path.Combine(paks, "ZZ_WirelessPower_Icon_P.pak"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(paks, "ZZ_WirelessPower_Model_P.pak"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(_extractedDir, "README_WirelessPower.txt"),
            "WirelessPower\n\nREQUIREMENTS\n- UE4SS (experimental build): https://x\n- ArcFission Library 1.2.0 or newer (separate download)\n\nINSTALL\n1. etc");
        var ue4ssModRepository = new FakeUe4ssModRepository { Result = "WirelessPower" };
        var prebuiltPakImporter = new FakePrebuiltPakImporter();
        var library = new FakeLibraryRepository();

        var result = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "WirelessPower 1.0.0 361 1 2026-10-12T13-49Z UAfrx4UUt.zip", library, ue4ssModRepository, prebuiltPakImporter, "SomeDataFolder", null);

        // One download = one mod: the Lua half is staged under its own name, and BOTH paks land in
        // ONE Library entry named after it — never a row per pak.
        Assert.Equal(PendingDownloadActivationKind.Ue4ssMod, result.Kind);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1, ue4ssModRepository.ImportFromFolderCallCount);
        Assert.EndsWith("WirelessPower", ue4ssModRepository.LastSourceFolder);
        Assert.Equal("WirelessPower", ue4ssModRepository.LastFallbackName);
        Assert.Equal(0, prebuiltPakImporter.ImportCallCount);
        var pakSet = Assert.Single(library.ImportPaksCalls);
        Assert.Equal("WirelessPower", pakSet.Name);
        Assert.Equal(["ZZ_WirelessPower_Icon_P.pak", "ZZ_WirelessPower_Model_P.pak"], pakSet.PakFilePaths.Select(Path.GetFileName).ToList());
        Assert.Equal(1, result.Items.Count(i => i.Kind == PendingDownloadActivationKind.Library));
        Assert.Equal(2, result.Requirements.Count);
        Assert.Contains(result.Requirements, r => r.StartsWith("ArcFission Library"));
    }

    /// <summary>A plain UE4SS zip (scripts at the root) still falls back to the file name — minus Nexus's id/timestamp/hash tail, which used to become the mod's name.</summary>
    [Fact]
    public async Task ClassifyAndImport_PlainUe4ssFolder_FallbackNameDropsTheNexusSuffix()
    {
        Directory.CreateDirectory(Path.Combine(_extractedDir, "Scripts"));
        File.WriteAllText(Path.Combine(_extractedDir, "Scripts", "main.lua"), "print('hi')");
        var ue4ssModRepository = new FakeUe4ssModRepository();

        var (_, _, kind, _) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "ArcFissionLibrary V1.2.2 356 3 2026-10-12T13-50Z b0gYfPbb2.zip", new FakeLibraryRepository(), ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Ue4ssMod, kind);
        Assert.Equal("ArcFissionLibrary V1.2.2", ue4ssModRepository.LastFallbackName);
    }

    [Fact]
    public async Task ClassifyAndImport_IoStoreContainerInside_IsRefusedWithTheReason()
    {
        File.WriteAllBytes(Path.Combine(_extractedDir, "pakchunk99-Windows.utoc"), [1]);
        File.WriteAllBytes(Path.Combine(_extractedDir, "pakchunk99-Windows.ucas"), [1]);

        var ex = await Assert.ThrowsAsync<FormatException>(() => ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Some.zip", new FakeLibraryRepository(), new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null));

        Assert.Contains("IoStore", ex.Message);
    }

    [Fact]
    public async Task ClassifyAndImport_SplitAssetPair_StillImportsButWarns()
    {
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        Directory.CreateDirectory(Path.Combine(_extractedDir, "BP"));
        File.WriteAllBytes(Path.Combine(_extractedDir, "BP", "Lonely.uasset"), [1]);

        var result = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Some.zip", new FakeLibraryRepository(), new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, result.Kind);
        Assert.Contains(result.Warnings, w => w.Contains("Lonely.uasset"));
    }

    [Fact]
    public async Task ClassifyAndImport_FolderContainsAnExmodFile_ImportsThroughLibraryRepositoryAsAnExmodMod()
    {
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        var libraryRepository = new FakeLibraryRepository();
        var ue4ssModRepository = new FakeUe4ssModRepository();
        var prebuiltPakImporter = new FakePrebuiltPakImporter();

        var (entryName, folderName, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, ue4ssModRepository, prebuiltPakImporter, "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.False(isOpaquePak);
        Assert.Equal("Faster Processors", entryName);
        Assert.Equal("Faster_Processors", folderName);
        Assert.Equal(1, libraryRepository.ImportCallCount);
        Assert.Equal(_extractedDir, libraryRepository.LastImportSourcePath);
        // Never even looked at the pak/UE4SS branches once a real .EXMOD was found.
        Assert.Equal(0, prebuiltPakImporter.ImportCallCount);
        Assert.Equal(0, ue4ssModRepository.ImportFromFolderCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ExmodFileNestedInASubfolder_StillDetectedViaTheRecursiveScan()
    {
        // A mod author's own zip is very often "zip the whole mod folder", landing the .EXMOD one
        // (or more) levels deep rather than at the archive's own root — hasExmod's own scan is
        // SearchOption.AllDirectories specifically to cover this, not just a top-level check.
        var nestedDir = Path.Combine(_extractedDir, "MyModFolder", "Nested");
        Directory.CreateDirectory(nestedDir);
        ExmodFolder.Write(nestedDir, BuildRealExmod());
        var libraryRepository = new FakeLibraryRepository();

        var (_, _, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.False(isOpaquePak);
        Assert.Equal(1, libraryRepository.ImportCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ExmodExtensionLowercase_StillDetectedCaseInsensitively()
    {
        // ExmodFolder.Write always writes ".EXMOD" (uppercase) itself, so this exercises the
        // detection's own OrdinalIgnoreCase choice directly against a lowercase file a different
        // tool (or a manually-renamed file) could plausibly produce.
        File.WriteAllText(Path.Combine(_extractedDir, "something.exmod"), "{}");
        var libraryRepository = new FakeLibraryRepository();

        var (_, _, kind, _) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.Equal(1, libraryRepository.ImportCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ExmodValidationThrows_PropagatesRatherThanFallingBackToAnotherKind()
    {
        // Genuinely EXMOD-shaped content that fails validation (corrupt JSON, an unsafe asset path,
        // more than one .EXMOD) is a real problem with a real EXMOD import — not a signal to
        // silently reclassify it as a pak or a UE4SS mod instead.
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        var libraryRepository = new FakeLibraryRepository { ImportException = new FormatException("corrupt EXMOD") };

        await Assert.ThrowsAsync<FormatException>(() => ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null));
    }

    [Fact]
    public async Task ClassifyAndImport_SingleBarePakNoExmod_ImportsThroughPrebuiltPakImporter()
    {
        var pakPath = Path.Combine(_extractedDir, "MyMod.pak");
        File.WriteAllBytes(pakPath, [1, 2, 3, 4]);
        var prebuiltPakImporter = new FakePrebuiltPakImporter
        {
            Result = new LibraryEntry
            {
                FolderName = "MyMod", Name = "MyMod", Author = "Unknown", Version = "1.0",
                Description = "", FileName = "MyMod", IsOpaquePak = true,
            },
        };

        var (entryName, folderName, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "MyMod.zip", new FakeLibraryRepository(), new FakeUe4ssModRepository(), prebuiltPakImporter, "SomeDataFolder", "SomeUnrealPak.exe");

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.True(isOpaquePak);
        Assert.Equal("MyMod", entryName);
        Assert.Equal("MyMod", folderName);
        Assert.Equal(1, prebuiltPakImporter.ImportCallCount);
        Assert.Equal(pakPath, prebuiltPakImporter.LastPakFilePath);
    }

    [Fact]
    public async Task ClassifyAndImport_PakConvertsToARealExmod_IsOpaquePakReflectsWhatTheImporterReturned()
    {
        // Kind alone can't distinguish "a real EXMOD" from "a pak that converted into one" — both
        // are PendingDownloadActivationKind.Library — only IsOpaquePak does, and it must come
        // straight from IPrebuiltPakImporter's own result, not be hardcoded true just because a
        // .pak file was what triggered this branch.
        File.WriteAllBytes(Path.Combine(_extractedDir, "MyMod.pak"), [1, 2, 3, 4]);
        var prebuiltPakImporter = new FakePrebuiltPakImporter
        {
            Result = new LibraryEntry
            {
                FolderName = "MyMod", Name = "MyMod", Author = "Unknown", Version = "1.0",
                Description = "", FileName = "MyMod", IsOpaquePak = false,
            },
        };

        var (_, _, _, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "MyMod.zip", new FakeLibraryRepository(), new FakeUe4ssModRepository(), prebuiltPakImporter, "SomeDataFolder", "SomeUnrealPak.exe");

        Assert.False(isOpaquePak);
    }

    /// <summary>One download = one mod: two loose paks become ONE opaque entry named after the download (this used to be refused as "ambiguous", which left a real user stuck).</summary>
    [Fact]
    public async Task ClassifyAndImport_MultiplePaksNoExmod_LandsAsOneLibraryModHoldingBothPaks()
    {
        File.WriteAllBytes(Path.Combine(_extractedDir, "First.pak"), [1]);
        File.WriteAllBytes(Path.Combine(_extractedDir, "Second.pak"), [2]);
        var prebuiltPakImporter = new FakePrebuiltPakImporter();
        var library = new FakeLibraryRepository();

        var result = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Cool Mod 1.2 412 7 2026-10-12T13-49Z abc.zip", library, new FakeUe4ssModRepository(), prebuiltPakImporter, "SomeDataFolder", null);

        var call = Assert.Single(library.ImportPaksCalls);
        Assert.Equal("Cool Mod 1.2", call.Name);
        Assert.Equal(["First.pak", "Second.pak"], call.PakFilePaths.Select(Path.GetFileName).ToList());
        Assert.Equal(0, prebuiltPakImporter.ImportCallCount);
        Assert.Single(result.Items);
        Assert.Equal(PendingDownloadActivationKind.Library, result.Kind);
    }

    [Fact]
    public async Task ClassifyAndImport_ExmodAndABarePakBothPresent_ExmodTakesPriorityOverThePak()
    {
        // A real mod could plausibly ship both — the .EXMOD is what actually carries this mod's own
        // declared identity/content, so it must win outright rather than the ambiguous-pak-count
        // check (or the single-pak import) ever getting a look at the .pak sitting alongside it.
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        File.WriteAllBytes(Path.Combine(_extractedDir, "Bundled.pak"), [1, 2, 3]);
        var libraryRepository = new FakeLibraryRepository();
        var prebuiltPakImporter = new FakePrebuiltPakImporter();

        var (_, _, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), prebuiltPakImporter, "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.False(isOpaquePak);
        Assert.Equal(1, libraryRepository.ImportCallCount);
        Assert.Equal(0, prebuiltPakImporter.ImportCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_NeitherExmodNorPak_FallsBackToAUe4ssModImport()
    {
        // The one kind this app handles that carries no metadata file of its own to detect it by —
        // a real UE4SS mod's own shape is just Lua scripts/assets under an arbitrary folder name.
        var scriptsDir = Path.Combine(_extractedDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "main.lua"), "print('hello')");
        var ue4ssModRepository = new FakeUe4ssModRepository { Result = "CoolMod_1" };

        var (entryName, folderName, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "CoolMod.zip", new FakeLibraryRepository(), ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Ue4ssMod, kind);
        Assert.False(isOpaquePak);
        Assert.Equal("CoolMod_1", entryName);
        Assert.Equal("CoolMod_1", folderName);
        Assert.Equal(1, ue4ssModRepository.ImportFromFolderCallCount);
        Assert.Equal(_extractedDir, ue4ssModRepository.LastSourceFolder);
        // The archive's own original filename (sans extension), not some path derived from the
        // extracted temp directory — that temp folder name is a random Guid, meaningless to a user.
        Assert.Equal("CoolMod", ue4ssModRepository.LastFallbackName);
    }

    /// <summary>
    /// Regression test: a real user bug report — a downloaded EXMODZ silently landed as a UE4SS mod
    /// instead. Root cause: the old fallback treated "neither EXMOD-shaped nor a single pak" as
    /// always meaning "this is a UE4SS mod," even for a folder full of raw Unreal asset files
    /// (.uasset/.uexp — exactly what a real EXMOD's own BP/ subfolder contains) with no .EXMOD
    /// manifest alongside them. That shape is a broken/incomplete download or a packaging mistake,
    /// never a genuine UE4SS mod (UE4SS mods are Lua scripts; loose .uasset/.uexp content is inert
    /// to it) — silently staging it as UE4SS previously reported success while importing something
    /// completely non-functional.
    /// </summary>
    [Fact]
    public async Task ClassifyAndImport_UnrealAssetFilesWithNoExmodManifest_ThrowsInsteadOfMisfilingAsUe4ss()
    {
        var bpDir = Path.Combine(_extractedDir, "BP", "Objects");
        Directory.CreateDirectory(bpDir);
        File.WriteAllBytes(Path.Combine(bpDir, "BP_Something.uasset"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(bpDir, "BP_Something.uexp"), [4, 5, 6]);
        var ue4ssModRepository = new FakeUe4ssModRepository();

        var ex = await Assert.ThrowsAsync<FormatException>(() => ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SteadfastStructures.EXMODZ", new FakeLibraryRepository(), ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null));

        Assert.Contains(".EXMOD manifest", ex.Message);
        Assert.Equal(0, ue4ssModRepository.ImportFromFolderCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_PureLuaContentNoUnrealAssets_StillFallsBackToUe4ssModImport()
    {
        // Confirms the new .uasset/.uexp check doesn't overreach — a genuine UE4SS mod (plain Lua,
        // nothing Unreal-asset-shaped) must keep working exactly as before.
        var scriptsDir = Path.Combine(_extractedDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "main.lua"), "print('hello')");
        var ue4ssModRepository = new FakeUe4ssModRepository { Result = "CoolMod_1" };

        var (_, _, kind, _) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "CoolMod.zip", new FakeLibraryRepository(), ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Ue4ssMod, kind);
        Assert.Equal(1, ue4ssModRepository.ImportFromFolderCallCount);
    }

    /// <summary>Zips the given folder's contents into archivePath — a real zip, so the classifier's own AnyArchiveExtractor does real extraction work rather than a fake.</summary>
    private static void ZipFolderInto(string sourceFolder, string archivePath) => ZipFile.CreateFromDirectory(sourceFolder, archivePath);

    private readonly List<string> _scratchFolders = [];

    /// <summary>A folder OUTSIDE _extractedDir on purpose — anything inside it would be scanned by the classifier itself and skew the result. Cleaned up in Dispose.</summary>
    private string NewScratchFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", $"scratch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _scratchFolders.Add(path);
        return path;
    }

    /// <summary>
    /// Regression test: a real user Nexus bug report, confirmed by inspecting the actual downloaded
    /// file — the Nexus upload was a .zip wrapping a single .EXMODZ (the author zipped their mod
    /// file for upload). After the outer extraction the folder held one .EXMODZ and nothing else, so
    /// the classifier found no .EXMOD/.pak/.uasset and fell straight through to the UE4SS catch-all,
    /// "staging" a useless UE4SS mod. The inner archive is the real mod and must be unpacked and
    /// classified as if it had been the download itself.
    /// </summary>
    [Fact]
    public async Task ClassifyAndImport_SingleNestedExmodzArchive_UnpacksItAndImportsAsAnExmodMod()
    {
        var innerModFolder = NewScratchFolder();
        ExmodFolder.Write(innerModFolder, BuildRealExmod());
        ZipFolderInto(innerModFolder, Path.Combine(_extractedDir, "Ferani_Steadfast_Structures.EXMODZ"));
        var libraryRepository = new FakeLibraryRepository();
        var ue4ssModRepository = new FakeUe4ssModRepository();

        var (entryName, _, kind, isOpaquePak) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Ferani_Steadfast_Structures_EXMODZ-339-1.zip", libraryRepository, ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Library, kind);
        Assert.False(isOpaquePak);
        Assert.Equal("Faster Processors", entryName);
        Assert.Equal(1, libraryRepository.ImportCallCount);
        Assert.Equal(0, ue4ssModRepository.ImportFromFolderCallCount);
        // Imported from the unpacked inner folder (a subfolder of the extraction dir), not the
        // outer folder — so the now-redundant outer .EXMODZ file itself never ends up in the Library.
        Assert.NotNull(libraryRepository.LastImportSourcePath);
        Assert.NotEqual(_extractedDir, libraryRepository.LastImportSourcePath);
        Assert.StartsWith(_extractedDir, libraryRepository.LastImportSourcePath);
    }

    [Fact]
    public async Task ClassifyAndImport_SingleNestedArchiveHoldingALuaMod_StillClassifiesAsUe4ss_NamedAfterTheInnerArchive()
    {
        // The nested unpack is format-agnostic — it re-runs the whole classification on what's
        // inside, so a zipped-up UE4SS mod still lands as UE4SS. Named after the INNER archive
        // (the mod's own file name), not the outer one, which for a real Nexus download is a
        // mangled "Name-339-1-1758806700" CDN string nobody would want as a mod folder name.
        var innerModFolder = NewScratchFolder();
        Directory.CreateDirectory(Path.Combine(innerModFolder, "Scripts"));
        File.WriteAllText(Path.Combine(innerModFolder, "Scripts", "main.lua"), "print('hello')");
        ZipFolderInto(innerModFolder, Path.Combine(_extractedDir, "CoolLuaMod.zip"));
        var ue4ssModRepository = new FakeUe4ssModRepository { Result = "CoolLuaMod_1" };

        var (_, _, kind, _) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "CoolLuaMod-123-1-999.zip", new FakeLibraryRepository(), ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Ue4ssMod, kind);
        Assert.Equal(1, ue4ssModRepository.ImportFromFolderCallCount);
        Assert.Equal("CoolLuaMod", ue4ssModRepository.LastFallbackName);
    }

    [Fact]
    public async Task ClassifyAndImport_TwoNestedArchives_ThrowsInsteadOfGuessingWhichIsTheMod()
    {
        var innerModFolder = NewScratchFolder();
        ExmodFolder.Write(innerModFolder, BuildRealExmod());
        ZipFolderInto(innerModFolder, Path.Combine(_extractedDir, "VariantA.EXMODZ"));
        ZipFolderInto(innerModFolder, Path.Combine(_extractedDir, "VariantB.EXMODZ"));
        var libraryRepository = new FakeLibraryRepository();
        var ue4ssModRepository = new FakeUe4ssModRepository();

        var ex = await Assert.ThrowsAsync<FormatException>(() => ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Bundle.zip", libraryRepository, ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null));

        Assert.Contains("2 archives", ex.Message);
        Assert.Equal(0, libraryRepository.ImportCallCount);
        Assert.Equal(0, ue4ssModRepository.ImportFromFolderCallCount);
    }

    /// <summary>
    /// The real "Minimap V1.0.5" Nexus download: two .EXMODZ files that are the SAME mod in English
    /// and Brazilian Portuguese ("add ONE of these", says its readme), plus a loose
    /// "UE4SS Mods/MiniMapa" script folder. The user picks one; the scripts come along as a UE4SS mod.
    /// </summary>
    private void BuildMinimapDownload()
    {
        foreach (var (file, name) in new[] { ("Minimap", "Minimap & Map Icons"), ("Minimap_PTBR", "Minimapa e Icones do Mapa") })
        {
            var folder = NewScratchFolder();
            var contents = BuildRealExmod();
            contents.Package.Name = name;
            contents.Package.FileName = file;
            ExmodFolder.Write(folder, contents);
            Directory.CreateDirectory(Path.Combine(_extractedDir, "Icarus Mod Manager"));
            ZipFolderInto(folder, Path.Combine(_extractedDir, "Icarus Mod Manager", file + ".EXMODZ"));
        }

        var scripts = Path.Combine(_extractedDir, "UE4SS Mods", "MiniMapa", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "main.lua"), "-- minimap");
        File.WriteAllText(Path.Combine(_extractedDir, "UE4SS Mods", "MiniMapa", "enabled.txt"), "");
        File.WriteAllText(Path.Combine(_extractedDir, "README_EN.txt"), "Add ONE of the files.");
    }

    [Fact]
    public async Task ClassifyAndImport_AlternativeArchives_AsksWhichOne_AndBringsTheLooseUe4ssScriptsAlong()
    {
        BuildMinimapDownload();
        var libraryRepository = new FakeLibraryRepository();
        var ue4ssModRepository = new FakeUe4ssModRepository { Result = "MiniMapa" };
        IReadOnlyList<ArchiveChoice>? offered = null;

        var result = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Minimap V1.0.5.zip", libraryRepository, ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null,
            source: "Nexus", nexusModId: 380,
            chooseArchive: (modName, choices) => { offered = choices; return choices.ToList().FindIndex(c => c.Title.Contains("Minimapa")); });

        Assert.NotNull(offered);
        Assert.Equal(2, offered.Count);
        Assert.Contains(offered, c => c.Title.StartsWith("Minimap & Map Icons", StringComparison.Ordinal));
        Assert.Equal(1, libraryRepository.ImportCallCount);
        Assert.Equal("Minimapa e Icones do Mapa", ExmodFolder.Read(libraryRepository.LastImportSourcePath!).Package.Name);
        Assert.Equal(1, ue4ssModRepository.ImportFromFolderCallCount);
        Assert.Equal("MiniMapa", ue4ssModRepository.LastFallbackName);
        Assert.EndsWith(Path.Combine("UE4SS Mods", "MiniMapa"), ue4ssModRepository.LastSourceFolder);
        Assert.Equal([PendingDownloadActivationKind.Library, PendingDownloadActivationKind.Ue4ssMod], result.Items.Select(i => i.Kind));
    }

    [Fact]
    public async Task ClassifyAndImport_AlternativeArchives_CancelledChoice_ImportsNothing()
    {
        BuildMinimapDownload();
        var libraryRepository = new FakeLibraryRepository();
        var ue4ssModRepository = new FakeUe4ssModRepository();

        var ex = await Assert.ThrowsAsync<FormatException>(() => ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Minimap V1.0.5.zip", libraryRepository, ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null,
            chooseArchive: (_, _) => null));

        Assert.Contains("cancelled", ex.Message);
        Assert.Equal(0, libraryRepository.ImportCallCount);
        Assert.Equal(0, ue4ssModRepository.ImportFromFolderCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ArchivesNestedTooDeep_ThrowsRatherThanUnpackingForever()
    {
        // Four layers of archive-in-archive: the classifier unpacks three (its own cap) and refuses
        // the fourth — a bound against a hostile chain that would otherwise extract without end.
        var innerModFolder = NewScratchFolder();
        ExmodFolder.Write(innerModFolder, BuildRealExmod());
        var current = innerModFolder;
        for (var layer = 0; layer < 4; layer++)
        {
            var wrapper = NewScratchFolder();
            ZipFolderInto(current, Path.Combine(wrapper, $"Layer{layer}.zip"));
            current = wrapper;
        }
        ZipFolderInto(current, Path.Combine(_extractedDir, "Outer.zip"));
        var libraryRepository = new FakeLibraryRepository();

        var ex = await Assert.ThrowsAsync<FormatException>(() => ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Download.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(), "SomeDataFolder", null));

        Assert.Contains("nested more than", ex.Message);
        Assert.Equal(0, libraryRepository.ImportCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_EmptyExtractedFolder_StillFallsBackToUe4ssModImport()
    {
        // An edge case worth pinning down explicitly: nothing recognizable at all still resolves to
        // SOME outcome (the UE4SS catch-all) rather than throwing — ImportOnePath's caller has
        // nothing better to fall back to either.
        var ue4ssModRepository = new FakeUe4ssModRepository { Result = "Empty_1" };

        var (_, _, kind, _) = await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "Empty.zip", new FakeLibraryRepository(), ue4ssModRepository, new FakePrebuiltPakImporter(), "SomeDataFolder", null);

        Assert.Equal(PendingDownloadActivationKind.Ue4ssMod, kind);
        Assert.Equal(1, ue4ssModRepository.ImportFromFolderCallCount);
    }

    [Fact]
    public async Task ClassifyAndImport_ProvenanceTags_FlowThroughToLibraryImport()
    {
        ExmodFolder.Write(_extractedDir, BuildRealExmod());
        var libraryRepository = new FakeLibraryRepository();

        await ExtractedModClassifier.ClassifyAndImport(
            _extractedDir, "SomeArchive.zip", libraryRepository, new FakeUe4ssModRepository(), new FakePrebuiltPakImporter(),
            "SomeDataFolder", null, source: "Nexus", nexusModId: 42);

        Assert.Equal("Nexus", libraryRepository.LastImportSource);
        Assert.Equal(42, libraryRepository.LastImportNexusModId);
    }

    private sealed class FakeLibraryRepository : ILibraryRepository
    {
        public Exception? ImportException { get; set; }
        public int ImportCallCount { get; private set; }
        public string? LastImportSourcePath { get; private set; }
        public string? LastImportSource { get; private set; }
        public int? LastImportNexusModId { get; private set; }

        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
        {
            ImportCallCount++;
            LastImportSourcePath = sourcePath;
            LastImportSource = source;
            LastImportNexusModId = nexusModId;
            if (ImportException is not null)
            {
                throw ImportException;
            }

            return new LibraryEntry
            {
                FolderName = "Faster_Processors", Name = "Faster Processors", Author = "Someone",
                Version = "1.0", Description = "D", FileName = "Faster_Processors",
            };
        }

        public IReadOnlyList<LibraryEntry> GetAll() => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> UnreadableFolders => [];
        public IReadOnlyList<LibraryEntry> Search(string query) => throw new NotSupportedException("Not exercised by these tests.");
        public List<(IReadOnlyList<string> PakFilePaths, string Name, string? Source, int? NexusModId)> ImportPaksCalls { get; } = [];
        public LibraryEntry ImportPaks(IReadOnlyList<string> pakFilePaths, string name, string? source = null, int? nexusModId = null, string? catalogEntryId = null)
        {
            ImportPaksCalls.Add((pakFilePaths, name, source, nexusModId));
            return new LibraryEntry { FolderName = name, Name = name, Author = "Unknown", Version = "", Description = "", FileName = name, IsOpaquePak = true, PakFileNames = [.. pakFilePaths.Select(Path.GetFileName)!] };
        }
        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null) =>
            throw new NotSupportedException("Not exercised by these tests — ExtractedModClassifier goes through IPrebuiltPakImporter, not this directly.");
        public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void Refresh() => throw new NotSupportedException("Not exercised by these tests.");
        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void MarkLocallyEdited(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void MarkConvertedFromPrebuiltPak(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void SetDisplayNameOverride(string folderName, string? displayName) => throw new NotSupportedException("Not exercised by these tests.");
        public void LinkToNexus(string folderName, int nexusModId) => throw new NotSupportedException("Not exercised by these tests.");
        public void SetCatalogEntry(string folderName, string catalogEntryId) => throw new NotSupportedException("Not exercised by these tests.");
        public string BackupMod(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public bool HasModBackup(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public bool RestoreLatestModBackup(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string? TryGetLatestModBackupPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public LibraryEntry CreateBlankMod(string name, string author, ModTemplate template = ModTemplate.Blank) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListAssetPaths(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListAssetPaths(string folderName, IReadOnlyList<string> precomputedFiles) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public byte[] ReadAssetContent(string folderName, string relativePath) => throw new NotSupportedException("Not exercised by these tests.");
        public string? ReadReadme(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string? ReadReadme(string folderName, IReadOnlyList<string> precomputedFiles) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListFolderFiles(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUe4ssModRepository : IUe4ssModRepository
    {
        public string Result { get; set; } = "SomeMod_1";
        public int ImportFromFolderCallCount { get; private set; }
        public string? LastSourceFolder { get; private set; }
        public string? LastFallbackName { get; private set; }

        public string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null)
        {
            ImportFromFolderCallCount++;
            LastSourceFolder = sourceFolder;
            LastFallbackName = fallbackName;
            return Result;
        }

        public IReadOnlyList<string> GetAll() => throw new NotSupportedException("Not exercised by these tests.");
        public string Import(string zipFilePath, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");
        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) => throw new NotSupportedException("Not exercised by these tests.");
        public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakePrebuiltPakImporter : IPrebuiltPakImporter
    {
        public LibraryEntry Result { get; set; } = new()
        {
            FolderName = "MyMod", Name = "MyMod", Author = "Unknown", Version = "1.0", Description = "", FileName = "MyMod", IsOpaquePak = true,
        };

        public int ImportCallCount { get; private set; }
        public string? LastPakFilePath { get; private set; }

        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default)
        {
            ImportCallCount++;
            LastPakFilePath = pakFilePath;
            return Task.FromResult(Result);
        }
    }
}
