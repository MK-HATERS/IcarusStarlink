using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodStaleItemRepairTests
{
    private static ExmodPackage MakePackage(params ExmodFileRow[] rows) => new()
    {
        Name = "Test Mod", Author = "A", Version = "1", Description = "D", FileName = "Test_Mod", Rows = [.. rows],
    };

    [Fact]
    public void RenameItem_MatchingItem_RenamesInPlace()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Old_Item", Fields = { ["A"] = JsonValue.Create(1) } }],
        });

        var renamed = ExmodStaleItemRepair.RenameItem(package, "Traits-D_Fuel.json", "Old_Item", "New_Item");

        Assert.True(renamed);
        var item = Assert.Single(package.Rows[0].FileItems);
        Assert.Equal("New_Item", item.Name);
        Assert.Equal(1, (int)item.Fields["A"]!);
    }

    [Fact]
    public void RenameItem_DuplicateItemEntries_RenamesEveryOne()
    {
        // Real EXMOD mods can legitimately list the same item name more than once (see the field
        // notes) — every entry must get renamed, not just the first.
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems =
            [
                new ExmodFileItem { Name = "Old_Item", Fields = { ["A"] = JsonValue.Create(1) } },
                new ExmodFileItem { Name = "Old_Item", Fields = { ["B"] = JsonValue.Create(2) } },
                new ExmodFileItem { Name = "Untouched", Fields = { ["C"] = JsonValue.Create(3) } },
            ],
        });

        var renamed = ExmodStaleItemRepair.RenameItem(package, "Traits-D_Fuel.json", "Old_Item", "New_Item");

        Assert.True(renamed);
        Assert.Equal(2, package.Rows[0].FileItems.Count(i => i.Name == "New_Item"));
        Assert.Single(package.Rows[0].FileItems, i => i.Name == "Untouched");
    }

    [Fact]
    public void RenameItem_NoMatchingFile_ReturnsFalse()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Old_Item" }],
        });

        var renamed = ExmodStaleItemRepair.RenameItem(package, "NoSuchFile.json", "Old_Item", "New_Item");

        Assert.False(renamed);
    }

    [Fact]
    public void RenameItem_NoMatchingItemName_ReturnsFalse()
    {
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Old_Item" }],
        });

        var renamed = ExmodStaleItemRepair.RenameItem(package, "Traits-D_Fuel.json", "Nonexistent", "New_Item");

        Assert.False(renamed);
    }
}
