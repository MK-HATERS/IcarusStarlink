using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>"Scale a field across a game table" — the rules behind the editor's Scale tool and the Quality of life template, shaped on what a real QoL mod (Deyvid's All-in-One, Nexus 317) does.</summary>
public sealed class ScaleRulesTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ScaleRules_" + Guid.NewGuid().ToString("N"));

    public ScaleRulesTests()
    {
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":100,"MaxStack":1},"Rows":[
              {"Name":"Item_Fiber","Weight":10,"MaxStack":100},
              {"Name":"Item_Stone","Weight":300,"MaxStack":20},
              {"Name":"Item_Inherits"}
            ]}
            """);
        Write("Traits/D_Armour.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Leather_Chest","ArmourStats":{"(Value=\"BaseColdResistance_%\")":6,"(Value=\"BaseHeatResistance_%\")":4},"ArmourType":"Chest"}
            ]}
            """);
        Write("MetaWorkshop/D_WorkshopItems.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Meta_Pick","ReplicationCost":[{"Meta":{"RowName":"Credits"},"Amount":100},{"Meta":{"RowName":"Exotic"},"Amount":1}]}
            ]}
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private void Write(string relative, string json)
    {
        var path = Path.Combine(_data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static ScaleRule Rule(string file, string field, double value, ScaleOperation op = ScaleOperation.Multiply, string? filter = null) =>
        new() { CurrentFile = file, Field = field, Value = value, Operation = op, RowFilter = filter };

    private static ExmodPackage EmptyMod() => new()
    {
        Name = "QoL", Author = "Me", Version = "1.0", Description = "", FileName = "QoL",
        Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }],
    };

    private static ExmodFileItem Item(ExmodPackage mod, string file, string name) =>
        mod.Rows.Single(r => r.CurrentFile == file).FileItems.Single(i => i.Name == name);

    [Fact]
    public void Multiply_ScalesEveryRow_IncludingOnesThatInheritTheTableDefault()
    {
        var mod = EmptyMod();

        var result = ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Itemable.json", "MaxStack", 10)], _data, new MergeReport());

        Assert.Equal(3, result.RowsWritten);
        Assert.Equal(1000, Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["MaxStack"]!.GetValue<long>());
        Assert.Equal(10, Item(mod, "Traits-D_Itemable.json", "Item_Inherits").Fields["MaxStack"]!.GetValue<long>());
        Assert.Equal("EndOfMod", mod.Rows[^1].CurrentFile);
        Assert.Single(mod.ScaleRules);
    }

    [Fact]
    public void IncludeInheritedOff_LeavesRowsThatUseTheTableDefault()
    {
        var mod = EmptyMod();
        var stacks = Rule("Traits-D_Itemable.json", "MaxStack", 10);
        stacks.IncludeInherited = false;

        ScaleRules.ApplyToPackage(mod, [stacks], _data, new MergeReport());

        Assert.DoesNotContain(mod.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems, i => i.Name == "Item_Inherits");
        Assert.False(ExmodJson.Parse(ExmodJson.Serialize(mod)).ScaleRules[0].IncludeInherited);
    }

    [Fact]
    public void DictionaryWildcard_ScalesEveryStat_AndKeepsTheRestOfTheField()
    {
        var mod = EmptyMod();

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Armour.json", "ArmourStats.*", 1.5)], _data, new MergeReport());

        var stats = Item(mod, "Traits-D_Armour.json", "Leather_Chest").Fields["ArmourStats"]!.AsObject();
        Assert.Equal(9, stats["(Value=\"BaseColdResistance_%\")"]!.GetValue<long>());
        Assert.Equal(6, stats["(Value=\"BaseHeatResistance_%\")"]!.GetValue<long>());
        Assert.False(Item(mod, "Traits-D_Armour.json", "Leather_Chest").Fields.ContainsKey("ArmourType"));
    }

    [Fact]
    public void OneDictionaryKey_ScalesOnlyThatStat()
    {
        var mod = EmptyMod();

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Armour.json", "ArmourStats.(Value=\"BaseColdResistance_%\")", 2)], _data, new MergeReport());

        var stats = Item(mod, "Traits-D_Armour.json", "Leather_Chest").Fields["ArmourStats"]!.AsObject();
        Assert.Equal(12, stats["(Value=\"BaseColdResistance_%\")"]!.GetValue<long>());
        Assert.Equal(4, stats["(Value=\"BaseHeatResistance_%\")"]!.GetValue<long>());
    }

    [Fact]
    public void ListField_ScalesEachElement_AndNeverRoundsARealCostToNothing()
    {
        var mod = EmptyMod();

        ScaleRules.ApplyToPackage(mod, [Rule("MetaWorkshop-D_WorkshopItems.json", "ReplicationCost[].Amount", 0.4)], _data, new MergeReport());

        var cost = Item(mod, "MetaWorkshop-D_WorkshopItems.json", "Meta_Pick").Fields["ReplicationCost"]!.AsArray();
        Assert.Equal(40, cost[0]!["Amount"]!.GetValue<long>());
        Assert.Equal(1, cost[1]!["Amount"]!.GetValue<long>()); // 1 × 0.4 stays 1
        Assert.Equal("Credits", cost[0]!["Meta"]!["RowName"]!.GetValue<string>());
    }

    [Fact]
    public void Set_RowFilter_AndZero()
    {
        var mod = EmptyMod();

        ScaleRules.ApplyToPackage(mod,
            [Rule("Traits-D_Itemable.json", "Weight", 0, filter: "stone"), Rule("Traits-D_Itemable.json", "MaxStack", 50, ScaleOperation.Set, filter: "fiber")],
            _data, new MergeReport());

        Assert.Equal(0, Item(mod, "Traits-D_Itemable.json", "Item_Stone").Fields["Weight"]!.GetValue<long>());
        Assert.Equal(50, Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["MaxStack"]!.GetValue<long>());
        Assert.DoesNotContain(mod.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems, i => i.Name == "Item_Inherits");
    }

    /// <summary>Changing or turning off a rule must not leave its old values behind; hand edits to other fields stay.</summary>
    [Fact]
    public void Reapply_ReplacesTheRulesOwnFields_AndKeepsHandEdits()
    {
        var mod = EmptyMod();
        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Itemable.json", "MaxStack", 10)], _data, new MergeReport());
        Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["DisplayName"] = "Hand edited";

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Itemable.json", "MaxStack", 2)], _data, new MergeReport());
        Assert.Equal(200, Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["MaxStack"]!.GetValue<long>());

        var off = Rule("Traits-D_Itemable.json", "MaxStack", 2);
        off.Enabled = false;
        ScaleRules.ApplyToPackage(mod, [off], _data, new MergeReport());

        var row = mod.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json");
        var fiber = Assert.Single(row.FileItems);
        Assert.Equal("Item_Fiber", fiber.Name);
        Assert.False(fiber.Fields.ContainsKey("MaxStack"));
        Assert.Equal("Hand edited", fiber.Fields["DisplayName"]!.GetValue<string>());
    }

    [Fact]
    public void RulesOnTheSameField_Compound()
    {
        var mod = EmptyMod();

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Itemable.json", "Weight", 0.5), Rule("Traits-D_Itemable.json", "Weight", 0.5)], _data, new MergeReport());

        Assert.Equal(75, Item(mod, "Traits-D_Itemable.json", "Item_Stone").Fields["Weight"]!.GetValue<long>());
    }

    [Fact]
    public void Preview_CountsRowsAndGivesAnExample()
    {
        var preview = ScaleRules.Preview(Rule("Traits-D_Itemable.json", "Weight", 0.25), _data);

        Assert.Equal(3, preview.RowCount);
        Assert.Equal("Item_Fiber: 10 → 3", preview.Example); // 2.5 rounds away from zero
    }

    [Fact]
    public void Rules_RoundTripThroughTheExmodFile()
    {
        var mod = EmptyMod();
        ScaleRules.ApplyToPackage(mod, [new ScaleRule { Label = "Stacks", CurrentFile = "Traits-D_Itemable.json", Field = "MaxStack", Value = 10, Enabled = false }], _data, new MergeReport());

        var reread = ExmodJson.Parse(ExmodJson.Serialize(mod));

        var rule = Assert.Single(reread.ScaleRules);
        Assert.Equal("Stacks", rule.Label);
        Assert.Equal(10, rule.Value);
        Assert.False(rule.Enabled);
        Assert.Equal(ScaleOperation.Multiply, rule.Operation);
    }

    [Fact]
    public void ListNumericFields_OffersPlainDictionaryAndListFields()
    {
        Assert.Equal(["MaxStack", "Weight"], ScaleRules.ListNumericFields(_data, "Traits-D_Itemable.json"));
        Assert.Contains("ArmourStats.*", ScaleRules.ListNumericFields(_data, "Traits-D_Armour.json"));
        Assert.Contains("ArmourStats.(Value=\"BaseColdResistance_%\")", ScaleRules.ListNumericFields(_data, "Traits-D_Armour.json"));
        Assert.Contains("ReplicationCost[].Amount", ScaleRules.ListNumericFields(_data, "MetaWorkshop-D_WorkshopItems.json"));
        Assert.Contains("Traits-D_Itemable.json", ScaleRules.ListTables(_data));
    }

    [Theory]
    [InlineData("MaxStack", new[] { "MaxStack" })]
    [InlineData("ArmourStats.(Value=\"Base.Odd_%\")", new[] { "ArmourStats", "(Value=\"Base.Odd_%\")" })]
    [InlineData("Inputs[].Count", new[] { "Inputs[]", "Count" })]
    public void SplitPath_IgnoresDotsInsideAStatKey(string path, string[] expected) =>
        Assert.Equal(expected, ScaleRules.SplitPath(path));

    /// <summary>A review finding: Apply took the rule's field off the mod's OWN new items too (a wizard item's own Itemable lost its Weight for good).</summary>
    [Fact]
    public void Apply_LeavesRowsTheModAddsItselfAlone_AndItemsTheAuthorLeftEmpty()
    {
        var mod = EmptyMod();
        mod.Rows.Insert(0, new ExmodFileRow
        {
            CurrentFile = "Traits-D_Itemable.json",
            FileItems =
            [
                new ExmodFileItem { Name = "Item_My_New_Thing", Fields = { ["Weight"] = 42 } },
                new ExmodFileItem { Name = "Item_Placeholder" },
            ],
        });

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Itemable.json", "Weight", 0.5)], _data, new MergeReport());

        Assert.Equal(42, Item(mod, "Traits-D_Itemable.json", "Item_My_New_Thing").Fields["Weight"]!.GetValue<int>());
        Assert.Empty(Item(mod, "Traits-D_Itemable.json", "Item_Placeholder").Fields);
        Assert.Equal(5, Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["Weight"]!.GetValue<long>());
    }

    [Fact]
    public void Apply_AHugeFactor_StaysAWholeNumberTheGameCanHold()
    {
        var mod = EmptyMod();

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Itemable.json", "MaxStack", 1e30)], _data, new MergeReport());

        Assert.Equal(int.MaxValue, Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["MaxStack"]!.GetValue<long>());
    }

    /// <summary>Weight 10 × 0.5 × 0.3 = 1.5: the first rule's 5.0 must not count as a whole-number field and round the second.</summary>
    [Fact]
    public void ChainedRules_OnAFractionalResult_DoNotRoundIt()
    {
        Write("Traits/D_Decayable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Food","SpoilTime":1.5}]}""");
        var mod = EmptyMod();

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Decayable.json", "SpoilTime", 2), Rule("Traits-D_Decayable.json", "SpoilTime", 0.5)], _data, new MergeReport());

        Assert.Equal(1.5, Item(mod, "Traits-D_Decayable.json", "Food").Fields["SpoilTime"]!.GetValue<double>());
    }
}
