using System.IO;
using System.IO.Compression;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>Export for sharing: one of the user's own mods written as an .EXMODZ a friend or Nexus can use.</summary>
public sealed class ModShareExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ModShareExport_" + Guid.NewGuid().ToString("N"));

    public ModShareExportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    internal static ExmodPackageContents OwnModWithAsset() => new(
        ExmodJson.Parse("""
            {
                "name": "My Faster Smelter", "author": "Me", "version": "1.2", "description": "Smelts faster.",
                "fileName": "My_Faster_Smelter",
                "Rows": [
                    {"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                     "File_Items": [{"Name": "SmelterRecipe", "CraftTime": 5}]}
                ]
            }
            """),
        [new ExmodAssetEntry("Textures/T_MySmelter.uasset", [1, 2, 3, 4]), new ExmodAssetEntry("Textures/T_MySmelter.uexp", [5, 6])]);

    private string ModFolder(ExmodPackageContents contents)
    {
        var folder = Path.Combine(_root, "Extracted_Mods", contents.Package.FileName);
        ExmodFolder.Write(folder, contents);
        return folder;
    }

    [Fact]
    public async Task ExportAsync_OwnModWithAnAsset_ReadsBackAsTheSamePackageAndAssets()
    {
        var contents = OwnModWithAsset();
        var output = Path.Combine(_root, "out", "My Faster Smelter 1.2.EXMODZ");

        await ModShareExport.ExportAsync(ModFolder(contents), output);

        var read = ExmodzArchive.Read(output);
        Assert.Equal(ExmodJson.Serialize(contents.Package), ExmodJson.Serialize(read.Package));
        Assert.Equal(
            contents.Assets.OrderBy(a => a.RelativePath, StringComparer.Ordinal),
            read.Assets.OrderBy(a => a.RelativePath, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExportAsync_EveryEntryPath_IsRelativeAndStaysInsideTheZip()
    {
        var output = Path.Combine(_root, "shared.EXMODZ");

        await ModShareExport.ExportAsync(ModFolder(OwnModWithAsset()), output);

        using var zip = ZipFile.OpenRead(output);
        Assert.NotEmpty(zip.Entries);
        foreach (var entry in zip.Entries)
        {
            Assert.False(Path.IsPathRooted(entry.FullName), entry.FullName);
            Assert.DoesNotContain("..", entry.FullName.Split('/'));
            Assert.DoesNotContain('\\', entry.FullName);
            Assert.DoesNotContain(_root, entry.FullName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Starlink's own notes beside the mod (the player points settings) say how this copy was made; a friend's copy doesn't need them.</summary>
    [Fact]
    public async Task ExportAsync_StarlinkSettingsFileInTheModFolder_IsLeftOut()
    {
        var folder = ModFolder(OwnModWithAsset());
        File.WriteAllText(Path.Combine(folder, "starlink-player-points.json"), "[]");
        var output = Path.Combine(_root, "shared.EXMODZ");

        await ModShareExport.ExportAsync(folder, output);

        using var zip = ZipFile.OpenRead(output);
        Assert.DoesNotContain(zip.Entries, e => e.Name.StartsWith("starlink-", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(zip.Entries, e => e.FullName == "Textures/T_MySmelter.uasset");
    }

    [Fact]
    public async Task ExportAsync_ModFolderUnreadable_KeepsTheFileAlreadyThere()
    {
        var output = Path.Combine(_root, "shared.EXMODZ");
        File.WriteAllBytes(output, [9, 9, 9]);
        var brokenFolder = Path.Combine(_root, "NoExmodHere");
        Directory.CreateDirectory(brokenFolder);

        await Assert.ThrowsAnyAsync<Exception>(() => ModShareExport.ExportAsync(brokenFolder, output));

        Assert.Equal([9, 9, 9], File.ReadAllBytes(output));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("My Mod", "1.0", "My Mod 1.0.EXMODZ")]
    [InlineData("Bad: name?", "", "Bad_ name_.EXMODZ")]
    [InlineData("", "2", "My mod 2.EXMODZ")]
    public void DefaultFileName_UsesNameAndVersion_WithoutCharactersWindowsRefuses(string name, string version, string expected) =>
        Assert.Equal(expected, ModShareExport.DefaultFileName(name, version));
}
