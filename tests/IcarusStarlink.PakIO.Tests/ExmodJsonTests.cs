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
