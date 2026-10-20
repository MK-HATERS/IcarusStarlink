using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// A Library mod whose cooked blueprint uses a game file the game has since moved (the case of
/// Jimk's Small Electrical wires: M_ITM_CableHand went from ASS/DEP to ASS/ITM), through the fix
/// service: listed as a sure fix, fixed with a backup, remembered, and made again after the file
/// comes back as it was (a re-conversion). The file is a real cooked Icarus blueprint (Wax's
/// MIT-licensed sample, PakIO.Tests/Fixtures/WaxSample) that uses /Game/BP/Utilities/WT/WT_CaveLight.
/// </summary>
public sealed class ModFixServiceAssetTests : IDisposable
{
    private const string OldPlace = "/Game/BP/Utilities/WT/WT_CaveLight";
    private const string NewPlace = "/Game/BP/World/WT_CaveLight";
    private const string ModFile = "BP/Wax/BP_WaxSampleCaveLight.uasset";

    private static readonly string FixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WaxSample");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ModFixAssets_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;
    private readonly string _folder;
    private readonly FakeGame _game = new();

    public ModFixServiceAssetTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        var table = Path.Combine(DataFolder, "Items", "D_ItemsStatic.json");
        Directory.CreateDirectory(Path.GetDirectoryName(table)!);
        File.WriteAllText(table, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Wood_Floor","Weight":100}]}""");

