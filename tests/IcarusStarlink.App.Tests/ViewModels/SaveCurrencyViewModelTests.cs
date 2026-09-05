using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class SaveCurrencyViewModelTests
{
    private static (SaveCurrencyViewModel ViewModel, JsonObject Node) Make(long initialCount = 100)
    {
        var node = new JsonObject { ["MetaRow"] = "Credits", ["Count"] = initialCount };
        return (new SaveCurrencyViewModel(node, "Credits", () => { }), node);
    }

    /// <summary>
    /// Regression guard: MarkClean() used to adopt a negative CountText as the new "clean" baseline
    /// even though ApplyToNode() (Save's actual write) rejects negative values and leaves Count
    /// unchanged — so after a Save, IsDirty compared CountText against itself and read false,
    /// showing "Saved" with the on-disk value still the original, unwritten one.
    /// </summary>
    [Fact]
    public void MarkClean_NegativeCountText_NotAdoptedAsBaseline_StaysDirty()
    {
        var (vm, node) = Make(initialCount: 100);
        vm.CountText = "-5";

        vm.ApplyToNode();
        vm.MarkClean();

        // ApplyToNode correctly declined to write the negative value.
        Assert.Equal(100, node["Count"]!.GetValue<long>());
        // MarkClean must not have adopted -5 as the new baseline either, or IsDirty below would
        // wrongly read false.
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void MarkClean_ValidCountText_AdoptedAsBaseline_BecomesClean()
    {
        var (vm, node) = Make(initialCount: 100);
        vm.CountText = "250";

        vm.ApplyToNode();
        vm.MarkClean();

        Assert.Equal(250, node["Count"]!.GetValue<long>());
        Assert.False(vm.IsDirty);
    }
}
