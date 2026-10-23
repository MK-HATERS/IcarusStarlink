using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class SaveMountViewModelTests
{
    private static (SaveMountViewModel ViewModel, JsonObject Node) Make(int initialLevel = 5)
    {
        var node = new JsonObject { ["MountName"] = "Rex", ["MountLevel"] = initialLevel, ["MountType"] = "Moa" };
        var vm = new SaveMountViewModel(node, "Rex", initialLevel, "Moa", iconPath: null, onDirtyChanged: () => { });
        return (vm, node);
    }

    /// <summary>
    /// Regression guard: MarkClean() used to adopt a negative LevelText as the new "clean" baseline
    /// even though ApplyToNode() (Save's actual write) rejects a negative level and leaves
    /// MountLevel unchanged — so after a Save, IsDirty compared LevelText against itself and read
    /// false, showing "Saved" with the on-disk MountLevel still the original, unwritten one.
    /// </summary>
    [Fact]
    public void MarkClean_NegativeLevelText_NotAdoptedAsBaseline_StaysDirty()
    {
        var (vm, node) = Make(initialLevel: 5);
        vm.LevelText = "-3";

        vm.ApplyToNode();
        vm.MarkClean();

        Assert.Equal(5, node["MountLevel"]!.GetValue<int>());
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void MarkClean_ValidLevelText_AdoptedAsBaseline_BecomesClean()
    {
        var (vm, node) = Make(initialLevel: 5);
        vm.LevelText = "10";

        vm.ApplyToNode();
        vm.MarkClean();

        Assert.Equal(10, node["MountLevel"]!.GetValue<int>());
        Assert.False(vm.IsDirty);
    }

    /// <summary>Plan step 4: a mount's data is only ever edited when it reads and writes back exactly.</summary>
    [Fact]
    public void Blob_ThatCantBeRead_IsRefused()
    {
        Assert.Null(IcarusStarlink.App.Services.MountBlob.TryRead([1, 2, 3, 4, 5], out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Mount_WhoseDataCantBeRead_StaysLabelOnly_AndItsDataIsNeverWritten()
    {
        var garbage = new JsonArray(1, 2, 3, 4, 5);
        var node = new JsonObject
        {
            ["MountName"] = "Frank",
            ["MountLevel"] = 7,
            ["MountType"] = "Dog",
            ["RecorderBlob"] = new JsonObject { ["ComponentClassName"] = "x", ["BinaryData"] = garbage },
        };
        var vm = new SaveMountViewModel(node, "Frank", 7, "Dog", null, () => { });

        Assert.False(vm.CanEditInside);
        Assert.NotNull(vm.InsideRefusal);

        vm.Name = "Rex";
        vm.LevelText = "9";
        vm.ApplyToNode();

        Assert.Equal("Rex", (string?)node["MountName"]);
        Assert.Equal(9, (int?)node["MountLevel"]);
        Assert.Same(garbage, node["RecorderBlob"]!["BinaryData"]);
    }
}
