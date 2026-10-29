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
              {"Name":"Leather_Chest","ArmourStats":{"(Value=\"BaseColdResistance_%\")":6,"(Value=\"BaseHeatResistance_%\")":4},"ArmourType":"Chest"},
              {"Name":"Plate_Chest","ArmourStats":{"(Value=\"BaseColdResistance_%\")":2},"ArmourType":"Chest"}
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

    /// <summary>Whole-or-not is per field: Rate 3 on one row is still a real number when another row has 0.5, so 3 × 0.25 is 0.75, not rounded to 1.</summary>
    [Fact]
    public void Multiply_FieldWithFractionsElsewhere_KeepsTheResultReal()
    {
        Write("Traits/D_Fillable.json", """
            {"RowStruct":"S","Defaults":{"Rate":1},"Rows":[
              {"Name":"Canteen","Rate":3},
              {"Name":"Bladder","Rate":0.5},
              {"Name":"Barrel","Count":3}
            ]}
            """);
        var mod = EmptyMod();

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Fillable.json", "Rate", 0.25), Rule("Traits-D_Fillable.json", "Count", 0.25)], _data, new MergeReport());

        Assert.Equal(0.75, Item(mod, "Traits-D_Fillable.json", "Canteen").Fields["Rate"]!.GetValue<double>());
        // A field whole everywhere stays whole (and never rounds a real amount to 0).
        Assert.Equal(1, Item(mod, "Traits-D_Fillable.json", "Barrel").Fields["Count"]!.GetValue<long>());
    }

    [Fact]
    public void IsDecimalField_LooksAtEveryRowAndTheDefault_ThroughListsAndDictionaries()
    {
        var game = new GameTableIndex(_data);

        Assert.False(game.IsDecimalField("Traits-D_Itemable.json", "Weight"));
        Assert.False(game.IsDecimalField("Traits-D_Armour.json", "ArmourStats.*"));
        Assert.False(game.IsDecimalField("MetaWorkshop-D_WorkshopItems.json", "ReplicationCost[].Amount"));
        Assert.Null(game.IsDecimalField("Traits-D_Itemable.json", "NoSuchField"));

        Write("Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{"Weight":0.5},"Rows":[{"Name":"Item_Fiber","Weight":10}]}""");
        Assert.True(new GameTableIndex(_data).IsDecimalField("Traits-D_Itemable.json", "Weight"));
    }

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

    /// <summary>The mod's own "item_fiber" is the game's Item_Fiber (row names ignore case): the rule's value joins it, not a second item for the same row.</summary>
    [Fact]
    public void AModItemSpelledInAnotherCase_GetsTheValue_InsteadOfASecondItem()
    {
        var mod = EmptyMod();
        mod.Rows.Insert(0, new ExmodFileRow { CurrentFile = "Traits-D_Itemable.json", FileItems = [new ExmodFileItem { Name = "item_fiber", Fields = new() { ["Weight"] = 5 } }] });

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Itemable.json", "MaxStack", 10)], _data, new MergeReport());

        var fiber = Assert.Single(mod.Rows.Single(r => r.CurrentFile == "Traits-D_Itemable.json").FileItems, i => string.Equals(i.Name, "Item_Fiber", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(5, fiber.Fields["Weight"]!.GetValue<int>());
        Assert.Equal(1000, fiber.Fields["MaxStack"]!.GetValue<long>());
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

    /// <summary>
    /// A hand edit of a rule's field on a row the rule doesn't cover is the author's: it used to be
    /// stripped from every game row of the table on each Apply (and on Save, which applies).
    /// </summary>
    [Fact]
    public void Reapply_KeepsAHandEditOfTheSameField_OnARowTheRuleDoesNotCover()
    {
        var mod = EmptyMod();
        var plate = Rule("Traits-D_Armour.json", "ArmourStats.*", 2, filter: "Plate");
        ScaleRules.ApplyToPackage(mod, [plate], _data, new MergeReport());
        mod.Rows.Single(r => r.CurrentFile == "Traits-D_Armour.json").FileItems.Add(new ExmodFileItem
        {
            Name = "Leather_Chest",
            Fields = { ["ArmourStats"] = new System.Text.Json.Nodes.JsonObject { ["(Value=\"BaseColdResistance_%\")"] = 99 } },
        });

        ScaleRules.ApplyToPackage(mod, [plate], _data, new MergeReport());

        Assert.Equal(99, Item(mod, "Traits-D_Armour.json", "Leather_Chest").Fields["ArmourStats"]!["(Value=\"BaseColdResistance_%\")"]!.GetValue<int>());
        Assert.Equal(4, Item(mod, "Traits-D_Armour.json", "Plate_Chest").Fields["ArmourStats"]!["(Value=\"BaseColdResistance_%\")"]!.GetValue<long>());
    }

    /// <summary>A changed filter takes the rows the old filter wrote back out.</summary>
    [Fact]
    public void Reapply_WithAnotherFilter_RemovesWhatTheOldFilterWrote()
    {
        var mod = EmptyMod();
        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Armour.json", "ArmourStats.*", 2, filter: "Plate")], _data, new MergeReport());

        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Armour.json", "ArmourStats.*", 2, filter: "Leather")], _data, new MergeReport());

        var row = mod.Rows.Single(r => r.CurrentFile == "Traits-D_Armour.json");
        Assert.Equal("Leather_Chest", Assert.Single(row.FileItems).Name);
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

    /// <summary>"Keep my value": a row on the rule's except list keeps the modder's own value through Apply, even though the rule (without the exception) wrote it before.</summary>
    [Fact]
    public void ExceptRows_KeepAHandValue_ThroughApply_AndTheOtherRowsStillScale()
    {
        var mod = EmptyMod();
        var weight = Rule("Traits-D_Itemable.json", "Weight", 0.5);
        ScaleRules.ApplyToPackage(mod, [weight], _data, new MergeReport());
        Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["Weight"] = 3;

        var keep = weight.Clone();
        keep.ExceptRows.Add("item_fiber"); // names ignore case
        ScaleRules.ApplyToPackage(mod, [keep], _data, new MergeReport());

        Assert.Equal(3, Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["Weight"]!.GetValue<int>());
        Assert.Equal(150, Item(mod, "Traits-D_Itemable.json", "Item_Stone").Fields["Weight"]!.GetValue<long>());
        Assert.Equal(["item_fiber"], mod.ScaleRules.Single().ExceptRows);
        // Clone copies the list: changing the copy never changes the rule it came from.
        Assert.Empty(weight.ExceptRows);
    }

    [Fact]
    public void ExceptRows_AreSavedWithTheRule_AndOldFilesLoadWithNone()
    {
        var mod = EmptyMod();
        var weight = Rule("Traits-D_Itemable.json", "Weight", 0.5);
        weight.ExceptRows.Add("Item_Fiber");
        mod.ScaleRules = [weight, Rule("Traits-D_Itemable.json", "MaxStack", 2)];

        var json = ExmodJson.Serialize(mod);
        var reloaded = ExmodJson.Parse(json);

        Assert.Equal(["Item_Fiber"], reloaded.ScaleRules[0].ExceptRows);
        Assert.Empty(reloaded.ScaleRules[1].ExceptRows);
        // A rule with no exceptions is written exactly as before (no new key).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "exceptRows"));

        // A file from before exceptions existed.
        var old = ExmodJson.Parse("""
            {"name":"Old","author":"Me","version":"1","description":"","fileName":"Old","Rows":[{"CurrentFile":"EndOfMod","File_Items":[]}],
             "starlinkScaleRules":[{"enabled":true,"label":"Light","currentFile":"Traits-D_Itemable.json","field":"Weight","operation":"Multiply","value":0.5,"rowFilter":null,"includeInherited":true}]}
            """);
        Assert.Empty(Assert.Single(old.ScaleRules).ExceptRows);
    }

    /// <summary>The editor's tag reads this: a rule owns only the rows its filter and inherited setting let it change.</summary>
    [Fact]
    public void Ownership_IsExactlyTheRowsApplyWrites()
    {
        var stacks = Rule("Traits-D_Itemable.json", "MaxStack", 10, filter: "Fiber, Inherits");
        stacks.IncludeInherited = false;
        var weight = Rule("Traits-D_Itemable.json", "Weight", 0.5);
        weight.ExceptRows.Add("Item_Stone");

        var owned = ScaleRules.Ownership([stacks, weight], _data);

        Assert.True(owned.TryGet("Traits-D_Itemable.json", "item_fiber", "MaxStack", out var rules, out var value));
        Assert.Equal([0], rules);
        Assert.Equal(1000, value!.GetValue<long>());
        Assert.False(owned.TryGet("Traits-D_Itemable.json", "Item_Inherits", "MaxStack", out _, out _)); // inherits: off for this rule
        Assert.False(owned.TryGet("Traits-D_Itemable.json", "Item_Stone", "MaxStack", out _, out _)); // filter
        Assert.False(owned.TryGet("Traits-D_Itemable.json", "Item_Stone", "Weight", out _, out _)); // excepted
        Assert.True(owned.TryGet("Traits-D_Itemable.json", "Item_Inherits", "Weight", out _, out _));
    }

    /// <summary>Only the modder's own values are listed: not what the rule writes anyway, not what the last applied rule wrote, not the game's value.</summary>
    [Fact]
    public void FindHandEdits_ListsOnlyValuesSetByHand()
    {
        var mod = EmptyMod();
        ScaleRules.ApplyToPackage(mod, [Rule("Traits-D_Itemable.json", "Weight", 0.5)], _data, new MergeReport());
        Item(mod, "Traits-D_Itemable.json", "Item_Fiber").Fields["Weight"] = 7;   // by hand
        Item(mod, "Traits-D_Itemable.json", "Item_Stone").Fields["Weight"] = 300; // back to the game's value

        // The factor changes: every row's old rule value differs from the new one, but only Fiber is a hand edit.
        var lighter = Rule("Traits-D_Itemable.json", "Weight", 0.25);
        var edit = Assert.Single(ScaleRules.FindHandEdits(mod, [lighter], _data));

        Assert.Equal("Item_Fiber", edit.RowName);
        Assert.Equal("Weight", edit.Field);
        Assert.Equal(7, edit.ModValue!.GetValue<int>());
        Assert.Equal(3, edit.RuleValue!.GetValue<long>()); // 10 × 0.25 = 2.5, whole field → 3
        Assert.Equal([0], edit.RuleIndexes);

        lighter.ExceptRows.Add("Item_Fiber");
        Assert.Empty(ScaleRules.FindHandEdits(mod, [lighter], _data));
    }

    [Fact]
    public void SameValue_ComparesNumbersByValue()
    {
        Assert.True(ScaleRules.SameValue(System.Text.Json.Nodes.JsonNode.Parse("100"), System.Text.Json.Nodes.JsonValue.Create(100.0)));
        Assert.True(ScaleRules.SameValue(System.Text.Json.Nodes.JsonNode.Parse("""{"a":1,"b":[2]}"""), System.Text.Json.Nodes.JsonNode.Parse("""{"b":[2.0],"a":1.0}""")));
        Assert.False(ScaleRules.SameValue(System.Text.Json.Nodes.JsonNode.Parse("5"), System.Text.Json.Nodes.JsonNode.Parse("5.5")));
        Assert.False(ScaleRules.SameValue(System.Text.Json.Nodes.JsonNode.Parse("\"5\""), System.Text.Json.Nodes.JsonNode.Parse("5")));
    }
}
