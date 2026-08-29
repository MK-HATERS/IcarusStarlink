using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodReferenceCheckerTests : IDisposable
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private void WriteBaseTable(string relativePath, string json)
    {
        var path = Path.Combine(_dataFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static ExmodPackage MakePackage(params ExmodFileRow[] rows) => new()
    {
        Name = "Test Mod", Author = "A", Version = "1", Description = "D", FileName = "Test_Mod", Rows = [.. rows],
    };

    private static ExmodFileRow MakeRow(string currentFile, string itemName, Dictionary<string, JsonNode?> fields) => new()
    {
        CurrentFile = currentFile,
        FileItems = [new ExmodFileItem { Name = itemName, Fields = fields }],
    };

    private static JsonNode MakeReference(string rowName, string tableName) =>
        new JsonObject { ["RowName"] = JsonValue.Create(rowName), ["DataTableName"] = JsonValue.Create(tableName) };

    [Fact]
    public void Check_ReferenceTargetsARealExistingRow_IsNotFlagged()
    {
        WriteBaseTable("Items/D_ItemsStatic.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Fiber"}]}""");
        var package = MakePackage(MakeRow("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["Inputs"] = new JsonArray(new JsonObject { ["Element"] = MakeReference("Fiber", "D_ItemsStatic"), ["Count"] = JsonValue.Create(10) }) }));

        var findings = ExmodReferenceChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_ReferenceTargetsARowThatDoesNotExist_IsFlagged()
    {
        WriteBaseTable("Items/D_ItemsStatic.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Fiber"}]}""");
        var package = MakePackage(MakeRow("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["Inputs"] = new JsonArray(new JsonObject { ["Element"] = MakeReference("Fibre", "D_ItemsStatic"), ["Count"] = JsonValue.Create(10) }) }));

        var findings = ExmodReferenceChecker.Check(package, _dataFolder);

        var finding = Assert.Single(findings);
        Assert.Contains("Fibre", finding.Reason);
        Assert.Contains("no such row exists", finding.Reason);
    }

    [Fact]
    public void Check_ReferenceTargetsATableThatDoesNotExist_IsFlaggedDifferentlyFromAMissingRow()
    {
        var package = MakePackage(MakeRow("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["Inputs"] = new JsonArray(new JsonObject { ["Element"] = MakeReference("Fiber", "D_DoesNotExist"), ["Count"] = JsonValue.Create(10) }) }));

        var findings = ExmodReferenceChecker.Check(package, _dataFolder);

        var finding = Assert.Single(findings);
        Assert.Contains("D_DoesNotExist", finding.Reason);
        Assert.Contains("doesn't exist", finding.Reason);
    }

    [Fact]
    public void Check_RowNameIsNoneSentinel_IsSkippedNotFlagged()
    {
        // Real, pervasive false-positive source found live against the user's own 49-mod library
        // (354 findings across 20 mods before this fix): "None" is Unreal's own FName::NAME_None
        // serialization for "no row selected" on an optional reference field, not a real row name.
        var package = MakePackage(MakeRow("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["ModifierState"] = MakeReference("None", "D_ModifierStates") }));

        var findings = ExmodReferenceChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_ReferenceTargetsAnItemTheSameModAlsoDeclares_IsNotFlagged()
    {
        // The dominant real false-positive source found live: a mod adding a new craftable item
        // almost always declares BOTH the recipe (Crafting-D_ProcessorRecipes) AND the item it
        // outputs (Items-D_ItemTemplate) in the same package — the recipe's own reference to that
        // self-declared item is real, working content once the mod is merged, not broken, even
        // though the base game's own data has no such row.
        var package = MakePackage(
            MakeRow("Crafting-D_ProcessorRecipes.json", "My_New_Gadget",
                new() { ["Outputs"] = new JsonArray(new JsonObject { ["Element"] = MakeReference("My_New_Gadget", "D_ItemTemplate") }) }),
            MakeRow("Items-D_ItemTemplate.json", "My_New_Gadget", new()));

        var findings = ExmodReferenceChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_BareRowNameWithNoDataTableName_IsSkippedNotGuessed()
    {
        // "Requirement"/"Audio"-style references in real recipe data carry a bare RowName with no
        // DataTableName at all — this checker has no reliable way to know their implicit target,
        // so it must not guess.
        var package = MakePackage(MakeRow("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new() { ["Requirement"] = new JsonObject { ["RowName"] = JsonValue.Create("Stone_Pickaxe") } }));

        var findings = ExmodReferenceChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_ReferenceNestedInsideAnArrayOfObjects_IsStillFound()
    {
        WriteBaseTable("Items/D_ItemsStatic.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(MakeRow("Crafting-D_ProcessorRecipes.json", "Stone_Pickaxe",
            new()
            {
                ["Outputs"] = new JsonArray(
                    new JsonObject { ["Element"] = MakeReference("Ghost_Item", "D_ItemsStatic"), ["Count"] = JsonValue.Create(1) }),
            }));

        var findings = ExmodReferenceChecker.Check(package, _dataFolder);

        var finding = Assert.Single(findings);
        Assert.Equal("Outputs[0].Element", finding.FieldPath);
    }

    [Fact]
    public void Check_EndOfModSentinelRow_IsSkippedWithoutAFalseWarning()
    {
        var package = MakePackage(new ExmodFileRow { CurrentFile = "EndOfMod", FileItems = [] });

        var findings = ExmodReferenceChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_SharedIndexAcrossTwoMods_BothStillCheckedCorrectly()
    {
        WriteBaseTable("Items/D_ItemsStatic.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Fiber"}]}""");
        var index = DataTableRowIndex.Build(_dataFolder);

        var goodPackage = MakePackage(MakeRow("Crafting-D_ProcessorRecipes.json", "A",
            new() { ["Element"] = MakeReference("Fiber", "D_ItemsStatic") }));
        var badPackage = MakePackage(MakeRow("Crafting-D_ProcessorRecipes.json", "B",
            new() { ["Element"] = MakeReference("Ghost", "D_ItemsStatic") }));

        var firstResult = ExmodReferenceChecker.Check(goodPackage, _dataFolder, index);
        var secondResult = ExmodReferenceChecker.Check(badPackage, _dataFolder, index);

        Assert.Empty(firstResult);
        Assert.Single(secondResult);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }
}
