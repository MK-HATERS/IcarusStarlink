using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

public class TableApplierTests
{
    private static readonly ISemanticClassifier Classifier = new DefaultSemanticClassifier();

    [Fact]
    public void RoundTrip_ApplyOfDiff_ReconstructsModdedTable()
    {
        // Contract: modded must be a superset of base's rows (never silently omit an existing
        // row) — TableDiffer only visits rows modded defines, by design (see TableDiffer).
        var baseTable = JsonNode.Parse("""
            {
                "Sword": {"Damage": 10, "Weight": 2.5},
                "Shield": {"Armor": 5}
            }
            """)!.AsObject();

        var modded = JsonNode.Parse("""
            {
                "Sword": {"Damage": 25, "Weight": 2.5},
                "Shield": {"Armor": 5},
                "LaserSword": {"Damage": 99, "GlowColor": "Blue"}
            }
            """)!.AsObject();

        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier);
        var result = TableApplier.Apply(baseTable, changes);

        Assert.True(JsonNode.DeepEquals(modded, result));
    }

    [Fact]
    public void RoundTrip_NewItemWithExplicitNullField_PreservesTheExplicitNull()
    {
        var baseTable = JsonNode.Parse("""{}""")!.AsObject();
        var modded = JsonNode.Parse("""{"NewItem": {"A": 1, "B": null}}""")!.AsObject();

        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier);
        var result = TableApplier.Apply(baseTable, changes);

        Assert.True(JsonNode.DeepEquals(modded, result));
    }

    [Fact]
    public void Apply_NewItemChange_CreatesRow()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10}}""")!.AsObject();
        var change = new FieldChange(
            "Items-D_ItemsStatic.json", "LaserSword", "Damage",
            OriginalValue: null, NewValue: JsonValue.Create(99), ValueSemantic.Scalar, IsNewItem: true);

        var result = TableApplier.Apply(baseTable, [change]);

        Assert.Equal(99, result["LaserSword"]!["Damage"]!.GetValue<int>());
        Assert.Equal(10, result["Sword"]!["Damage"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_ChangeToRowRemovedFromBase_SkipsWithWarning()
    {
        var currentBase = JsonNode.Parse("""{"Sword": {"Damage": 10}}""")!.AsObject();
        var change = new FieldChange(
            "Items-D_ItemsStatic.json", "Shield", "Armor",
            OriginalValue: JsonValue.Create(5), NewValue: JsonValue.Create(50), ValueSemantic.Scalar, IsNewItem: false);

        var report = new MergeReport();
        var result = TableApplier.Apply(currentBase, [change], report);

        Assert.Null(result["Shield"]);
        var warning = Assert.Single(report.Warnings);
        Assert.Contains("Shield", warning);
        Assert.Contains("Armor", warning);
    }

    [Fact]
    public void Apply_FieldRemovedChange_RemovesKeyRatherThanSettingJsonNull()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10, "Enchantment": "Fire"}}""")!.AsObject();
        var change = new FieldChange(
            "Items-D_ItemsStatic.json", "Sword", "Enchantment",
            OriginalValue: JsonValue.Create("Fire"), NewValue: null, ValueSemantic.Scalar,
            IsFieldRemoved: true);

        var result = TableApplier.Apply(baseTable, [change]);

        Assert.False(result["Sword"]!.AsObject().ContainsKey("Enchantment"));
    }

    [Fact]
    public void RoundTrip_FieldRemoval_ReconstructsModdedTableWithKeyAbsent()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10, "Enchantment": "Fire"}}""")!.AsObject();
        var modded = JsonNode.Parse("""{"Sword": {"Damage": 10}}""")!.AsObject();

        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier);
        var result = TableApplier.Apply(baseTable, changes);

        Assert.True(JsonNode.DeepEquals(modded, result));
    }

    [Fact]
    public void Apply_DoesNotMutateBaseTable()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10}}""")!.AsObject();
        var change = new FieldChange(
            "Items-D_ItemsStatic.json", "Sword", "Damage",
            OriginalValue: JsonValue.Create(10), NewValue: JsonValue.Create(999), ValueSemantic.Scalar);

        TableApplier.Apply(baseTable, [change]);

        Assert.Equal(10, baseTable["Sword"]!["Damage"]!.GetValue<int>());
    }
}
