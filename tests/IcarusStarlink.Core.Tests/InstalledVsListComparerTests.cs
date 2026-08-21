using IcarusStarlink.Core.InstallComparison;

namespace IcarusStarlink.Core.Tests;

public class InstalledVsListComparerTests
{
    [Fact]
    public void Compare_ModInBothLists_IsUnchanged()
    {
        var result = InstalledVsListComparer.Compare(["Mod A"], ["Mod A"], [], []);

        var entry = Assert.Single(result);
        Assert.Equal("Mod A", entry.Name);
        Assert.Equal(InstalledComparisonState.Unchanged, entry.State);
        Assert.False(entry.IsUe4ss);
    }

    [Fact]
    public void Compare_ModOnlyInQueuedList_IsAdded()
    {
        var result = InstalledVsListComparer.Compare([], ["New Mod"], [], []);

        var entry = Assert.Single(result);
        Assert.Equal("New Mod", entry.Name);
        Assert.Equal(InstalledComparisonState.Added, entry.State);
    }

    [Fact]
    public void Compare_ModOnlyInInstalledList_IsRemoved()
    {
        var result = InstalledVsListComparer.Compare(["Old Mod"], [], [], []);

        var entry = Assert.Single(result);
        Assert.Equal("Old Mod", entry.Name);
        Assert.Equal(InstalledComparisonState.Removed, entry.State);
    }

    [Fact]
    public void Compare_NameMatchIsCaseInsensitive()
    {
        var result = InstalledVsListComparer.Compare(["mod a"], ["Mod A"], [], []);

        var entry = Assert.Single(result);
        Assert.Equal(InstalledComparisonState.Unchanged, entry.State);
    }

    [Fact]
    public void Compare_Ue4ssModsComparedSeparatelyFromPakMods_AndFlaggedAsUe4ss()
    {
        var result = InstalledVsListComparer.Compare(["Pak Mod"], ["Pak Mod"], [], ["NearbyCrafting"]);

        Assert.Equal(2, result.Count);
        var ue4ssEntry = Assert.Single(result, e => e.IsUe4ss);
        Assert.Equal("NearbyCrafting", ue4ssEntry.Name);
        Assert.Equal(InstalledComparisonState.Added, ue4ssEntry.State);
    }

    [Fact]
    public void Compare_MixOfAddedRemovedAndUnchanged_ReturnsAllThree()
    {
        var result = InstalledVsListComparer.Compare(
            installedModNames: ["Mod A", "Mod B"], queuedModNames: ["Mod A", "Mod C"],
            installedUe4ssModNames: [], attachedUe4ssModNames: []);

        Assert.Equal(3, result.Count);
        Assert.Equal(InstalledComparisonState.Unchanged, result.Single(e => e.Name == "Mod A").State);
        Assert.Equal(InstalledComparisonState.Removed, result.Single(e => e.Name == "Mod B").State);
        Assert.Equal(InstalledComparisonState.Added, result.Single(e => e.Name == "Mod C").State);
    }

    [Fact]
    public void Compare_BothListsEmpty_ReturnsEmpty()
    {
        Assert.Empty(InstalledVsListComparer.Compare([], [], [], []));
    }

    [Fact]
    public void Compare_DuplicateNameWithinTheSameList_ReturnedOnlyOnce()
    {
        var result = InstalledVsListComparer.Compare([], ["Mod A", "Mod A"], [], []);

        Assert.Single(result);
    }
}
