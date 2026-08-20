using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodFolderLightweightReadTests : IDisposable
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
            new("Icarus/Content/Data/Crafting.uasset", [1, 2, 3, 4]),
            new("readme.md", "# Faster Processors"u8.ToArray()),
        };

        return new ExmodPackageContents(package, assets);
    }

    [Fact]
    public void ReadPackageOnly_ReturnsHeaderAndRows_WithoutRequiringAssetFilesToExist()
    {
        ExmodFolder.Write(_tempDir, BuildFixture());

        var package = ExmodFolder.ReadPackageOnly(_tempDir);

        Assert.Equal("Faster Processors", package.Name);
        Assert.Single(package.Rows);
    }

    [Fact]
    public void ListAssetPaths_ReturnsPathsWithoutReadingContent()
    {
        ExmodFolder.Write(_tempDir, BuildFixture());

        var paths = ExmodFolder.ListAssetPaths(_tempDir);

        Assert.Equal(2, paths.Count);
        Assert.Contains(paths, p => p.EndsWith(".uasset"));
        Assert.Contains(paths, p => p == "readme.md");
    }

    [Fact]
    public void ReadAssetContent_ReturnsOnlyTheRequestedFilesBytes()
    {
        ExmodFolder.Write(_tempDir, BuildFixture());

        var content = ExmodFolder.ReadAssetContent(_tempDir, "readme.md");

        Assert.Equal("# Faster Processors", System.Text.Encoding.UTF8.GetString(content));
    }

    [Fact]
    public void ReadAssetContent_TraversalPath_ThrowsAndDoesNotEscapeTargetDirectory()
    {
        ExmodFolder.Write(_tempDir, BuildFixture());

        Assert.Throws<FormatException>(() => ExmodFolder.ReadAssetContent(_tempDir, "../../evil.dll"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
