using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Saves page's grouped lists show groups in the order their first row comes, so the row order is the group order.</summary>
public sealed class SaveGroupOrderTests
{
    private static SaveTalentViewModel Talent(string name, string group, int groupOrder, int order = int.MaxValue) =>
        new(name, new TalentDisplayInfo(name, "", "Tree", 1, false, group, "Blueprint", GroupOrder: groupOrder, Order: order), 0, () => { });

    /// <summary>Blueprints listed alphabetically put "Tier 10" before "Tier 2" and a mod's tree anywhere; now by the tables' tier order, rows by name, unplaced groups last by name.</summary>
    [Fact]
    public void Talents_GroupsInTierOrder_RowsByName_UnplacedLast()
    {
        var rows = new[]
        {
            Talent("Zeta", "Tier 2 · Crafting Bench", 2),
            Talent("Mod row", "Not in the game's tables", int.MaxValue),
            Talent("Beta", "Tier 10 · Mod Bench", 10),
            Talent("Alpha", "Tier 2 · Crafting Bench", 2),
            Talent("Knife", "Tier 1 · Character crafting", 1),
            Talent("Other mod", "A mod tree", int.MaxValue),
        };

        Assert.Equal(["Knife", "Alpha", "Zeta", "Beta", "Other mod", "Mod row"], SaveGroupOrder.Talents(rows).Select(t => t.DisplayName));
    }

    /// <summary>Missions keep the game's own order inside a map (its first area first), not A to Z.</summary>
    [Fact]
    public void Missions_KeepTheGamesOrderInsideAMap()
    {
        var rows = new[]
        {
            Talent("Canyon Scan", "Olympus", 0, order: 7),
            Talent("Elysium Start", "Elysium", 3, order: 1),
            Talent("Forest Recon", "Olympus", 0, order: 2),
        };

        Assert.Equal(["Forest Recon", "Canyon Scan", "Elysium Start"], SaveGroupOrder.Missions(rows).Select(t => t.DisplayName));
    }

    [Fact]
    public void Bestiary_BiomesInOrder_CreaturesKeepTheirOrder_UnknownLast()
    {
        SaveBestiaryEntryViewModel Creature(string name, string group, int order) => new(name, name, 100, false, 0, null, () => { }, group, order);
        var rows = new[]
        {
            Creature("Polar Bear", "Arctic", 2),
            new SaveBestiaryEntryViewModel("Old_Row", "Old_Row", 0, false, 5, null, () => { }),
            Creature("Wolf", "Forest", 1),
            Creature("Deer", "Forest", 1),
        };

        Assert.Equal(["Wolf", "Deer", "Polar Bear", "Old_Row"], SaveGroupOrder.Bestiary(rows).Select(b => b.DisplayName));
    }
}
