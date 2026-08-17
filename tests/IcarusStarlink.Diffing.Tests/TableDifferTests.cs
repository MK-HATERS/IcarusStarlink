using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.Diffing.Tests;

public class TableDifferTests
{
    private static readonly ISemanticClassifier Classifier = new DefaultSemanticClassifier();

    [Fact]
    public void Diff_IdenticalTables_ReturnsNoChanges()
    {
        var table = JsonNode.Parse("""{"Sword": {"Damage": 10, "Weight": 2.5}}""")!.AsObject();
        var modded = JsonNode.Parse("""{"Sword": {"Damage": 10, "Weight": 2.5}}""")!.AsObject();

        var changes = TableDiffer.Diff(table, modded, "Items-D_ItemsStatic.json", Classifier);

        Assert.Empty(changes);
    }

    [Fact]
    public void Diff_ScalarFieldChanged_ProducesSingleFieldChange()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10, "Weight": 2.5}}""")!.AsObject();
        var modded = JsonNode.Parse("""{"Sword": {"Damage": 25, "Weight": 2.5}}""")!.AsObject();

        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier);

        var change = Assert.Single(changes);
        Assert.Equal("Sword", change.ItemName);
        Assert.Equal("Damage", change.FieldName);
        Assert.Equal(10, change.OriginalValue!.GetValue<int>());
        Assert.Equal(25, change.NewValue!.GetValue<int>());
        Assert.False(change.IsNewItem);
    }

    [Fact]
    public void Diff_NewRowInModded_MarksEveryFieldAsNewItem()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10}}""")!.AsObject();
        var modded = JsonNode.Parse("""{"Sword": {"Damage": 10}, "LaserSword": {"Damage": 99, "GlowColor": "Blue"}}""")!.AsObject();

        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal("LaserSword", c.ItemName));
        Assert.All(changes, c => Assert.True(c.IsNewItem));
        Assert.Contains(changes, c => c.FieldName == "Damage" && c.NewValue!.GetValue<int>() == 99);
        Assert.Contains(changes, c => c.FieldName == "GlowColor");
    }

    [Fact]
    public void Diff_NewItemWithExplicitNullField_IsNotDroppedAsANoOp()
    {
        // JsonNode represents "key absent" and "key present with explicit JSON null" the same
        // way (C# null), so a naive value-only comparison would treat this as no change.
        var baseTable = JsonNode.Parse("""{}""")!.AsObject();
        var modded = JsonNode.Parse("""{"NewItem": {"A": 1, "B": null}}""")!.AsObject();

        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier);

        Assert.Equal(2, changes.Count);
        var fieldB = Assert.Single(changes, c => c.FieldName == "B");
        Assert.True(fieldB.IsNewItem);
        Assert.False(fieldB.IsFieldRemoved);
        Assert.Null(fieldB.NewValue);
    }

    [Fact]
    public void Diff_RowOnlyInBase_IsNotVisited()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10}, "Shield": {"Armor": 5}}""")!.AsObject();
        var modded = JsonNode.Parse("""{"Sword": {"Damage": 10}}""")!.AsObject();

        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier);

        Assert.Empty(changes);
    }

    [Fact]
    public void Diff_MalformedModdedRow_SkipsWithWarningInsteadOfBlankingEveryField()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10, "Weight": 2.5}}""")!.AsObject();
        var modded = JsonNode.Parse("""{"Sword": ["not", "an", "object"]}""")!.AsObject();

        var report = new MergeReport();
        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier, report);

        Assert.Empty(changes);
        var warning = Assert.Single(report.Warnings);
        Assert.Contains("Sword", warning);
    }

    [Fact]
    public void Diff_FieldRemovedFromExistingRow_ProducesNullNewValue()
    {
        var baseTable = JsonNode.Parse("""{"Sword": {"Damage": 10, "Enchantment": "Fire"}}""")!.AsObject();
        var modded = JsonNode.Parse("""{"Sword": {"Damage": 10}}""")!.AsObject();

        var changes = TableDiffer.Diff(baseTable, modded, "Items-D_ItemsStatic.json", Classifier);

        var change = Assert.Single(changes);
        Assert.Equal("Enchantment", change.FieldName);
        Assert.Null(change.NewValue);
        Assert.False(change.IsNewItem);
    }
}
