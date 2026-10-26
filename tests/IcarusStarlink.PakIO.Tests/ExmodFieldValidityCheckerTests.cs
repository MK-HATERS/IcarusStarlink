using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodFieldValidityCheckerTests : IDisposable
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

    [Fact]
    public void Check_FieldNameDoesNotExistAnywhereInBaseFile_IsFlagged()
    {
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{"MaxStack":1},"Rows":[{"Name":"Stone_Pickaxe","MaxStack":1}]}""");
        var package = MakePackage(MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe",
            new() { ["MaxStackk"] = JsonValue.Create(200) }));

        var findings = ExmodFieldValidityChecker.Check(package, _dataFolder);

        var finding = Assert.Single(findings);
        Assert.Equal("MaxStackk", finding.FieldName);
        Assert.True(finding.IsUnknownField);
        Assert.Equal("MaxStack", finding.SuggestedField);
        Assert.Contains("has no field 'MaxStackk'", finding.Reason);
    }

    [Fact]
    public void Check_RowListedTwice_AFieldMisspeltOnlyInTheEarlierCopy_IsFlaggedOnce_AndAWrongKindTheLaterCopyOverrides_IsNot()
    {
        // The merge takes every copy's fields (a field in both: the later value), so the earlier
        // copy's typo reaches the game; its wrong-kind MaxStack doesn't (the later copy's wins).
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{"MaxStack":1},"Rows":[{"Name":"Stone_Pickaxe","MaxStack":1}]}""");
        var row = MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe", new() { ["MaxStackk"] = JsonValue.Create(200), ["MaxStack"] = JsonValue.Create("lots") });
        row.FileItems.Add(new ExmodFileItem { Name = "stone_pickaxe", Fields = new() { ["MaxStack"] = JsonValue.Create(5) } });

        var finding = Assert.Single(ExmodFieldValidityChecker.Check(MakePackage(row), _dataFolder));

        Assert.Equal("MaxStackk", finding.FieldName);
        Assert.True(finding.IsUnknownField);
        // The copy with the typo already has MaxStack: renaming it there would lose the typo's value.
        Assert.Null(finding.SuggestedField);
    }

    [Fact]
    public void Check_RowListedTwice_TheSameTypoInBothCopies_IsFlaggedOnce_AndTheRenameIsOfferedForTheCopyThatCounts()
    {
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{"MaxStack":1},"Rows":[{"Name":"Stone_Pickaxe","MaxStack":1}]}""");
        var row = MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe", new() { ["MaxStackk"] = JsonValue.Create(200), ["MaxStack"] = JsonValue.Create(4) });
        row.FileItems.Add(new ExmodFileItem { Name = "Stone_Pickaxe", Fields = new() { ["MaxStackk"] = JsonValue.Create(300) } });

        var finding = Assert.Single(ExmodFieldValidityChecker.Check(MakePackage(row), _dataFolder));

        Assert.Equal("MaxStack", finding.SuggestedField);
    }

    [Fact]
    public void Check_FieldNameAndValueKindMatchBaseData_IsNotFlagged()
    {
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{"MaxStack":1},"Rows":[{"Name":"Stone_Pickaxe","MaxStack":1}]}""");
        var package = MakePackage(MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe",
            new() { ["MaxStack"] = JsonValue.Create(200) }));

        var findings = ExmodFieldValidityChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_ValueKindDiffersFromBaseData_IsFlaggedAsTypeMismatch()
    {
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{"MaxStack":1},"Rows":[{"Name":"Stone_Pickaxe","MaxStack":1}]}""");
        var package = MakePackage(MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe",
            new() { ["MaxStack"] = JsonValue.Create("two hundred") }));

        var findings = ExmodFieldValidityChecker.Check(package, _dataFolder);

        var finding = Assert.Single(findings);
        Assert.Equal("MaxStack", finding.FieldName);
        Assert.Contains("a number", finding.Reason);
        Assert.Contains("text", finding.Reason);
    }

    [Fact]
    public void Check_BoolFieldSetToTheOtherBoolValue_IsNotFlagged()
    {
        // True and False are two distinct JsonValueKind values for the one conceptual boolean
        // type — a base default of false and a mod setting true must not read as a type mismatch.
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{"bAllowZeroWeight":false},"Rows":[{"Name":"Stone_Pickaxe","bAllowZeroWeight":false}]}""");
        var package = MakePackage(MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe",
            new() { ["bAllowZeroWeight"] = JsonValue.Create(true) }));

        var findings = ExmodFieldValidityChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_FieldOnlyInDefaultsNeverOnAnyRow_CountsAsReal()
    {
        // Confirmed against real extracted data (Traits/D_Itemable.json): "Behaviour" appears only
        // in Defaults, never on any actual row — a mod setting it must not be flagged as fake.
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{"Behaviour":"None"},"Rows":[{"Name":"Stone_Pickaxe"}]}""");
        var package = MakePackage(MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe",
            new() { ["Behaviour"] = JsonValue.Create("Something") }));

        var findings = ExmodFieldValidityChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_FieldOnlyOnARowNeverInDefaults_CountsAsReal()
    {
        // Confirmed against real extracted data: "Metadata" appears on some rows but has no entry
        // in Defaults at all — the reverse of the case above, must also count as a real field.
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Other_Item","Metadata":"x"},{"Name":"Stone_Pickaxe"}]}""");
        var package = MakePackage(MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe",
            new() { ["Metadata"] = JsonValue.Create("y") }));

        var findings = ExmodFieldValidityChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_EndOfModSentinelRow_IsSkippedWithoutAFalseWarning()
    {
        // A file the marker would resolve to and an unknown field, so only the skip keeps this clean.
        WriteBaseTable("EndOfMod", """{"RowStruct":"S","Defaults":{"A":1},"Rows":[]}""");
        var package = MakePackage(MakeRow("EndOfMod", "X", new() { ["Bogus"] = JsonValue.Create(1) }));

        var findings = ExmodFieldValidityChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_NoMatchingBaseFileAtAll_ReturnsEmptyInsteadOfThrowing()
    {
        var package = MakePackage(MakeRow("Traits-D_DoesNotExist.json", "Some_Item",
            new() { ["SomeField"] = JsonValue.Create(1) }));

        var findings = ExmodFieldValidityChecker.Check(package, _dataFolder);

        Assert.Empty(findings);
    }

    [Fact]
    public void Check_SharedSchemaCacheAcrossTwoMods_BothStillCheckedCorrectly()
    {
        WriteBaseTable("Traits/D_Itemable.json",
            """{"RowStruct":"S","Defaults":{"MaxStack":1},"Rows":[{"Name":"Stone_Pickaxe","MaxStack":1}]}""");
        var cache = new Dictionary<string, Dictionary<string, System.Text.Json.JsonValueKind>?>();

        var goodPackage = MakePackage(MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe",
            new() { ["MaxStack"] = JsonValue.Create(5) }));
        var badPackage = MakePackage(MakeRow("Traits-D_Itemable.json", "Stone_Pickaxe",
            new() { ["MaxStackk"] = JsonValue.Create(5) }));

        var firstResult = ExmodFieldValidityChecker.Check(goodPackage, _dataFolder, cache);
        // Gone from disk: the second check can only work from the cache.
        File.Delete(Path.Combine(_dataFolder, "Traits", "D_Itemable.json"));
        var secondResult = ExmodFieldValidityChecker.Check(badPackage, _dataFolder, cache);

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
