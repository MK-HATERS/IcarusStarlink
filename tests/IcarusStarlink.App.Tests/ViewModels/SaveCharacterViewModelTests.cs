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

    /// <summary>Audit finding: XP was parsed as a long with only a >= 0 check, so a value past int32 (the game's own field type) was written and then wrapped or was discarded in-game.</summary>
    [Fact]
    public void ApplyToNode_XpBeyondInt32_KeepsTheOriginalValue()
    {
        var (vm, node) = Make(initialXp: 1000);
        vm.XpText = "3000000000";

        vm.ApplyToNode();

        Assert.Equal(1000, node["XP"]!.GetValue<long>());
    }

    /// <summary>A game update added the on/off IsReworkedCharacter to Cosmetic; reading it as a number broke the whole Saves page.</summary>
    [Fact]
    public void Cosmetic_OnOffFieldFromTheGame_IsSkipped_AndKeptWhenSaving()
    {
        var node = new JsonObject
        {
            ["CharacterName"] = "TESTER", ["ChrSlot"] = 0, ["XP"] = 10, ["IsDead"] = false,
            ["Cosmetic"] = new JsonObject { ["IsMale"] = true, ["Customization_Head"] = 12345, ["IsReworkedCharacter"] = false },
        };

        var vm = new SaveCharacterViewModel(node, EmptyNames, () => { });
        vm.ApplyToNode();

        Assert.Equal("Customization_Head", Assert.Single(vm.Cosmetics).Key);
        Assert.False(node["Cosmetic"]!["IsReworkedCharacter"]!.GetValue<bool>());
        Assert.Equal(12345, node["Cosmetic"]!["Customization_Head"]!.GetValue<long>());
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
