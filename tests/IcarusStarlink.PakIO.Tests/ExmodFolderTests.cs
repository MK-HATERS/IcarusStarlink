using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodFolderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private static ExmodPackageContents BuildFixture()
    {
        var package = ExmodJson.Parse("""
            {
                "name": "Faster Processors", "author": "A", "version": "1", "description": "D",
                "fileName": "Faster_Processors",
                "Rows": [
                    {"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                     "File_Items": [{"Name": "SmelterRecipe", "CraftTime": 5}]}
                ]
            }
            """);

        var assets = new List<ExmodAssetEntry>
        {
            new("Faster_Processors/Icarus/Content/Data/Crafting.uasset", [1, 2, 3, 4]),
            new("readme.md", "# Faster Processors"u8.ToArray()),
        };

        return new ExmodPackageContents(package, assets);
    }

    [Fact]
    public void RoundTrip_WriteThenRead_ReproducesPackageAndAssets()
    {
        var original = BuildFixture();

        ExmodFolder.Write(_tempDir, original);
        var result = ExmodFolder.Read(_tempDir);

        Assert.Equal(original.Package.Name, result.Package.Name);
        Assert.Single(result.Package.Rows);
        Assert.Equal(2, result.Assets.Count);
        var uasset = Assert.Single(result.Assets, a => a.RelativePath.EndsWith(".uasset"));
        Assert.Equal<byte>([1, 2, 3, 4], uasset.Content);
    }

    [Fact]
    public void Write_PlacesExmodUnderExtractedModsSubfolder()
    {
        ExmodFolder.Write(_tempDir, BuildFixture());

        Assert.True(File.Exists(Path.Combine(_tempDir, "Extracted Mods", "Faster_Processors.EXMOD")));
    }

    [Fact]
    public void Write_Again_ReplacesTheExmodAndLeavesNoTempFile()
    {
        ExmodFolder.Write(_tempDir, BuildFixture());
        var second = BuildFixture();
        second.Package.Description = "Second";

        ExmodFolder.Write(_tempDir, second);

        Assert.Equal("Second", ExmodFolder.ReadPackageOnly(_tempDir).Description);
        Assert.Empty(Directory.GetFiles(Path.Combine(_tempDir, "Extracted Mods"), "*.tmp"));
    }

    /// <summary>A write that can't finish (here: the .EXMOD is held open) must leave the previous file whole, not truncated.</summary>
    [Fact]
    public void Write_ExmodLocked_KeepsTheOldFileWhole()
    {
        ExmodFolder.Write(_tempDir, BuildFixture());
        var exmodPath = Path.Combine(_tempDir, "Extracted Mods", "Faster_Processors.EXMOD");
        var before = File.ReadAllText(exmodPath);
        var second = BuildFixture();
        second.Package.Description = "Second";

        using (new FileStream(exmodPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => ExmodFolder.Write(_tempDir, second));
        }

        Assert.Equal(before, File.ReadAllText(exmodPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(_tempDir, "Extracted Mods"), "*.tmp"));
    }

    [Fact]
    public void Write_AssetCollidingWithReservedExmodPath_ThrowsInsteadOfOverwritingPackageMetadata()
    {
        var package = ExmodJson.Parse("""
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "Foo"}
            """);
        var contents = new ExmodPackageContents(package,
        [
            new ExmodAssetEntry("Extracted Mods/Foo.EXMOD", "attacker-controlled"u8.ToArray()),
        ]);

        Assert.Throws<FormatException>(() => ExmodFolder.Write(_tempDir, contents));
    }

    [Fact]
    public void Write_DuplicateAssetPaths_ThrowsInsteadOfSilentlyOverwriting()
    {
        var package = ExmodJson.Parse("""
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "F"}
            """);
        var contents = new ExmodPackageContents(package,
        [
            new ExmodAssetEntry("a/b.txt", [1]),
            new ExmodAssetEntry("a\\b.txt", [2]), // same path, different separator style
        ]);

        Assert.Throws<FormatException>(() => ExmodFolder.Write(_tempDir, contents));
    }

    [Fact]
    public void Write_AssetWithTraversalPath_ThrowsAndDoesNotEscapeTargetDirectory()
    {
        var package = ExmodJson.Parse("""
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "F"}
            """);
        var contents = new ExmodPackageContents(package, [new ExmodAssetEntry("../../evil.dll", [1, 2, 3])]);

        Assert.Throws<FormatException>(() => ExmodFolder.Write(_tempDir, contents));

        var escapedPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_tempDir))!, "evil.dll");
        Assert.False(File.Exists(escapedPath));
    }

    [Fact]
    public void Write_AssetWithTraversalPath_LeavesNoPartialExmodFileBehind()
    {
        var package = ExmodJson.Parse("""
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "F"}
            """);
        var contents = new ExmodPackageContents(package, [new ExmodAssetEntry("../../evil.dll", [1, 2, 3])]);

        Assert.Throws<FormatException>(() => ExmodFolder.Write(_tempDir, contents));

        Assert.False(File.Exists(Path.Combine(_tempDir, "Extracted Mods", "F.EXMOD")));
    }

    /// <summary>A package whose second asset can't be written: "a/b" is a file, so the folder "a/b" for "a/b/c" can't be made. The write guard can't see that clash.</summary>
    private static ExmodPackageContents WithClashingAssets(string description)
    {
        var package = BuildFixture().Package;
        package.Description = description;
        return new ExmodPackageContents(package, [new ExmodAssetEntry("a/b", [1]), new ExmodAssetEntry("a/b/c", [2])]);
    }

    /// <summary>
    /// Regression: an import whose asset writes failed partway (a full disk) left the new folder
    /// with a valid .EXMOD and missing assets, which loaded as a normal mod on the next rescan.
    /// </summary>
    [Fact]
    public void Write_NewFolderAndAnAssetWriteFails_LeavesNoFolderBehind()
    {
        var target = Path.Combine(_tempDir, "New_Mod");

        Assert.ThrowsAny<IOException>(() => ExmodFolder.Write(target, WithClashingAssets("D")));

        Assert.False(Directory.Exists(target));
    }

    /// <summary>Regression: the .EXMOD used to be written first, so a rewrite whose asset writes failed still replaced the old package.</summary>
    [Fact]
    public void Write_ExistingModAndAnAssetWriteFails_KeepsTheOldExmod()
    {
        ExmodFolder.Write(_tempDir, BuildFixture());

        Assert.ThrowsAny<IOException>(() => ExmodFolder.Write(_tempDir, WithClashingAssets("Second")));

        Assert.True(Directory.Exists(_tempDir));
        Assert.Equal("D", ExmodFolder.ReadPackageOnly(_tempDir).Description);
    }

    /// <summary>
    /// Regression: a folder copied from classic IMM keeps its .EXMOD at the root. Saving it wrote a
    /// second one under "Extracted Mods", and the next scan dropped the mod as "more than one .EXMOD".
    /// </summary>
    [Fact]
    public void Write_ClassicLayoutWithExmodAtTheRoot_RewritesThatFile()
    {
        var package = ExmodJson.Parse("""
            {"name": "Bestiary", "author": "A", "version": "1", "description": "D", "fileName": "Bestiary"}
            """);
        Directory.CreateDirectory(_tempDir);
        var rootExmod = Path.Combine(_tempDir, "Bestiary.EXMOD");
        File.WriteAllText(rootExmod, ExmodJson.Serialize(package));
        File.WriteAllBytes(Path.Combine(_tempDir, "Bestiary.uasset"), [1, 2, 3]);
        package.Description = "Edited";

        ExmodFolder.Write(_tempDir, new ExmodPackageContents(package, []));

        Assert.Equal([rootExmod], Directory.GetFiles(_tempDir, "*.EXMOD", SearchOption.AllDirectories));
        Assert.Equal("Edited", ExmodFolder.ReadPackageOnly(_tempDir).Description);
    }

    [Fact]
    public void TryFindExmodPath_FindsTheOneExmodWhereverItIs_NullWhenNoneOrTwo()
    {
        Assert.Null(ExmodFolder.TryFindExmodPath(_tempDir));
        Directory.CreateDirectory(_tempDir);
        Assert.Null(ExmodFolder.TryFindExmodPath(_tempDir));
        var root = Path.Combine(_tempDir, "Mod.EXMOD");
        File.WriteAllText(root, "{}");
        Assert.Equal(root, ExmodFolder.TryFindExmodPath(_tempDir));
        Directory.CreateDirectory(Path.Combine(_tempDir, "Extracted Mods"));
        File.WriteAllText(Path.Combine(_tempDir, "Extracted Mods", "Other.exmod"), "{}");
        Assert.Null(ExmodFolder.TryFindExmodPath(_tempDir));
    }

    [Fact]
    public void Read_FolderWithTwoExmodFiles_ThrowsFormatExceptionInsteadOfPickingOne()
    {
        Directory.CreateDirectory(_tempDir);
        var json = ExmodJson.Serialize(BuildFixture().Package);
        File.WriteAllText(Path.Combine(_tempDir, "A.EXMOD"), json);
        File.WriteAllText(Path.Combine(_tempDir, "B.EXMOD"), json);

        Assert.Throws<FormatException>(() => ExmodFolder.Read(_tempDir));
    }

    [Fact]
    public void Read_AssetOverTheSizeLimit_ThrowsInsteadOfLoadingItFully()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(
            Path.Combine(_tempDir, "F.EXMOD"),
            ExmodJson.Serialize(BuildFixture().Package));
        using (var fs = File.Create(Path.Combine(_tempDir, "huge.bin")))
        {
            fs.SetLength(ExmodSizeLimits.MaxAssetEntryBytes + 1);
        }

        Assert.Throws<FormatException>(() => ExmodFolder.Read(_tempDir));
    }

    /// <summary>
    /// Regression: an .EXMOD saved as Windows-1252 ("ANSI" in Notepad) had its 0x93/0x94 smart
    /// quotes read as U+FFFD, and saving the mod wrote the replacement characters back for good.
    /// </summary>
    [Fact]
    public void Read_ExmodSavedAsWindows1252_KeepsItsSmartQuotes()
    {
        Directory.CreateDirectory(_tempDir);
        byte[] json =
        [
            .. """{"name": "N", "author": "A", "version": "1", "description": "Say """u8,
            0x93, .. "hi"u8, 0x94,
            .. "\", \"fileName\": \"Ansi_Mod\"}"u8,
        ];
        File.WriteAllBytes(Path.Combine(_tempDir, "Ansi_Mod.EXMOD"), json);

        var result = ExmodFolder.Read(_tempDir);

        Assert.Equal("Say “hi”", result.Package.Description);
    }

    [Fact]
    public void Read_ExmodInUtf8WithABom_StillReadsAsUtf8()
    {
        Directory.CreateDirectory(_tempDir);
        byte[] json =
        [
            0xEF, 0xBB, 0xBF,
            .. """{"name": "N", "author": "A", "version": "1", "description": "Say “hi” é", "fileName": "Utf8_Mod"}"""u8,
        ];
        File.WriteAllBytes(Path.Combine(_tempDir, "Utf8_Mod.EXMOD"), json);

        var result = ExmodFolder.Read(_tempDir);

        Assert.Equal("Say “hi” é", result.Package.Description);
    }

    [Fact]
    public void Read_FolderWithNoExmodFile_ThrowsFormatException()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "readme.md"), "hello");

        Assert.Throws<FormatException>(() => ExmodFolder.Read(_tempDir));
    }

    [Fact]
    public void Read_EveryAssetWrappedInAFolderMatchingTheModsOwnFileName_StripsTheWrapper()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Extracted Mods"));
        var package = ExmodJson.Parse("""
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "Weapons_Pack_1"}
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Extracted Mods", "Weapons_Pack_1.EXMOD"), ExmodJson.Serialize(package));
        Directory.CreateDirectory(Path.Combine(_tempDir, "Weapons_Pack_1", "Pistols"));
        File.WriteAllBytes(Path.Combine(_tempDir, "Weapons_Pack_1", "Pistols", "Pistol_A.uasset"), [1, 2, 3]);

        var result = ExmodFolder.Read(_tempDir);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("Pistols/Pistol_A.uasset", asset.RelativePath);
    }

    [Fact]
    public void Read_AssetsWrappedInFolderNotMatchingModsFileName_LeavesPathsUnchanged()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Extracted Mods"));
        var package = ExmodJson.Parse("""
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "Weapons_Pack_1"}
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Extracted Mods", "Weapons_Pack_1.EXMOD"), ExmodJson.Serialize(package));
        Directory.CreateDirectory(Path.Combine(_tempDir, "SomeOtherFolder"));
        File.WriteAllBytes(Path.Combine(_tempDir, "SomeOtherFolder", "Pistol_A.uasset"), [1, 2, 3]);

        var result = ExmodFolder.Read(_tempDir);

        var asset = Assert.Single(result.Assets);
        Assert.Equal("SomeOtherFolder/Pistol_A.uasset", asset.RelativePath);
    }

    [Fact]
    public void Read_AssetsInSeveralDistinctTopLevelFolders_LeavesPathsUnchanged()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "Extracted Mods"));
        var package = ExmodJson.Parse("""
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "Multi_Folder_Mod"}
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Extracted Mods", "Multi_Folder_Mod.EXMOD"), ExmodJson.Serialize(package));
        Directory.CreateDirectory(Path.Combine(_tempDir, "Outlet"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "Water_Pump"));
        File.WriteAllBytes(Path.Combine(_tempDir, "Outlet", "BP_Outlet.uasset"), [1]);
        File.WriteAllBytes(Path.Combine(_tempDir, "Water_Pump", "BP_Pump.uasset"), [2]);

        var result = ExmodFolder.Read(_tempDir);

        Assert.Equal(2, result.Assets.Count);
        Assert.Contains(result.Assets, a => a.RelativePath == "Outlet/BP_Outlet.uasset");
        Assert.Contains(result.Assets, a => a.RelativePath == "Water_Pump/BP_Pump.uasset");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
