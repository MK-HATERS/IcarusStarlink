using IcarusStarlink.Core.Library;

namespace IcarusStarlink.Core.Tests.Library;

public class ModListTests
{
    private static LibraryEntry MakeEntry(string folderName, string name, string? fileName = null) => new()
    {
        FolderName = folderName, Name = name, Author = "A", Version = "1", Description = "", FileName = fileName ?? folderName,
    };

    [Fact]
    public void ParseNames_RealManifestShape_SkipsHeaderAndBlankLines()
    {
        // The exact shape of the user's own real LastMergedMods.txt / IMM_Merged_Mod.txt.
        var content = "Includes the following mods:\r\nChance-LitSigns\r\nFood Buff Duration - 2x\r\n\r\n";

        Assert.Equal(["Chance-LitSigns", "Food Buff Duration - 2x"], ModListText.ParseNames(content));
    }

    [Fact]
    public void ParseNames_NoHeaderAtAll_EveryLineIsAName()
    {
        Assert.Equal(["Mod A", "Mod B"], ModListText.ParseNames("Mod A\nMod B"));
    }

    /// <summary>
    /// Regression guard: an options-only build (RebuildService.WriteManifest) appends a "Gameplay
    /// options applied:" section after the mods — an option description like "Stacks x2" must never
    /// be fed into ParseNames' own mod-name diffing logic (InstalledState.ModNames/
    /// InstalledVsListComparer).
    /// </summary>
    [Fact]
    public void ParseNames_ManifestWithGameplayOptionsSectionAfterMods_StopsAtOptionsHeader()
    {
        var content = "Includes the following mods:\r\nMod_A\r\nGameplay options applied:\r\nStacks x2\r\nRemove Weight\r\n";

        Assert.Equal(["Mod_A"], ModListText.ParseNames(content));
    }

    [Fact]
    public void ParseNames_OptionsOnlyManifest_NoModsSectionAtAll_ReturnsEmpty()
    {
        // The real shape RebuildService.WriteManifest now produces for a build with an empty queue
        // and no prebuilt paks but at least one gameplay option enabled.
        var content = "Gameplay options applied:\r\nStacks x2\r\n";

        Assert.Empty(ModListText.ParseNames(content));
    }

    [Fact]
    public void ParseOptionDescriptions_ManifestWithBothSections_ReturnsOnlyTheOptionsLines()
    {
        var content = "Includes the following mods:\r\nMod_A\r\nGameplay options applied:\r\nStacks x2\r\nRemove Weight\r\n";

        Assert.Equal(["Stacks x2", "Remove Weight"], ModListText.ParseOptionDescriptions(content));
    }

    [Fact]
    public void ParseOptionDescriptions_NoOptionsSection_ReturnsEmpty()
    {
        var content = "Includes the following mods:\r\nMod_A\r\n";

        Assert.Empty(ModListText.ParseOptionDescriptions(content));
    }

    [Fact]
    public void Match_DisplayNameAndFolderStyleNamesMixed_BothMatch()
    {
        // Classic IMM's real lists mix both — confirmed against the user's own file.
        var entries = new[]
        {
            MakeEntry("Food_Buff_2x", "Food Buff Duration - 2x"),
            MakeEntry("Coracks_Ammo_and_Repair_x100", "Coracks Ammo and Repair"),
        };

        var result = ModListMatcher.Match(["Food Buff Duration - 2x", "Coracks_Ammo_and_Repair_x100"], entries);

        Assert.Equal(["Food_Buff_2x", "Coracks_Ammo_and_Repair_x100"], result.Matched.Select(e => e.FolderName));
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Match_UnknownName_ReportedAsUnmatchedNotSilentlyDropped()
    {
        var result = ModListMatcher.Match(["Not In Library"], [MakeEntry("Some_Mod", "Some Mod")]);

        Assert.Empty(result.Matched);
        Assert.Equal(["Not In Library"], result.Unmatched);
    }

    [Fact]
    public void Match_PreservesListOrderAsMergePriority()
    {
        var entries = new[] { MakeEntry("A", "Mod A"), MakeEntry("B", "Mod B"), MakeEntry("C", "Mod C") };

        var result = ModListMatcher.Match(["Mod C", "Mod A", "Mod B"], entries);

        Assert.Equal(["C", "A", "B"], result.Matched.Select(e => e.FolderName));
    }

    [Fact]
    public void Match_TwoNamesResolvingToTheSameEntry_QueuesItOnce()
    {
        var entries = new[] { MakeEntry("Same_Mod", "Same Mod") };

        var result = ModListMatcher.Match(["Same Mod", "Same_Mod"], entries);

        Assert.Single(result.Matched);
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void Match_FileNameFallback_MatchesWhenNameAndFolderDiffer()
    {
        var entries = new[] { MakeEntry("Renamed_Folder_2", "My Display Name", fileName: "Original_FileName") };

        var result = ModListMatcher.Match(["Original_FileName"], entries);

        Assert.Single(result.Matched);
    }
}
