using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodJsonTests
{
    // Shape matches two real .EXMODZ samples inspected during planning; values are synthetic.
    private const string RealisticJson = """
        {
            "name": "Faster Processors",
            "author": "TestAuthor",
            "version": "1.2",
            "description": "Speeds up processor recipes.",
            "fileName": "Faster_Processors",
            "imageURL": "",
            "readmeURL": "",
            "Level2": "True",
            "Rows": [
                {
                    "CurrentFile": "Crafting-D_ProcessorRecipes.json",
                    "File_Items": [
                        {
                            "Name": "SmelterRecipe",
                            "CraftTime": 5,
                            "Inputs": [{"RowName": "Ore_Iron"}, {"RowName": "Fuel_Coal"}]
                        }
                    ]
                }
            ]
        }
        """;

    [Fact]
    public void Parse_RealisticShape_ExtractsAllFields()
    {
        var package = ExmodJson.Parse(RealisticJson);

        Assert.Equal("Faster Processors", package.Name);
        Assert.Equal("TestAuthor", package.Author);
        Assert.Equal("1.2", package.Version);
        Assert.Equal("Speeds up processor recipes.", package.Description);
        Assert.Equal("Faster_Processors", package.FileName);
        Assert.Equal("True", package.Level2); // string, not JSON boolean — confirmed against real samples
        Assert.Null(package.Week);
        Assert.Null(package.VariantGroup);

        var row = Assert.Single(package.Rows);
        Assert.Equal("Crafting-D_ProcessorRecipes.json", row.CurrentFile);

        var item = Assert.Single(row.FileItems);
        Assert.Equal("SmelterRecipe", item.Name);
        Assert.Equal(2, item.Fields.Count);
        Assert.Equal(5, item.Fields["CraftTime"]!.GetValue<int>());
        Assert.Equal(2, item.Fields["Inputs"]!.AsArray().Count);
    }

    [Fact]
    public void Parse_MinimalRequiredFieldsOnly_Succeeds()
    {
        const string json = """
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "F"}
            """;

        var package = ExmodJson.Parse(json);

        Assert.Equal("N", package.Name);
        Assert.Empty(package.Rows);
        Assert.Null(package.ImageUrl);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("author")]
    [InlineData("version")]
    [InlineData("description")]
    [InlineData("fileName")]
    public void Parse_MissingRequiredField_ThrowsFormatException(string missingField)
    {
        var fields = new Dictionary<string, string>
        {
            ["name"] = "N", ["author"] = "A", ["version"] = "1", ["description"] = "D", ["fileName"] = "F",
        };
        fields.Remove(missingField);
        var json = "{" + string.Join(",", fields.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";

        var ex = Assert.Throws<FormatException>(() => ExmodJson.Parse(json));
        Assert.Contains(missingField, ex.Message);
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Parse_FileNameLooksLikeAPath_ThrowsFormatException(string maliciousFileName)
    {
        var root = new JsonObject
        {
            ["name"] = "N", ["author"] = "A", ["version"] = "1", ["description"] = "D",
            ["fileName"] = maliciousFileName,
        };

        Assert.Throws<FormatException>(() => ExmodJson.Parse(root));
    }

    [Fact]
    public void Parse_CurrentFileWithControlCharacter_ThrowsFormatException()
    {
        var currentFileWithTab = "Items" + '\t' + ".json";
        var root = new JsonObject
        {
            ["name"] = "N", ["author"] = "A", ["version"] = "1", ["description"] = "D", ["fileName"] = "F",
            ["Rows"] = new JsonArray
            {
                new JsonObject { ["CurrentFile"] = currentFileWithTab, ["File_Items"] = new JsonArray() },
            },
        };

        Assert.Throws<FormatException>(() => ExmodJson.Parse(root));
    }

    [Fact]
    public void Parse_RowsContainsNonObjectEntry_ThrowsInsteadOfSilentlyDroppingIt()
    {
        var root = new JsonObject
        {
            ["name"] = "N", ["author"] = "A", ["version"] = "1", ["description"] = "D", ["fileName"] = "F",
            ["Rows"] = new JsonArray { "garbage" },
        };

        Assert.Throws<FormatException>(() => ExmodJson.Parse(root));
    }

    [Fact]
    public void Parse_FileItemsContainsNonObjectEntry_ThrowsInsteadOfSilentlyDroppingIt()
    {
        var root = new JsonObject
        {
            ["name"] = "N", ["author"] = "A", ["version"] = "1", ["description"] = "D", ["fileName"] = "F",
            ["Rows"] = new JsonArray
            {
                new JsonObject
                {
                    ["CurrentFile"] = "Items-D_ItemsStatic.json",
                    ["File_Items"] = new JsonArray { "garbage" },
                },
            },
        };

        Assert.Throws<FormatException>(() => ExmodJson.Parse(root));
    }

    [Fact]
    public void Parse_FileItemObjectHasADuplicateJsonKey_KeepsTheLastValue()
    {
        // Real-world regression, twice revised as more was learned. A Jimk72-authored EXMOD has a
        // single File_Item object listing "ResourceCostMultipliers" twice. This originally crashed
        // the app (raw ArgumentException from JsonObject building its property dictionary), then
        // was made to throw FormatException so the library scan skipped the mod.
        //
        // Skipping turned out to be too harsh: a second real mod (Coracks_Ammo_and_Repair_x100,
        // which repeats "description") was then invisible and unusable here despite working fine
        // in classic IMM. Duplicates are now tolerated with last-wins — normal JSON semantics, and
        // what those files evidently mean.
        const string json = """
            {
                "name": "N", "author": "A", "version": "1", "description": "D", "fileName": "F",
                "Rows": [
                    {
                        "CurrentFile": "Items-D_ItemsStatic.json",
                        "File_Items": [
                            {
                                "Name": "Jimk_Wood_Fence",
                                "ResourceCostMultipliers": [1],
                                "ResourceCostMultipliers": [2]
                            }
                        ]
                    }
                ]
            }
            """;

        var package = ExmodJson.Parse(json);

        var item = Assert.Single(Assert.Single(package.Rows).FileItems);
        Assert.Equal("[2]", item.Fields["ResourceCostMultipliers"]!.ToJsonString());
    }

    [Fact]
    public void Parse_ItemNameBlank_ThrowsFormatException()
    {
        var root = new JsonObject
        {
            ["name"] = "N", ["author"] = "A", ["version"] = "1", ["description"] = "D", ["fileName"] = "F",
            ["Rows"] = new JsonArray
            {
                new JsonObject
                {
                    ["CurrentFile"] = "Items-D_ItemsStatic.json",
                    ["File_Items"] = new JsonArray { new JsonObject { ["Name"] = "   ", ["Damage"] = 1 } },
                },
            },
        };

        Assert.Throws<FormatException>(() => ExmodJson.Parse(root));
    }

    [Fact]
    public void Parse_WithVariantKeys_ExtractsThem()
    {
        const string json = """
            {
                "name": "Take Home Tools", "author": "A", "version": "1", "description": "D",
                "fileName": "Take_Home_Tools",
                "variantGroup": "Take_Home", "variant": "Tools", "variantSort": 1
            }
            """;

        var package = ExmodJson.Parse(json);

        Assert.Equal("Take_Home", package.VariantGroup);
        Assert.Equal("Tools", package.Variant);
        Assert.Equal(1, package.VariantSort);
    }

    [Fact]
    public void Serialize_NullImageAndReadmeUrl_WritesEmptyStringNotAbsent()
    {
        // Deliberately inconsistent with Level2/week/variant*: both real samples always carried
        // imageURL/readmeURL present-but-empty rather than omitted, so null maps to "" here.
        var package = ExmodJson.Parse("""
            {"name": "N", "author": "A", "version": "1", "description": "D", "fileName": "F"}
            """);

        var json = ExmodJson.Serialize(package);
        var reparsed = ExmodJson.Parse(json);

        Assert.Contains("\"imageURL\": \"\"", json);
        Assert.Equal("", reparsed.ImageUrl);
    }

    [Fact]
    public void ToJsonObject_BlankCurrentFile_ThrowsSymmetricallyWithParse()
    {
        var package = new ExmodPackage
        {
            Name = "N", Author = "A", Version = "1", Description = "D", FileName = "F",
            Rows = [new ExmodFileRow { CurrentFile = "   " }],
        };

        Assert.Throws<FormatException>(() => ExmodJson.ToJsonObject(package));
    }

    [Fact]
    public void ToJsonObject_ItemFieldLiterallyNamedName_ThrowsRatherThanCorruptingRowIdentity()
    {
        var package = new ExmodPackage
        {
            Name = "N", Author = "A", Version = "1", Description = "D", FileName = "F",
            Rows =
            [
                new ExmodFileRow
                {
                    CurrentFile = "Items-D_ItemsStatic.json",
                    FileItems =
                    [
                        new ExmodFileItem
                        {
                            Name = "Sword",
                            Fields = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
                            {
                                ["Name"] = "Excalibur",
                            },
                        },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<FormatException>(() => ExmodJson.ToJsonObject(package));
        Assert.Contains("Sword", ex.Message);
    }

    [Fact]
    public void ParseRow_RealisticShape_MatchesWhatParseWouldProduceForTheSameRow()
    {
        // ParseRow (Phase 7.2's File JSON raw view) is ParseCore's per-row logic pulled out into
        // its own public method — this confirms it still produces exactly the same row a full
        // Parse() of the same JSON would, not a divergent code path.
        var fullPackage = ExmodJson.Parse(RealisticJson);
        var rowObject = ExmodJson.ToJsonObject(fullPackage).AsObject()["Rows"]!.AsArray()[0]!.AsObject();

        var row = ExmodJson.ParseRow(rowObject);

        Assert.Equal("Crafting-D_ProcessorRecipes.json", row.CurrentFile);
        var item = Assert.Single(row.FileItems);
        Assert.Equal("SmelterRecipe", item.Name);
        Assert.Equal(5, item.Fields["CraftTime"]!.GetValue<int>());
    }

    [Fact]
    public void ParseRow_NonObjectFileItemsEntry_ThrowsInsteadOfSilentlyDroppingIt()
    {
        var rowObject = new JsonObject
        {
            ["CurrentFile"] = "Items-D_ItemsStatic.json",
            ["File_Items"] = new JsonArray { "garbage" },
        };

        Assert.Throws<FormatException>(() => ExmodJson.ParseRow(rowObject));
    }

    [Fact]
    public void RowToJsonObject_ThenParseRow_RoundTrips()
    {
        var row = new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Item_Wood", Fields = { ["Weight"] = JsonValue.Create(50) } }],
        };

        var reparsed = ExmodJson.ParseRow(ExmodJson.RowToJsonObject(row));

        Assert.Equal(row.CurrentFile, reparsed.CurrentFile);
        Assert.Equal("Item_Wood", reparsed.FileItems[0].Name);
        Assert.Equal(50, reparsed.FileItems[0].Fields["Weight"]!.GetValue<int>());
    }

    [Fact]
    public void RowToJsonObject_ItemFieldLiterallyNamedName_ThrowsSameAsToJsonObject()
    {
        var row = new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemsStatic.json",
            FileItems = [new ExmodFileItem { Name = "Sword", Fields = { ["Name"] = "Excalibur" } }],
        };

        var ex = Assert.Throws<FormatException>(() => ExmodJson.RowToJsonObject(row));
        Assert.Contains("Sword", ex.Message);
    }

    [Fact]
    public void RoundTrip_SerializeThenParse_ReproducesEquivalentPackage()
    {
        var original = ExmodJson.Parse(RealisticJson);

        var reparsed = ExmodJson.Parse(ExmodJson.Serialize(original));

        Assert.Equal(original.Name, reparsed.Name);
        Assert.Equal(original.Level2, reparsed.Level2);
        Assert.Single(reparsed.Rows);
        Assert.Equal(original.Rows[0].CurrentFile, reparsed.Rows[0].CurrentFile);
        Assert.Equal(original.Rows[0].FileItems[0].Name, reparsed.Rows[0].FileItems[0].Name);
        Assert.Equal(5, reparsed.Rows[0].FileItems[0].Fields["CraftTime"]!.GetValue<int>());
    }
}
