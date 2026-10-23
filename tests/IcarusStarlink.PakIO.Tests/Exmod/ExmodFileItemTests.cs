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

    [Fact]
    public void FromRow_LeavesOutTheRowsName_AndCopiesTheRest()
    {
        var row = new JsonObject { ["Name"] = "Item_Fiber", ["Weight"] = 10 };

        var item = ExmodFileItem.FromRow("Item_Fiber", row);

        Assert.Equal(["Weight"], item.Fields.Keys);
        Assert.Null(item.Fields["Weight"]!.Parent);
    }
}
