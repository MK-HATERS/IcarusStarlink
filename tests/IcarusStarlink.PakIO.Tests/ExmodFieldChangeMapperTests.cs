using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodFieldChangeMapperTests
{
    private static readonly ISemanticClassifier Classifier = new DefaultSemanticClassifier();

    [Fact]
    public void ToFieldChanges_ProducesOneChangePerField_DefaultingNewItemTrueAndRemovedFalse()
    {
        var package = ExmodJson.Parse("""
            {
                "name": "N", "author": "A", "version": "1", "description": "D", "fileName": "F",
                "Rows": [
                    {
                        "CurrentFile": "Items-D_ItemsStatic.json",
                        "File_Items": [
                            {"Name": "Sword", "Damage": 25, "Weight": 2.5}
                        ]
                    }
                ]
            }
            """);

        var changes = ExmodFieldChangeMapper.ToFieldChanges(package, Classifier);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal("Items-D_ItemsStatic.json", c.CurrentFile));
        Assert.All(changes, c => Assert.Equal("Sword", c.ItemName));
        Assert.All(changes, c => Assert.Null(c.OriginalValue));
        Assert.All(changes, c => Assert.True(c.IsNewItem));
        Assert.All(changes, c => Assert.False(c.IsFieldRemoved));
        Assert.Contains(changes, c => c.FieldName == "Damage" && c.NewValue!.GetValue<int>() == 25);
    }

    [Fact]
    public void FromFieldChanges_GroupsByFileThenItem()
    {
        var changes = new List<FieldChange>
        {
            new("Items-D_ItemsStatic.json", "Sword", "Damage", null, System.Text.Json.Nodes.JsonValue.Create(25), ValueSemantic.Scalar),
            new("Items-D_ItemsStatic.json", "Sword", "Weight", null, System.Text.Json.Nodes.JsonValue.Create(2.5), ValueSemantic.Scalar),
            new("Crafting-D_ProcessorRecipes.json", "SwordRecipe", "CraftTime", null, System.Text.Json.Nodes.JsonValue.Create(1), ValueSemantic.Scalar),
        };

        var rows = ExmodFieldChangeMapper.FromFieldChanges(changes);

        Assert.Equal(2, rows.Count);
        var itemsFile = Assert.Single(rows, r => r.CurrentFile == "Items-D_ItemsStatic.json");
        var swordItem = Assert.Single(itemsFile.FileItems);
        Assert.Equal("Sword", swordItem.Name);
        Assert.Equal(2, swordItem.Fields.Count);

        var craftingFile = Assert.Single(rows, r => r.CurrentFile == "Crafting-D_ProcessorRecipes.json");
        Assert.Single(craftingFile.FileItems);
    }

    [Fact]
    public void FromFieldChanges_FieldNamedName_ThrowsRatherThanCorruptingRowIdentity()
    {
        var changes = new List<FieldChange>
        {
            new("Items-D_ItemsStatic.json", "Sword", "Name",
                null, System.Text.Json.Nodes.JsonValue.Create("Excalibur"), ValueSemantic.Scalar),
        };

        var ex = Assert.Throws<FormatException>(() => ExmodFieldChangeMapper.FromFieldChanges(changes));
        Assert.Contains("Sword", ex.Message);
    }

    [Fact]
    public void FromFieldChanges_IsFieldRemovedTrue_WritesNullEvenIfNewValueIsSet()
    {
        // IsFieldRemoved must be authoritative (matching TableApplier), not just a proxy for
        // "NewValue happens to be null" — nothing enforces that invariant on an arbitrary
        // FieldChange.
        var changes = new List<FieldChange>
        {
            new("Items-D_ItemsStatic.json", "Sword", "Enchantment",
                null, System.Text.Json.Nodes.JsonValue.Create("StaleValue"), ValueSemantic.Scalar,
                IsFieldRemoved: true),
        };

        var rows = ExmodFieldChangeMapper.FromFieldChanges(changes);

        var field = rows[0].FileItems[0].Fields["Enchantment"];
        Assert.Null(field);
    }

    [Fact]
    public void RoundTrip_RemovedField_ThroughSerializeAndParse_PreservesIsFieldRemoved()
    {
        var removalChange = new FieldChange(
            "Items-D_ItemsStatic.json", "Sword", "Enchantment",
            null, null, ValueSemantic.Scalar, IsFieldRemoved: true);

        var rows = ExmodFieldChangeMapper.FromFieldChanges([removalChange]);
        var package = new ExmodPackage { Name = "N", Author = "A", Version = "1", Description = "D", FileName = "F", Rows = rows };

        // The full round trip through the actual on-disk representation, not just the in-memory model.
        var json = ExmodJson.Serialize(package);
        var reparsed = ExmodJson.Parse(json);
        var changesAfterRoundTrip = ExmodFieldChangeMapper.ToFieldChanges(reparsed, Classifier);

        var change = Assert.Single(changesAfterRoundTrip);
        Assert.True(change.IsFieldRemoved);
    }

    [Fact]
    public void RoundTrip_ToThenFromFieldChanges_PreservesFieldValues()
    {
        var package = ExmodJson.Parse("""
            {
                "name": "N", "author": "A", "version": "1", "description": "D", "fileName": "F",
                "Rows": [
                    {"CurrentFile": "Items-D_ItemsStatic.json",
                     "File_Items": [{"Name": "Sword", "Damage": 25}]}
                ]
            }
            """);

        var changes = ExmodFieldChangeMapper.ToFieldChanges(package, Classifier);
        var roundTripped = ExmodFieldChangeMapper.FromFieldChanges(changes);

        var row = Assert.Single(roundTripped);
        var item = Assert.Single(row.FileItems);
        Assert.Equal(25, item.Fields["Damage"]!.GetValue<int>());
    }
}
