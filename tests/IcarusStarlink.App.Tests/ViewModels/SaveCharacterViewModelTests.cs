using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class SaveCharacterViewModelTests
{
    // No real game-data files needed — SaveGameNames degrades to empty lists for every lookup when
    // the backing files don't exist, and this test only exercises the XP field.
    private static readonly SaveGameNames EmptyNames = new(Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "NoSuchDataFolder"));

    private static (SaveCharacterViewModel ViewModel, JsonObject Node) Make(long initialXp = 1000)
    {
        var node = new JsonObject
        {
            ["CharacterName"] = "TESTER",
            ["ChrSlot"] = 0,
            ["XP"] = initialXp,
            ["IsDead"] = false,
            ["Location"] = "Prospect_Grasslands",
        };
        return (new SaveCharacterViewModel(node, EmptyNames, () => { }), node);
    }

    /// <summary>
    /// Regression guard: MarkClean() used to adopt a negative XpText as the new "clean" baseline
    /// even though ApplyToNode() (Save's actual write) rejects negative XP and leaves the node's XP
    /// field unchanged — so after a Save, IsDirty compared XpText against itself and read false,
    /// showing "Saved" with the on-disk XP still the original, unwritten value.
    /// </summary>
    [Fact]
    public void MarkClean_NegativeXpText_NotAdoptedAsBaseline_StaysDirty()
    {
        var (vm, node) = Make(initialXp: 1000);
        vm.XpText = "-50";

        vm.ApplyToNode();
        vm.MarkClean();

        Assert.Equal(1000, node["XP"]!.GetValue<long>());
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void MarkClean_ValidXpText_AdoptedAsBaseline_BecomesClean()
    {
        var (vm, node) = Make(initialXp: 1000);
        vm.XpText = "5000";

        vm.ApplyToNode();
        vm.MarkClean();

        Assert.Equal(5000, node["XP"]!.GetValue<long>());
        Assert.False(vm.IsDirty);
    }
}
