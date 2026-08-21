using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodBaseDifferTests : IDisposable
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly DefaultSemanticClassifier _classifier = new();

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

    [Fact]
    public void DiffAgainstBase_ChangedField_ReportsRealOriginalValue()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","RequiredMillijoules":2500}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Crafting-D_ProcessorRecipes.json",
            FileItems = [new ExmodFileItem { Name = "Stone_Pickaxe", Fields = { ["RequiredMillijoules"] = System.Text.Json.Nodes.JsonValue.Create(1313) } }],
        });

        var changes = ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier);

        var change = Assert.Single(changes);
        Assert.Equal("Stone_Pickaxe", change.ItemName);
        Assert.Equal("RequiredMillijoules", change.FieldName);
        Assert.Equal(2500, (int)change.OriginalValue!);
        Assert.Equal(1313, (int)change.NewValue!);
    }

    [Fact]
    public void DiffAgainstBase_FieldEqualToBase_ProducesNoChange()
    {
        // TableDiffer.Diff (Phase 1) only ever reports a genuine difference — a field the mod
        // "changed" back to exactly its base value produces no FieldChange at all. Real, useful
        // for the editor design: it means the editor's own Fields list has to come from the
        // package's own item.Fields directly (unconditionally), not solely from this diff's
        // output, which silently omits anything currently equal to base.
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","RequiredMillijoules":2500}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Crafting-D_ProcessorRecipes.json",
            FileItems = [new ExmodFileItem { Name = "Stone_Pickaxe", Fields = { ["RequiredMillijoules"] = System.Text.Json.Nodes.JsonValue.Create(2500) } }],
        });

        var changes = ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier);

        Assert.Empty(changes);
    }

    [Fact]
    public void DiffAgainstBase_MissingBaseFile_AddsWarningInsteadOfThrowing()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "NoSuchCategory-D_Missing.json",
            FileItems = [new ExmodFileItem { Name = "X", Fields = { ["Y"] = System.Text.Json.Nodes.JsonValue.Create(1) } }],
        });
        var report = new MergeReport();

        var changes = ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier, report);

        Assert.Empty(changes);
        Assert.Contains(report.Warnings, w => w.Contains("NoSuchCategory-D_Missing.json"));
    }

    [Fact]
    public void DiffAgainstBase_ItemNotInBase_IsNewItem()
    {
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "New_Item", Fields = { ["SomeField"] = System.Text.Json.Nodes.JsonValue.Create(5) } }],
        });

        var changes = ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier);

        var change = Assert.Single(changes);
        Assert.True(change.IsNewItem);
        Assert.Null(change.OriginalValue);
    }

    [Fact]
    public void DiffAgainstBase_MultipleRows_AllDiffed()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","RequiredMillijoules":2500}]}""");
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Wood","Weight":150}]}""");
        var package = MakePackage(
            new ExmodFileRow
            {
                CurrentFile = "Crafting-D_ProcessorRecipes.json",
                FileItems = [new ExmodFileItem { Name = "Stone_Pickaxe", Fields = { ["RequiredMillijoules"] = System.Text.Json.Nodes.JsonValue.Create(1000) } }],
            },
            new ExmodFileRow
            {
                CurrentFile = "Traits-D_Fuel.json",
                FileItems = [new ExmodFileItem { Name = "Item_Wood", Fields = { ["Weight"] = System.Text.Json.Nodes.JsonValue.Create(0) } }],
            });

        var changes = ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier);

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.FieldName == "RequiredMillijoules" && (int)c.OriginalValue! == 2500);
        Assert.Contains(changes, c => c.FieldName == "Weight" && (int)c.OriginalValue! == 150);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }
}
