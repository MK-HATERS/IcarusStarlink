using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Storage.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// Every table fix the fix window offers, made through the fix service on a real Library folder:
/// written (backed up first), gone on the next check, and the sure ones made by Fix sure problems
/// and remembered. Renaming rows, removing old rows and repointing files are covered with their
/// own evidence (row history, cooked files) in ModHealthCheckerTests and ModFixServiceAssetTests.
/// </summary>
public sealed class ModFixServiceFixesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ModFixes_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;
    private readonly string _folder;

    public ModFixServiceFixesTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups"), NullLogger<FolderLibraryRepository>.Instance);
        Table("Items/D_ItemsStatic.json", """
            {"Name": "Wood_Floor", "Weight": 100, "Manual_Tags": {"GameplayTags": [{"TagName": "A"}, {"TagName": "B"}]}},
            {"Name": "Bread", "Weight": 20}
            """);
        Table("Traits/D_Itemable.json", """{"Name": "Item_Fiber", "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_Fiber-DisplayName\", \"Fiber\")"}""");

        _folder = _library.CreateBlankMod("Fix Me", "Me", ModTemplate.Blank).FolderName;
        ExmodFolder.Write(_library.GetFolderPath(_folder), new ExmodPackageContents(ExmodJson.Parse("""
            {
                "name": "Fix Me", "author": "Me", "version": "1", "description": "D", "fileName": "Fix_Me",
                "Rows": [
                    {"CurrentFile": "Items-D_ItemsStatic.json", "File_Items": [
                        {"Name": "Bread", "weight": 5, "Wieght": 6},
                        {"Name": "Wood_Floor", "Weight": 100, "Manual_Tags": {"GameplayTags": [{"TagName": "A"}, {"TagName": "C"}]}}
                    ]},
                    {"CurrentFile": "D_Itemable.json", "File_Items": [
                        {"Name": "Item_Rope", "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_Fiber-DisplayName\", \"Rope\")"}
                    ]}
                ]
            }
            """), []));
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

    private ModFixService Service() => new(_library, DataFolder, decisionsDirectory: Path.Combine(_root, "Library_Meta", "Fix_Choices"));

    private void Table(string relative, string rows)
    {
        var path = Path.Combine(DataFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$$"""{"RowStruct": "S", "Defaults": {"Weight": 0}, "Rows": [{{{rows}}}]}""");
    }

    private ExmodPackage Mod() => ExmodFolder.ReadPackageOnly(_library.GetFolderPath(_folder));

    [Fact]
    public void EveryTableFix_IsWrittenAndGoneOnTheNextCheck()
    {
        var service = Service();
        var before = service.Check(_folder);
        // The text key is found even in the misnamed table (its key names the game's table).
        Assert.Equal(
            [ModIssueKind.SameAsGame, ModIssueKind.MixedList, ModIssueKind.UnknownTable, ModIssueKind.UnknownField, ModIssueKind.UnknownField, ModIssueKind.SharedTextKey],
            before.Issues.Select(i => i.Kind).Order());

        // All at once, as the fix window's Apply does: the field fixes go before the table moves.
        var applied = service.Apply(_folder, [.. before.Issues.Select(i => (i, i.FieldName == "Wieght" ? ModIssueFix.UseGame : i.DefaultFix))]);

        Assert.Equal(6, applied);
        Assert.True(_library.HasModBackup(_folder));
        Assert.Empty(service.Check(_folder).Issues);

        var mod = Mod();
        var items = mod.Rows.Single(r => r.CurrentFile == "Items-D_ItemsStatic.json").FileItems;
        var bread = items.Single(i => i.Name == "Bread");
        Assert.Equal(["Weight"], bread.Fields.Keys);
        Assert.Equal(5, bread.Fields["Weight"]!.GetValue<int>());
        Assert.Equal(["A", "B", "C"], items.Single(i => i.Name == "Wood_Floor").Fields["Manual_Tags"]!["GameplayTags"]!.AsArray().Select(t => t!["TagName"]!.GetValue<string>()));
        Assert.False(items.Single(i => i.Name == "Wood_Floor").Fields.ContainsKey("Weight"));
        var rope = mod.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems.Single().Fields["DisplayName"]!.GetValue<string>();
        Assert.True(NsLocText.TryParse(rope, out _, out var key, out var text));
        Assert.Equal(("Item_Rope-DisplayName", "Rope"), (key, text));
    }

    [Fact]
    public void FixSureProblems_MakesOnlyTheSureOnes_AndRemembersThem()
    {
        var service = Service();

        var (applied, toReview) = service.FixSure(_folder);

        // Sure: the table moved, the field's case matched. Your call: the typo, the mixed list,
        // and the text key the move brought to light. Tidying (a value equal to the game's) is
        // never made by itself.
        Assert.Equal(2, applied);
        Assert.Equal(3, toReview);
        var report = service.Check(_folder);
        Assert.DoesNotContain(report.Issues, i => i.Kind == ModIssueKind.UnknownTable);
        Assert.DoesNotContain(report.Issues, i => i.Kind == ModIssueKind.UnknownField && i.FieldName == "weight");
        Assert.Contains(report.Issues, i => i.Kind == ModIssueKind.SameAsGame);
        Assert.Equal(2, service.RememberedCount(_folder));
    }
}
