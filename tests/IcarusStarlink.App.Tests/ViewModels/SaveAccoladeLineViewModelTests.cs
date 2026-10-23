using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Accolades tab's cells (a tiered set as one cell) and the grouped lists' heading counts.</summary>
public sealed class SaveAccoladeLineViewModelTests
{
    private static SaveAccoladeViewModel Accolade(string name, string category = "Hunting", bool completed = false) =>
        new($"Row_{name.Replace(' ', '_')}", name, $"About {name}", category, completed, () => { });

    [Fact]
    public void Build_PutsATieredSetInOneCell_InTierOrder_AtTheFirstTiersPlace()
    {
        var lines = SaveAccoladeLineViewModel.Build(
        [
            Accolade("Wolf Killer II"), Accolade("Fish Killer I"), Accolade("Wolf Killer I"), Accolade("Wolf Killer III"), Accolade("Fish Killer II"),
        ]);

        Assert.Equal(["Wolf Killer", "Fish Killer"], lines.Select(l => l.Name));
        Assert.Equal(["I", "II", "III"], lines[0].Tiers.Select(t => t.Label));
        Assert.Equal(["Wolf Killer I", "Wolf Killer II", "Wolf Killer III"], lines[0].Tiers.Select(t => t.Accolade.DisplayName));
        Assert.True(lines[0].IsTiered);
        Assert.Null(lines[0].Single);
    }

    [Fact]
    public void Build_KeepsSeparate_ANumeralOnItsOwn_ARepeatedNumeral_AndOtherCategories()
    {
        var lines = SaveAccoladeLineViewModel.Build(
        [
            Accolade("Gotta Catch Them All II", "Achievement"),
            Accolade("Frozen I", "Survival"), Accolade("Frozen I", "Survival"),
            Accolade("Hunted I", "Survival"), Accolade("Hunted II", "Hunting"),
            Accolade("Friendly Fire", "Achievement"),
        ]);

        Assert.Equal(6, lines.Count);
        Assert.All(lines, l => Assert.False(l.IsTiered));
        Assert.Equal("Gotta Catch Them All II", lines[0].Name);
        Assert.Same(lines[5].Tiers[0].Accolade, lines[5].Single);
    }

    [Fact]
    public void ATierChip_SwitchesOnlyItsOwnAccolade_AndTheCellShowsItChanged()
    {
        var tiers = new[] { Accolade("Survivor I", completed: true), Accolade("Survivor II"), Accolade("Survivor III") };
        var line = Assert.Single(SaveAccoladeLineViewModel.Build(tiers));
        var raised = new List<string?>();
        line.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        line.Tiers[2].Accolade.IsCompleted = true;

        Assert.Equal([true, false, true], tiers.Select(t => t.IsCompleted));
        Assert.Equal([false, false, true], tiers.Select(t => t.IsDirty));
        Assert.True(line.IsDirty);
        Assert.Contains(nameof(SaveAccoladeLineViewModel.IsDirty), raised);
        Assert.True(line.AnyNotEarned);
    }

    [Fact]
    public void GroupCounts_SayHowManyOfTheGroupAreDone_InEachTabsOwnWord()
    {
        static SaveTalentViewModel Talent(string row, int rank, string model) =>
            new(row, new TalentDisplayInfo(row, "", "Tree", 3, false, "Group", model), rank, () => { });

        Assert.Equal("1 of 2 done", SaveGroupCounts.Describe([Talent("Prospect_A", 1, "Prospect"), Talent("Prospect_B", 0, "Prospect")]));
        Assert.Equal("1 of 2 researched", SaveGroupCounts.Describe([Talent("Workshop_A", 1, "Workshop"), Talent("Workshop_B", 0, "Workshop")]));
        Assert.Equal("2 learned", SaveGroupCounts.Describe([Talent("A", 1, "Player"), Talent("B", 2, "Player")]));
        Assert.Equal("1 of 2 learned", SaveGroupCounts.Describe([Talent("A", 1, "Player"), Talent("B", 0, "Player")]));

        var bestiary = new[]
        {
            new SaveBestiaryEntryViewModel("Wolf", "Wolf", 100, false, 100, null, () => { }, "Forest"),
            new SaveBestiaryEntryViewModel("Bear", "Bear", 100, false, 20, null, () => { }, "Forest"),
            new SaveBestiaryEntryViewModel("Old", "Old", 0, false, 5, null, () => { }),
        };
        Assert.Equal("1 of 2 complete", SaveGroupCounts.Describe(bestiary));

        var accolades = SaveAccoladeLineViewModel.Build([Accolade("Wolf Killer I", completed: true), Accolade("Wolf Killer II"), Accolade("Friendly Fire", completed: true)]);
        Assert.Equal("2 of 3 earned", SaveGroupCounts.Describe(accolades));

        Assert.Equal("", SaveGroupCounts.Describe([]));
    }

    [Fact]
    public void AMissionTheGameDoesntList_ReadsWithoutItsPrefixAndUnderscores()
    {
        var info = TalentDisplayInfo.Fallback("Prospect_OLY_Glacier_Exploration");

        Assert.Equal("OLY Glacier Exploration", info.DisplayName);
        Assert.False(info.IsInGameTables);
        Assert.Equal("Some_Mod_Talent", TalentDisplayInfo.Fallback("Some_Mod_Talent").DisplayName);
        Assert.Equal("Prospect_", TalentDisplayInfo.ReadableRowName("Prospect_"));

        var row = new SaveTalentViewModel("Prospect_OLY_Glacier_Exploration", info, 1, () => { });
        Assert.Equal("Prospect_OLY_Glacier_Exploration", row.RowName);
        Assert.False(row.IsInGameTables);
    }
}
