using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class SaveCharacterViewModelTests
{
    // No real game-data files needed — SaveGameNames degrades to empty lists for every lookup when
    // the backing files don't exist; these tests cover XP, talents and cosmetics, none of which need names.
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

    /// <summary>The character's talent list offers character trees only (D_TalentTrees Archetype → D_TalentArchetypes Model Player/Blueprint/Solo), never workshop or mount talents — but a learned one stays whatever its tree.</summary>
    [Fact]
    public void Talents_OfferOnlyCharacterTrees_ButKeepWhatTheSaveHas()
    {
        var data = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Talents_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(data, "Talents"));
        try
        {
            File.WriteAllText(Path.Combine(data, "Talents", "D_TalentArchetypes.json"), """
                { "Rows": [ { "Name": "Survival", "Model": { "RowName": "Player" } }, { "Name": "Blueprint_Crafting", "Model": { "RowName": "Blueprint" } },
                            { "Name": "Workshop_Armor", "Model": { "RowName": "Workshop" } }, { "Name": "Creature_Wolf", "Model": { "RowName": "Creature" } } ] }
                """);
            File.WriteAllText(Path.Combine(data, "Talents", "D_TalentTrees.json"), """
                { "Rows": [ { "Name": "Survival_Resources", "Archetype": { "RowName": "Survival" } }, { "Name": "Crafting_T1", "Archetype": { "RowName": "Blueprint_Crafting" } },
                            { "Name": "Workshop_Armor", "Archetype": { "RowName": "Workshop_Armor" } }, { "Name": "Wolf_Tree", "Archetype": { "RowName": "Creature_Wolf" } } ] }
                """);
            File.WriteAllText(Path.Combine(data, "Talents", "D_Talents.json"), """
                { "Rows": [ { "Name": "Survival_Gather", "TalentTree": { "RowName": "Survival_Resources" } },
                            { "Name": "Bp_Bench", "TalentTree": { "RowName": "Crafting_T1" } },
                            { "Name": "Workshop_Suit", "TalentTree": { "RowName": "Workshop_Armor" } },
                            { "Name": "Wolf_Bite", "TalentTree": { "RowName": "Wolf_Tree" } },
                            { "Name": "Wolf_Howl", "TalentTree": { "RowName": "Wolf_Tree" } } ] }
                """);
            var node = new JsonObject
            {
                ["CharacterName"] = "TESTER",
                ["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Wolf_Howl", ["Rank"] = 1 }),
            };

            var vm = new SaveCharacterViewModel(node, new SaveGameNames(data), () => { });

            Assert.Equal(["Wolf_Howl", "Bp_Bench", "Survival_Gather"], vm.Talents.Select(t => t.RowName));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
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
