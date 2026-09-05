using System.Text.Json.Nodes;
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

    [Fact]
    public void DiffAgainstBase_SameFileTwiceWithSharedCache_SecondCallReusesCachedTable()
    {
        // Proves the cache is actually consulted, not just accepted and ignored: after the first
        // call populates it for this CurrentFile, the base file is deleted from disk — a second
        // call that still produces the same real diff (instead of a "no matching base file"
        // warning) could only have come from the cache, not a fresh read.
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","RequiredMillijoules":2500}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Crafting-D_ProcessorRecipes.json",
            FileItems = [new ExmodFileItem { Name = "Stone_Pickaxe", Fields = { ["RequiredMillijoules"] = System.Text.Json.Nodes.JsonValue.Create(1313) } }],
        });
        var cache = new Dictionary<string, System.Text.Json.Nodes.JsonObject?>();

        var first = ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier, baseTableCache: cache);
        File.Delete(Path.Combine(_dataFolder, "Crafting", "D_ProcessorRecipes.json"));
        var second = ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier, baseTableCache: cache);

        Assert.Equal(2500, (int)Assert.Single(first).OriginalValue!);
        Assert.Equal(2500, (int)Assert.Single(second).OriginalValue!);
    }

    [Fact]
    public void DiffAgainstBase_MissingBaseFileWithSharedCache_StillWarnsOnEveryCall()
    {
        // A cache hit for a genuinely-missing file must still re-warn each call — that warning is
        // a per-mod signal (which mod is affected), not something the cache itself should suppress.
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "NoSuchCategory-D_Missing.json",
            FileItems = [new ExmodFileItem { Name = "X", Fields = { ["Y"] = System.Text.Json.Nodes.JsonValue.Create(1) } }],
        });
        var cache = new Dictionary<string, System.Text.Json.Nodes.JsonObject?>();
        var firstReport = new MergeReport();
        var secondReport = new MergeReport();

        ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier, firstReport, cache);
        ExmodBaseDiffer.DiffAgainstBase(package, _dataFolder, _classifier, secondReport, cache);

        Assert.Contains(firstReport.Warnings, w => w.Contains("NoSuchCategory-D_Missing.json"));
        Assert.Contains(secondReport.Warnings, w => w.Contains("NoSuchCategory-D_Missing.json"));
    }

    [Fact]
    public void StripFieldsIdenticalToBase_FieldEqualToBase_IsStripped()
    {
        WriteBaseTable("Crafting/D_ProcessorRecipes.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","RequiredMillijoules":2500,"Weight":10}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Crafting-D_ProcessorRecipes.json",
            FileItems =
            [
                new ExmodFileItem
                {
                    Name = "Stone_Pickaxe",
                    // RequiredMillijoules matches base exactly (e.g. left over from "Add item from
                    // game data" and never actually touched); Weight was genuinely changed.
                    Fields = { ["RequiredMillijoules"] = JsonValue.Create(2500), ["Weight"] = JsonValue.Create(5) },
                },
            ],
        });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        var item = Assert.Single(Assert.Single(result).FileItems);
        Assert.False(item.Fields.ContainsKey("RequiredMillijoules"));
        Assert.Equal(5, (int)item.Fields["Weight"]!);
    }

    /// <summary>
    /// The critical regression this whole feature must never introduce: AddItem + AddField is a
    /// real, intentional editor workflow for hand-authoring a deliberately sparse item — e.g. typing
    /// an item name that happens to match a real base row, then adding just ONE field, exactly the
    /// classic hand-written EXMOD convention (only list what changed). Stripping must never invent a
    /// "removed" entry for every OTHER field the real base row has that this sparse item simply
    /// never mentions — that would silently turn "bump MaxStack on Item_Sword" into "delete every
    /// other field of Item_Sword" the instant the mod is saved.
    /// </summary>
    [Fact]
    public void StripFieldsIdenticalToBase_SparseHandAuthoredItem_NeverInventsRemovalsForFieldsItNeverMentioned()
    {
        WriteBaseTable("Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Sword","MaxStack":1,"Weight":500,"Icon":"/Some/Icon"}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            // Deliberately sparse — never touched Weight/Icon at all, not even via a full-row copy.
            FileItems = [new ExmodFileItem { Name = "Item_Sword", Fields = { ["MaxStack"] = JsonValue.Create(200) } }],
        });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        var item = Assert.Single(Assert.Single(result).FileItems);
        Assert.Equal(200, (int)item.Fields["MaxStack"]!);
        Assert.False(item.Fields.ContainsKey("Weight"));
        Assert.False(item.Fields.ContainsKey("Icon"));
        Assert.Single(item.Fields);
    }

    [Fact]
    public void StripFieldsIdenticalToBase_ItemNotInBase_KeepsEveryFieldUnconditionally()
    {
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "New_Item", Fields = { ["SomeField"] = JsonValue.Create(5) } }],
        });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        var item = Assert.Single(Assert.Single(result).FileItems);
        Assert.Equal(5, (int)item.Fields["SomeField"]!);
    }

    [Fact]
    public void StripFieldsIdenticalToBase_NoMatchingBaseFile_RowKeptCompletelyAsIs()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "NoSuchCategory-D_Missing.json",
            FileItems = [new ExmodFileItem { Name = "X", Fields = { ["Y"] = JsonValue.Create(1) } }],
        });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        var item = Assert.Single(Assert.Single(result).FileItems);
        Assert.Equal(1, (int)item.Fields["Y"]!);
    }

    [Fact]
    public void StripFieldsIdenticalToBase_EndOfModMarker_KeptCompletelyAsIs()
    {
        var package = MakePackage(new ExmodFileRow { CurrentFile = "EndOfMod", FileItems = [] });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        Assert.Single(result);
        Assert.Equal("EndOfMod", result[0].CurrentFile);
    }

    /// <summary>"Insert file at location" adds a row with no items yet, meant to be filled in later — it must survive a Save (and thus this strip pass) even though it currently has nothing to strip or keep.</summary>
    [Fact]
    public void StripFieldsIdenticalToBase_EmptyPlaceholderRow_IsPreservedNotDropped()
    {
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow { CurrentFile = "Traits-D_Fuel.json", FileItems = [] });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        var row = Assert.Single(result);
        Assert.Equal("Traits-D_Fuel.json", row.CurrentFile);
        Assert.Empty(row.FileItems);
    }

    /// <summary>"Add item" adds an item with no fields yet (before "Add field" is used) — it must survive a Save the same way an empty placeholder row does, and so must an item whose every field ended up stripped (e.g. copied wholesale from game data and never actually edited).</summary>
    [Fact]
    public void StripFieldsIdenticalToBase_ItemWithNoRemainingFields_IsPreservedNotDropped()
    {
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Wood","Weight":150}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems =
            [
                new ExmodFileItem { Name = "Item_Wood", Fields = { ["Weight"] = JsonValue.Create(150) } }, // fully redundant copy
                new ExmodFileItem { Name = "Freshly_Added" }, // AddItem with no AddField yet
            ],
        });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        var row = Assert.Single(result);
        Assert.Equal(2, row.FileItems.Count);
        Assert.Empty(row.FileItems.Single(i => i.Name == "Item_Wood").Fields);
        Assert.Empty(row.FileItems.Single(i => i.Name == "Freshly_Added").Fields);
    }

    [Fact]
    public void StripFieldsIdenticalToBase_ExplicitRemovalOfARealBaseField_IsKept()
    {
        // An explicit JSON null on a field base DOES have is a genuine removal — never strippable,
        // since it's a real, intentional change (present-in-base -> absent-in-modded), not a no-op.
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Wood","Weight":150}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Item_Wood", Fields = { ["Weight"] = null } }],
        });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        var item = Assert.Single(Assert.Single(result).FileItems);
        Assert.True(item.Fields.ContainsKey("Weight"));
        Assert.Null(item.Fields["Weight"]);
    }

    [Fact]
    public void StripFieldsIdenticalToBase_ExplicitRemovalOfAFieldBaseNeverHad_IsStrippedAsANoOp()
    {
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Wood","Weight":150}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Item_Wood", Fields = { ["FieldBaseNeverHad"] = null } }],
        });

        var result = ExmodBaseDiffer.StripFieldsIdenticalToBase(package, _dataFolder);

        Assert.Empty(Assert.Single(result).FileItems.Single().Fields);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }
}
