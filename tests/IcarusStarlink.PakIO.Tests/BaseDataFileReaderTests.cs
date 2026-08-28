using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Tests;

public class BaseDataFileReaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _dataFolder;

    public BaseDataFileReaderTests()
    {
        _dataFolder = Path.Combine(_tempDir, "Data");
        Directory.CreateDirectory(_dataFolder);
    }

    [Fact]
    public void ParseFile_CurrentFileEscapesDataFolder_ReturnsNullWithWarningInsteadOfReadingOutsideDataFolder()
    {
        // CurrentFile is untrusted EXMOD content reachable here via the Library's own "Check mods
        // against game data" staleness feature — a "../"-laden value must never let this read a
        // file outside the extracted game data folder.
        var secretPath = Path.Combine(_tempDir, "secret.json");
        File.WriteAllText(secretPath, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"TopSecret","Value":1}]}""");
        var report = new MergeReport();

        var result = BaseDataFileReader.ParseFile(_dataFolder, "..-secret.json", report);

        Assert.Null(result);
        Assert.Contains(report.Warnings, w => w.Contains("..-secret.json") && w.Contains("valid location"));
    }

    [Fact]
    public void ParseFile_CurrentFileIsRootedAbsolutePath_ReturnsNullWithWarningInsteadOfReadingArbitraryFile()
    {
        var secretPath = Path.Combine(_tempDir, "secret2.json");
        File.WriteAllText(secretPath, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"TopSecret2","Value":1}]}""");
        var report = new MergeReport();

        var result = BaseDataFileReader.ParseFile(_dataFolder, secretPath.Replace('\\', '/'), report);

        Assert.Null(result);
        Assert.Contains(report.Warnings, w => w.Contains("valid location"));
    }

    [Fact]
    public void ParseFile_NormalCurrentFile_StillReadsCorrectly()
    {
        var path = Path.Combine(_dataFolder, "Crafting", "D_ProcessorRecipes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");

        var result = BaseDataFileReader.ParseFile(_dataFolder, "Crafting-D_ProcessorRecipes.json", report: null);

        Assert.NotNull(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
