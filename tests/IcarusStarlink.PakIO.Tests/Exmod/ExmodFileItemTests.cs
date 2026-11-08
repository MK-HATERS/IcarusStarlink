using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

public class ExmodFileItemTests
{
    [Fact]
    public void CloneAs_CopiesEveryValue_SoEditingTheCopyLeavesTheOriginal()
    {
        var original = new ExmodFileItem { Name = "Envirosuit", Fields = { ["ArmourStats"] = new JsonObject { ["Cold"] = 5 }, ["Weight"] = 100 } };

        var copy = original.CloneAs("Envirosuit_Copy");
        copy.Fields["ArmourStats"]!["Cold"] = 50;

        Assert.Equal("Envirosuit_Copy", copy.Name);
        Assert.Equal(5, original.Fields["ArmourStats"]!["Cold"]!.GetValue<int>());
        Assert.Equal(100, copy.Fields["Weight"]!.GetValue<int>());
    }

    /// <summary>Regression: Duplicate (Ctrl+D) kept the old row's text keys, so the copy and the original had two texts under one NSLOCTEXT key.</summary>
    [Fact]
    public void DuplicateAs_GivesTheCopysOwnTextsKeysUnderItsName_AndKeepsTheGamesKeys()
    {
        var original = new ExmodFileItem
        {
            Name = "Item_Dragon_Suit",
            Fields =
            {
                ["DisplayName"] = "NSLOCTEXT(\"D_Itemable\", \"Item_Dragon_Suit-DisplayName\", \"Dragon Suit\")",
                ["Description"] = "NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-Description\", \"A suit.\")",
                ["Weight"] = 5,
            },
        };

        var copy = original.DuplicateAs("Item_Dragon_Suit_Copy");

        Assert.Equal("Item_Dragon_Suit_Copy", copy.Name);
        Assert.Equal("NSLOCTEXT(\"D_Itemable\", \"Item_Dragon_Suit_Copy-DisplayName\", \"Dragon Suit\")", copy.Fields["DisplayName"]!.GetValue<string>());
        // A key the game owns stays: reusing it keeps the game's translations.
        Assert.Equal("NSLOCTEXT(\"D_Itemable\", \"Item_Envirosuit-Description\", \"A suit.\")", copy.Fields["Description"]!.GetValue<string>());
        Assert.Equal(5, copy.Fields["Weight"]!.GetValue<int>());
        Assert.Equal("NSLOCTEXT(\"D_Itemable\", \"Item_Dragon_Suit-DisplayName\", \"Dragon Suit\")", original.Fields["DisplayName"]!.GetValue<string>());
    }

    [Fact]
    public void FromRow_LeavesOutTheRowsName_AndCopiesTheRest()
    {
        var row = new JsonObject { ["Name"] = "Item_Fiber", ["Weight"] = 10 };

        var item = ExmodFileItem.FromRow("Item_Fiber", row);

        Assert.Equal(["Weight"], item.Fields.Keys);
        Assert.Null(item.Fields["Weight"]!.Parent);
    }
}
