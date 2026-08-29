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
