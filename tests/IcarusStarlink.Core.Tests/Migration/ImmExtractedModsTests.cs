using IcarusStarlink.Core.Migration;

namespace IcarusStarlink.Core.Tests.Migration;

public sealed class ImmExtractedModsTests
{
    // Shaped from the user's own real classic-IMM ExtractedMods.json (74 entries), including its
    // real quirks: a blank author, and a display name that differs from the folder name.
    private const string RealShapeJson = """
        {
          "mods": [
            { "name": "Bestiary", "displayname": "Bestiary", "author": "", "version": "1.1", "fileName": "Bestiary" },
            { "name": "Shengong_Invincible", "displayname": "Shengong_Invincible", "author": "BruteForce", "version": "1.4", "fileName": "BF_ShengongArmour_V1.4" },
            { "name": "Building Height and Strength", "displayname": "Building Height and Strength", "author": "Jimk72", "version": "2.8", "fileName": "Building_Height_Strength" }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsEveryFieldMigrationNeeds()
    {
        var mods = ImmExtractedMods.Parse(RealShapeJson);

        Assert.Equal(3, mods.Count);
        var shengong = mods[1];
        Assert.Equal("Shengong_Invincible", shengong.Name);
        Assert.Equal("BruteForce", shengong.Author);
        Assert.Equal("1.4", shengong.Version);
        Assert.Equal("BF_ShengongArmour_V1.4", shengong.FolderName);
    }

    [Fact]
    public void Find_MatchesByDisplayName_EvenWhenFolderNameDiffers()
    {
        // The whole reason this file is needed: a merged list says "Shengong_Invincible", but the
        // mod's folder on disk is "BF_ShengongArmour_V1.4".
        var mods = ImmExtractedMods.Parse(RealShapeJson);

        var found = ImmExtractedMods.Find(mods, "Shengong_Invincible");

        Assert.NotNull(found);
        Assert.Equal("BF_ShengongArmour_V1.4", found.FolderName);
    }

    [Fact]
    public void Find_AlsoMatchesByFolderName()
    {
        // Real merged lists mix conventions — some lines are already folder names.
        var mods = ImmExtractedMods.Parse(RealShapeJson);

        var found = ImmExtractedMods.Find(mods, "Building_Height_Strength");

        Assert.NotNull(found);
        Assert.Equal("Building Height and Strength", found.Name);
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var mods = ImmExtractedMods.Parse(RealShapeJson);

        Assert.NotNull(ImmExtractedMods.Find(mods, "bESTIARY"));
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        var mods = ImmExtractedMods.Parse(RealShapeJson);

        Assert.Null(ImmExtractedMods.Find(mods, "Something Never Installed"));
    }

    [Fact]
    public void Parse_SkipsEntriesWithNoFolderName()
    {
        // Without fileName there's nothing to copy from — a display name alone can't locate it.
        var mods = ImmExtractedMods.Parse("""{"mods":[{"name":"Ghost","author":"x","version":"1.0"}]}""");

        Assert.Empty(mods);
    }

    [Fact]
    public void Parse_MissingModsArray_ReturnsEmptyRatherThanThrowing()
    {
        Assert.Empty(ImmExtractedMods.Parse("{}"));
    }
}
