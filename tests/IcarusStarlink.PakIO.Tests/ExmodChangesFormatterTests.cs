using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodChangesFormatterTests
{
    private static ExmodPackage MakePackage(params ExmodFileRow[] rows) => new()
    {
        Name = "Test Mod", Author = "A", Version = "1", Description = "D", FileName = "Test_Mod", Rows = [.. rows],
    };

    [Fact]
    public void Format_NoRows_ReturnsAFriendlyMessageNotAnEmptyString()
    {
        var result = ExmodChangesFormatter.Format(MakePackage());

        Assert.Contains("doesn't change", result);
    }

    [Fact]
    public void Format_CurrentFileDashConvention_IsShownAsARealPath()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Crafting-D_ProcessorRecipes.json",
            FileItems = [new ExmodFileItem { Name = "Stone_Pickaxe", Fields = { ["RequiredMillijoules"] = JsonValue.Create(1313) } }],
        });

        var result = ExmodChangesFormatter.Format(package);

        Assert.Contains("Crafting/D_ProcessorRecipes.json", result);
        Assert.DoesNotContain("Crafting-D_ProcessorRecipes.json", result);
    }

    [Fact]
    public void Format_ScalarField_ShownInlineWithItsValue()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Crafting-D_ProcessorRecipes.json",
            FileItems = [new ExmodFileItem { Name = "Stone_Pickaxe", Fields = { ["RequiredMillijoules"] = JsonValue.Create(1313) } }],
        });

        var result = ExmodChangesFormatter.Format(package);

        Assert.Contains("Stone_Pickaxe", result);
        Assert.Contains("RequiredMillijoules: 1313", result);
    }

    [Fact]
    public void Format_StringField_QuotedNotRaw()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Item_Wood", Fields = { ["DisplayName"] = JsonValue.Create("Timber") } }],
        });

        var result = ExmodChangesFormatter.Format(package);

        Assert.Contains("DisplayName: \"Timber\"", result);
    }

    [Fact]
    public void Format_NullField_ShownAsNull()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Item_Wood", Fields = { ["SomeField"] = null } }],
        });

        var result = ExmodChangesFormatter.Format(package);

        Assert.Contains("SomeField: null", result);
    }

    [Fact]
    public void Format_ObjectField_IndentedNotOneUnreadableLine()
    {
        var statsGranted = new JsonObject { ["(Value=\"BaseUpgradeSlots_+\")"] = 8, ["(Value=\"BaseOxygenSlots_+\")"] = 1 };
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Armour.json",
            FileItems = [new ExmodFileItem { Name = "Undersuit_Shengong", Fields = { ["StatsGranted"] = statsGranted } }],
        });

        var result = ExmodChangesFormatter.Format(package);

        Assert.Contains("StatsGranted:", result);
        Assert.Contains("BaseUpgradeSlots_+", result);
        Assert.Contains("BaseOxygenSlots_+", result);
        // Genuinely multi-line, not a single-line JSON blob.
        Assert.True(result.Split('\n').Length > 4);
    }

    [Fact]
    public void Format_MultipleRowsAndItems_AllPresentInOrder()
    {
        var package = MakePackage(
            new ExmodFileRow
            {
                CurrentFile = "Crafting-D_ProcessorRecipes.json",
                FileItems =
                [
                    new ExmodFileItem { Name = "Bone_Spear", Fields = { ["RequiredMillijoules"] = JsonValue.Create(1313) } },
                    new ExmodFileItem { Name = "Bone_Knife", Fields = { ["RequiredMillijoules"] = JsonValue.Create(1313) } },
                ],
            },
            new ExmodFileRow
            {
                CurrentFile = "Traits-D_Fuel.json",
                FileItems = [new ExmodFileItem { Name = "Item_Wood", Fields = { ["Weight"] = JsonValue.Create(0) } }],
            });

        var result = ExmodChangesFormatter.Format(package);

        var fileIndex = result.IndexOf("Crafting/D_ProcessorRecipes.json", StringComparison.Ordinal);
        var spearIndex = result.IndexOf("Bone_Spear", StringComparison.Ordinal);
        var knifeIndex = result.IndexOf("Bone_Knife", StringComparison.Ordinal);
        var secondFileIndex = result.IndexOf("Traits/D_Fuel.json", StringComparison.Ordinal);

        Assert.True(fileIndex < spearIndex);
        Assert.True(spearIndex < knifeIndex);
        Assert.True(knifeIndex < secondFileIndex);
    }

    /// <summary>Ferani's Steadfast Structures: only the EndOfMod marker and a replaced building blueprint. It used to read as an empty mod with a stray "EndOfMod" line.</summary>
    [Fact]
    public void Format_BlueprintOnlyMod_NamesTheReplacedGameFile_AndSkipsEndOfMod()
    {
        var package = new ExmodPackage
        {
            Name = "Ferani_Steadfast_Structures", Author = "Ferani", Version = "1.0", Description = "", FileName = "Ferani_Steadfast_Structures",
            Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }],
        };

        var result = ExmodChangesFormatter.Format(package, ["BP/Building/BP_Building_Base.uasset", "BP\\Building\\BP_Building_Base.uexp", "readme.txt"]);

        Assert.DoesNotContain("EndOfMod", result);
        Assert.Contains("doesn't change any data table fields", result);
        Assert.Contains("replacing 1 game file with its own: BP/Building/BP_Building_Base.", result);
    }
}
