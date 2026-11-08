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

    /// <summary>
    /// The write itself fails part way (the zip writer refuses two assets at one path), so this
    /// checks the temp file: a file already at the path is kept, and no temp file is left beside it.
    /// </summary>
    [Fact]
    public void WriteZip_FailsWhileWriting_KeepsTheFileAlreadyThereAndLeavesNoTempFile()
    {
        var outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(outDir);
        var output = Path.Combine(outDir, "shared.EXMODZ");
        File.WriteAllBytes(output, [9, 9, 9]);
        var good = OwnModWithAsset();
        var clashing = good with { Assets = [new ExmodAssetEntry("Textures/A.uasset", [1]), new ExmodAssetEntry("textures/a.uasset", [2])] };

        Assert.ThrowsAny<Exception>(() => ModShareExport.WriteZip(clashing, output));

        Assert.Equal([9, 9, 9], File.ReadAllBytes(output));
        Assert.Equal([output], Directory.GetFiles(outDir));
    }

    /// <summary>The scale rules are Starlink's own key; the rows they built are already in the file, so a friend's copy doesn't need them.</summary>
    [Fact]
    public async Task ExportAsync_ModWithScaleRules_LeavesTheRulesOutAndKeepsTheRows()
    {
        var contents = OwnModWithAsset();
        contents.Package.ScaleRules.Add(new ScaleRule { Label = "Stack sizes", CurrentFile = "Traits-D_Itemable.json", Field = "MaxStack", Value = 2 });
        var folder = ModFolder(contents);
        Assert.Contains("starlinkScaleRules", File.ReadAllText(Directory.GetFiles(folder, "*.EXMOD", SearchOption.AllDirectories).Single()));
        var output = Path.Combine(_root, "shared.EXMODZ");

        await ModShareExport.ExportAsync(folder, output);

        using var zip = ZipFile.OpenRead(output);
        using var reader = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase)).Open());
        var exmodText = reader.ReadToEnd();
        Assert.DoesNotContain("starlinkScaleRules", exmodText);
        Assert.Contains("SmelterRecipe", exmodText);
    }

    [Fact]
    public void ForSharing_LeavesTheModsOwnPackageAlone()
    {
        var contents = OwnModWithAsset();
        contents.Package.ScaleRules.Add(new ScaleRule { CurrentFile = "Traits-D_Itemable.json", Field = "MaxStack", Value = 2 });

        var shared = ModShareExport.ForSharing(contents);

        Assert.Empty(shared.Package.ScaleRules);
        Assert.Single(contents.Package.ScaleRules);
    }

    [Theory]
    [InlineData(@"C:\Lib\MyMod\x.EXMODZ", @"C:\Lib", true)]
    [InlineData(@"C:\Lib\x.EXMODZ", @"C:\Lib", true)]
    [InlineData(@"c:\lib\MyMod\Textures\x.EXMODZ", @"C:\Lib\", true)]
    [InlineData(@"C:\Library2\x.EXMODZ", @"C:\Lib", false)]
    [InlineData(@"C:\Users\Me\Documents\x.EXMODZ", @"C:\Lib", false)]
    public void IsInsideFolder_MatchesTheFolderAndEverythingUnderIt(string path, string folder, bool expected) =>
        Assert.Equal(expected, ModShareExport.IsInsideFolder(path, folder));

    [Theory]
    [InlineData(@"C:\Out\Thing.EXMODZ", @"C:\Out\Thing.EXMODZ")]
    [InlineData(@"C:\Out\Thing.exmodz", @"C:\Out\Thing.exmodz")]
    [InlineData(@"C:\Out\Thing.EXMOD", @"C:\Out\Thing.EXMODZ")]
    [InlineData(@"C:\Out\My Mod 1.0", @"C:\Out\My Mod 1.0.EXMODZ")]
    [InlineData(@"C:\Out\Thing.zip", @"C:\Out\Thing.zip.EXMODZ")]
    public void WithShareExtension_AlwaysEndsInExmodz(string path, string expected) =>
        Assert.Equal(expected, ModShareExport.WithShareExtension(path));

    [Theory]
    [InlineData("My Mod", "1.0", "My Mod 1.0.EXMODZ")]
    [InlineData("Bad: name?", "", "Bad_ name_.EXMODZ")]
    [InlineData("", "2", "My mod 2.EXMODZ")]
    [InlineData("Thing.", "", "Thing.EXMODZ")]
    [InlineData("Thing ", " ", "Thing.EXMODZ")]
    [InlineData("...", "", "My mod.EXMODZ")]
    [InlineData("CON", "", "_CON.EXMODZ")]
    [InlineData("nul", "", "_nul.EXMODZ")]
    public void DefaultFileName_UsesNameAndVersion_WithoutCharactersWindowsRefuses(string name, string version, string expected) =>
        Assert.Equal(expected, ModShareExport.DefaultFileName(name, version));
}
