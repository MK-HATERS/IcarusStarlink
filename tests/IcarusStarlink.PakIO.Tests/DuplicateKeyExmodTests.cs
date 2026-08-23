using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

/// <summary>
/// Real mods repeat a key inside one JSON object. Before this was handled, such a mod threw and
/// was skipped entirely — invisible in the Library and unusable — despite working fine in classic
/// IMM. Both shapes here are taken from the user's own library.
/// </summary>
public sealed class DuplicateKeyExmodTests
{
    [Fact]
    public void Parse_DuplicateHeaderKey_KeepsLastValueInsteadOfFailing()
    {
        // Coracks_Ammo_and_Repair_x100's own header lists "description" twice.
        var json = """
            {
              "fileName": "Coracks_Test", "name": "Coracks Test", "author": "Coracks",
              "version": "1.0", "description": "first copy", "description": "second copy",
              "Rows": []
            }
            """;

        var package = ExmodJson.Parse(json);

        Assert.Equal("Coracks Test", package.Name);
        Assert.Equal("second copy", package.Description);
    }

    [Fact]
    public void Parse_DuplicateFieldKeyInsideAnItem_KeepsLastValue()
    {
        // A Jimk72-authored file lists "ResourceCostMultipliers" twice inside one item.
        var json = """
            {
              "fileName": "Dup_Field", "name": "Dup Field", "author": "a", "version": "1.0", "description": "d",
              "Rows": [
                {
                  "CurrentFile": "Crafting-D_ProcessorRecipes.json",
                  "File_Items": [
                    { "Name": "Thing", "ResourceCostMultipliers": 1, "ResourceCostMultipliers": 2 }
                  ]
                }
              ]
            }
            """;

        var package = ExmodJson.Parse(json);

        var item = Assert.Single(Assert.Single(package.Rows).FileItems);
        Assert.Equal("2", item.Fields["ResourceCostMultipliers"]!.ToJsonString());
    }

    [Fact]
    public void Parse_NestedArraysAndObjects_SurviveTheTolerantParse()
    {
        // The tolerant parse rebuilds the whole tree by hand, so nested shapes (a recipe's Inputs
        // array of objects) have to come through intact, not just top-level scalars.
        var json = """
            {
              "fileName": "Nested", "name": "Nested", "author": "a", "version": "1.0", "description": "d",
              "Rows": [
                {
                  "CurrentFile": "Crafting-D_ProcessorRecipes.json",
                  "File_Items": [
                    { "Name": "Recipe", "Inputs": [ { "Element": { "RowName": "Wood" }, "Count": 3 } ], "Enabled": true, "Ratio": 1.5, "Missing": null }
                  ]
                }
              ]
            }
            """;

        var item = Assert.Single(Assert.Single(ExmodJson.Parse(json).Rows).FileItems);

        Assert.Equal("""[{"Element":{"RowName":"Wood"},"Count":3}]""", item.Fields["Inputs"]!.ToJsonString());
        Assert.Equal("true", item.Fields["Enabled"]!.ToJsonString());
        Assert.Equal("1.5", item.Fields["Ratio"]!.ToJsonString());
        Assert.Null(item.Fields["Missing"]);
    }

    [Fact]
    public void Parse_StillRejectsGenuinelyMalformedJson()
    {
        Assert.ThrowsAny<Exception>(() => ExmodJson.Parse("{not json at all"));
    }
}