        _folder = _library.CreateBlankMod("Cave Light", "Me", ModTemplate.Blank).FolderName;
        PutOriginalFile();
        _game[NewPlace] = CaveLightObjects();
    }

    public void Dispose()
    {
        _library.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DataFolder => Path.Combine(_root, "Data");

    private string ModFilePath => Path.Combine(_library.GetFolderPath(_folder), "BP", "Wax", "BP_WaxSampleCaveLight.uasset");

    private ModFixService Service(IGameAssetCatalog? game = null) =>
        new(_library, DataFolder, decisionsDirectory: Path.Combine(_root, "Library_Meta", "Fix_Choices"), gameAssets: game ?? _game);

    private static Dictionary<string, string> CaveLightObjects() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["WT_CaveLight_C"] = "BlueprintGeneratedClass",
        ["Default__WT_CaveLight_C"] = "WT_CaveLight_C",
        ["WT_CaveLight_C.PointLight_GEN_VARIABLE"] = "PointLightComponent",
        ["WT_CaveLight_C.DefaultSceneRoot_GEN_VARIABLE"] = "SceneComponent",
    };

    /// <summary>The mod's file as the author shipped it (what a re-conversion of a prebuilt pak puts back).</summary>
    private void PutOriginalFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModFilePath)!);
        foreach (var extension in new[] { ".uasset", ".uexp" })
        {
            File.Copy(Path.Combine(FixtureFolder, "BP_WaxSampleCaveLight" + extension), Path.ChangeExtension(ModFilePath, extension), overwrite: true);
        }
    }

    private IReadOnlyList<string?> UsedPackages()
    {
        var header = UnrealPackageHeader.TryRead(File.ReadAllBytes(ModFilePath))!;
        return [.. Enumerable.Range(0, header.Imports.Count).Select(header.ImportPackage).Distinct()];
    }

    private static IEnumerable<ModIssue> AssetIssues(ModHealthReport report) =>
        report.Issues.Where(i => i.Kind is ModIssueKind.MissingAsset or ModIssueKind.MissingAssetPart);

    [Fact]
    public void Check_AMovedGameFile_IsASureFix()
    {
        var issue = Assert.Single(AssetIssues(Service().Check(_folder)));

        Assert.Equal(ModIssueKind.MissingAsset, issue.Kind);
        Assert.Equal(ModFile, issue.CurrentFile);
        Assert.Equal(OldPlace, issue.ItemName);
        Assert.Equal(NewPlace, issue.SuggestedRow);
        Assert.True(issue.IsAutomatic);
        Assert.Equal([ModIssueFix.Repoint, ModIssueFix.KeepMod], issue.Choices);
    }

    [Fact]
    public void FixSure_PointsTheFileThere_WithABackup()
    {
        var service = Service();

        var (applied, _) = service.FixSure(_folder);

        Assert.Equal(1, applied);
        Assert.Contains(NewPlace, UsedPackages());
        Assert.DoesNotContain(OldPlace, UsedPackages());
        Assert.Empty(AssetIssues(service.Check(_folder)));
        Assert.NotNull(_library.GetAll().Single(e => e.FolderName == _folder).FixedByIslAtUtc);
        Assert.True(service.HasBackup(_folder));

        Assert.True(service.RestoreBackup(_folder));
        Assert.Equal(File.ReadAllBytes(Path.Combine(FixtureFolder, "BP_WaxSampleCaveLight.uasset")), File.ReadAllBytes(ModFilePath));
    }

    /// <summary>A converted prebuilt pak is converted again after each game update, putting the file back as the author shipped it: the fix is made again.</summary>
    [Fact]
    public void ReapplyRemembered_AfterTheFileCameBack_PointsItThereAgain()
    {
        var service = Service();
        service.FixSure(_folder);
        PutOriginalFile();
        Assert.Contains(OldPlace, UsedPackages());

        var reapplied = service.ReapplyRemembered(_folder);

        Assert.Equal(1, reapplied);
        Assert.Contains(NewPlace, UsedPackages());
    }

    [Fact]
    public void LeftAsItIs_IsntAskedAgain()
    {
        var service = Service();
        var issue = Assert.Single(AssetIssues(service.Check(_folder)));

        service.Apply(_folder, [(issue, ModIssueFix.KeepMod)]);

        Assert.Empty(AssetIssues(service.Check(_folder)));
        Assert.Contains(OldPlace, UsedPackages());
    }

    [Fact]
    public void GoneFromTheGame_IsOnlyListed()
    {
        _game.Clear();
        var service = Service();

        var issue = Assert.Single(AssetIssues(service.Check(_folder)));

        Assert.Equal(ModIssueKind.MissingAsset, issue.Kind);
        Assert.Null(issue.SuggestedRow);
        Assert.False(issue.IsAutomatic);
        Assert.Equal([ModIssueFix.KeepMod], issue.Choices);
        Assert.Equal(0, service.FixSure(_folder).Applied);
        Assert.Equal(File.ReadAllBytes(Path.Combine(FixtureFolder, "BP_WaxSampleCaveLight.uasset")), File.ReadAllBytes(ModFilePath));
    }

    [Fact]
    public void AnotherLibraryModHasIt_IsntAProblem()
    {
        _game.Clear();
        var other = _library.CreateBlankMod("Cave Light Base", "Me", ModTemplate.Blank).FolderName;
        var path = Path.Combine(_library.GetFolderPath(other), "BP", "Utilities", "WT", "WT_CaveLight.uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Empty(AssetIssues(Service().Check(_folder)));
    }

    [Fact]
    public void WithoutTheGamesContent_NothingIsSaid()
    {
        var service = new ModFixService(_library, DataFolder, decisionsDirectory: Path.Combine(_root, "Library_Meta", "Fix_Choices"));

        Assert.Empty(AssetIssues(service.Check(_folder)));
    }

    private sealed class FakeGame : Dictionary<string, Dictionary<string, string>>, IGameAssetCatalog
    {
        public FakeGame() : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public bool? HasPackage(string packagePath) => ContainsKey(packagePath);

        public IReadOnlyDictionary<string, string>? Objects(string packagePath) => TryGetValue(packagePath, out var objects) ? objects : null;

        public IReadOnlyList<string> PackagesNamed(string assetName) =>
            [.. Keys.Where(k => string.Equals(AssetReferenceChecker.AssetName(k), assetName, StringComparison.OrdinalIgnoreCase))];
    }
}
