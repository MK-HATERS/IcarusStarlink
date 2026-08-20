using IcarusStarlink.Core.Library;

namespace IcarusStarlink.Core.Tests;

public class VariantGroupingTests
{
    private static LibraryEntry MakeEntry(
        string fileName, string name, string? variantGroup = null, string? variant = null, int? variantSort = null) => new()
    {
        FolderName = fileName,
        Name = name,
        Author = "A",
        Version = "1",
        Description = "D",
        FileName = fileName,
        VariantGroup = variantGroup,
        Variant = variant,
        VariantSort = variantSort,
    };

    [Fact]
    public void Group_SingleUnrelatedEntries_EachStaysStandalone()
    {
        var entries = new[] { MakeEntry("A", "Alpha"), MakeEntry("B", "Beta") };

        var groups = VariantGrouping.Group(entries);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsFamily));
    }

    [Fact]
    public void Group_TwoEntriesWithSameExplicitVariantGroup_FormsAFamily()
    {
        var entries = new[]
        {
            MakeEntry("Take_Home_Tools", "Take Home Tools", variantGroup: "Take_Home", variant: "Tools"),
            MakeEntry("Take_Home_Almost_All", "Take Home Almost All", variantGroup: "Take_Home", variant: "Almost All"),
        };

        var groups = VariantGrouping.Group(entries);

        var family = Assert.Single(groups);
        Assert.True(family.IsFamily);
        Assert.Equal(2, family.Entries.Count);
        Assert.Equal("Take Home", family.DisplayName);
    }

    [Fact]
    public void Group_ExplicitVariantGroup_IsCaseInsensitive()
    {
        var entries = new[]
        {
            MakeEntry("A", "A", variantGroup: "Take_Home"),
            MakeEntry("B", "B", variantGroup: "take_home"),
        };

        var groups = VariantGrouping.Group(entries);

        Assert.Single(groups);
    }

    [Fact]
    public void Group_LoneEntryWithVariantGroup_IsNotTreatedAsAFamily()
    {
        var entries = new[] { MakeEntry("A", "Solo", variantGroup: "OnlyOne") };

        var groups = VariantGrouping.Group(entries);

        var group = Assert.Single(groups);
        Assert.False(group.IsFamily);
    }

    [Fact]
    public void Group_FileNameSuffixGuessing_GroupsMultiplierVariants()
    {
        var entries = new[]
        {
            MakeEntry("MyMod_2x", "My Mod 2x"),
            MakeEntry("MyMod_5x", "My Mod 5x"),
            MakeEntry("MyMod_10x", "My Mod 10x"),
        };

        var groups = VariantGrouping.Group(entries);

        var family = Assert.Single(groups);
        Assert.True(family.IsFamily);
        Assert.Equal(3, family.Entries.Count);
        Assert.Equal("MyMod", family.DisplayName);
    }

    [Fact]
    public void Group_MultiplierVariants_SortNumericallyNotAlphabetically()
    {
        var entries = new[]
        {
            MakeEntry("MyMod_10x", "MyMod_10x"),
            MakeEntry("MyMod_2x", "MyMod_2x"),
            MakeEntry("MyMod_5x", "MyMod_5x"),
        };

        var groups = VariantGrouping.Group(entries);

        var family = Assert.Single(groups);
        Assert.Equal(["MyMod_2x", "MyMod_5x", "MyMod_10x"], family.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Group_ExplicitVariantSort_TakesPriorityOverAlphabeticalOrNumericGuessing()
    {
        var entries = new[]
        {
            MakeEntry("A", "Almost All", variantGroup: "G", variant: "Almost All", variantSort: 2),
            MakeEntry("B", "Tools", variantGroup: "G", variant: "Tools", variantSort: 1),
        };

        var groups = VariantGrouping.Group(entries);

        var family = Assert.Single(groups);
        Assert.Equal(["B", "A"], family.Entries.Select(e => e.FolderName));
    }

    [Fact]
    public void Group_SameModReimportedTwice_DoesNotMergeIntoASpuriousFamily()
    {
        // Re-importing a mod produces two entries sharing the same FileName (the mod's own
        // internal identity) but different FolderName (disambiguated on collision) — these must
        // not be treated as variants of each other.
        LibraryEntry MakeReimport(string folderName) => new()
        {
            FolderName = folderName, Name = "Faster Processors", Author = "A", Version = "1", Description = "D",
            FileName = "Faster_Processors",
        };

        var entries = new[] { MakeReimport("Faster_Processors"), MakeReimport("Faster_Processors_2") };

        var groups = VariantGrouping.Group(entries);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsFamily));
    }

    [Fact]
    public void Group_DifferentFilesEditedTogether_DoNotAccidentallyGroup()
    {
        var entries = new[] { MakeEntry("TakeHomeTools", "Take Home Tools"), MakeEntry("BagSize", "Bigger Bags") };

        var groups = VariantGrouping.Group(entries);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.False(g.IsFamily));
    }
}
