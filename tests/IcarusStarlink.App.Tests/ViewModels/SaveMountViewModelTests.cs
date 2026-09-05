using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class SaveMountViewModelTests
{
    private static (SaveMountViewModel ViewModel, JsonObject Node) Make(int initialLevel = 5)
    {
        var node = new JsonObject { ["MountName"] = "Rex", ["MountLevel"] = initialLevel, ["MountType"] = "Moa" };
        var vm = new SaveMountViewModel(node, "Rex", initialLevel, "Moa", availableTypeRowNames: ["Moa", "Wolf"], iconPath: null, onDirtyChanged: () => { });
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
}
