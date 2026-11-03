using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>"Add stat" refuses a stat the game is known (tested in game) to ignore where it would go, saying why.</summary>
public sealed class KnownGameBehaviourEditorTests
{
    private static readonly StatChoice SpoilRate = new(KnownGameBehaviour.SpoilRateStat, "Spoil rate", "percent bonus");
    private static readonly StatChoice Slots = new("BaseInventorySlots_+", "Inventory slots", "flat amount");
    private static readonly StatChoice HealthAura = new("GrantedAuraHealthRegen_?", "Health regen aura", "flat amount");

    /// <summary>The Editor page's Storage card: a storage's spoil-rate stat changes nothing (Hook Scout, 2026-10-23); another stat goes in.</summary>
    [Fact]
    public void AStorageCard_RefusesTheSpoilRateStat()
    {
        var section = new EditableSection("Storage:0", "Storage", "Inventory-D_InventoryInfo.json", "Food_Bag", "Food_Bag", 1, [], ["Stats"]);
        var card = new GameItemSectionViewModel(section, [SpoilRate, Slots], () => { });

        card.Picker.SelectedStat = SpoilRate;
        card.Picker.AddStatCommand.Execute(null);

        Assert.StartsWith("Changes nothing", card.Picker.StatError);
        Assert.Empty(card.Fields);

        card.Picker.SelectedStat = Slots;
        card.Picker.AddStatCommand.Execute(null);
        Assert.Null(card.Picker.StatError);
        Assert.Single(card.Fields);
    }

    /// <summary>The mod editor's stats list asks its row (here: an aura stat on a placed item) before adding.</summary>
    [Fact]
    public void TheModEditorsStatsList_RefusesAStatItsRowIgnores()
    {
        var fields = new Dictionary<string, JsonNode?> { ["AdditionalStats"] = new JsonObject { ["(Value=\"BaseWeightCapacity_+\")"] = 5 } };
        var field = new EditorFieldViewModel("AdditionalStats", fields, new JsonObject { ["(Value=\"BaseWeightCapacity_+\")"] = 5 }, null, [HealthAura, Slots],
            statWithNoEffect: stat => KnownGameBehaviour.StatWithNoEffect("D_ItemsStatic", "AdditionalStats", stat, placedItem: true));

        field.Picker!.SelectedStat = HealthAura;
        field.Picker.AddStatCommand.Execute(null);

        Assert.Contains("no aura", field.Picker.StatError);
        Assert.DoesNotContain(field.Stats, s => s.Key.Contains("GrantedAura", StringComparison.Ordinal));
    }
}
