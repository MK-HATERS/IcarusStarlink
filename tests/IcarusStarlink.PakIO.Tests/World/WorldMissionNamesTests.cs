using System.Text;
using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.PakIO.Tests.World;

/// <summary>Mission spots named the way players see them, from small copies of D_ProspectList and D_Quests.</summary>
public sealed class WorldMissionNamesTests
{
    private static IReadOnlyDictionary<string, string> Names()
    {
        static JsonObject Prospect(string name, string dropName, string terrain = "Terrain_016") => new()
        {
            ["Name"] = name,
            ["DropName"] = $"NSLOCTEXT(\"D_ProspectList\", \"{name}-DropName\", \"{dropName}\")",
            ["Terrain"] = new JsonObject { ["RowName"] = terrain },
        };

        var tables = new Dictionary<string, JsonObject>
        {
            ["Prospects/D_ProspectList.json"] = new()
            {
                ["Rows"] = new JsonArray(
                    Prospect("Tier1_Forest_Range", "POTSHOT"),
                    Prospect("Tier1_Forest_WS_Stockpile", "SPELUNKING"),
                    Prospect("Tier1_Forest_Defence", "FORSAKEN"),
                    Prospect("Tier4_Desert_Extermination_0", "DUST UP"),
                    Prospect("Tier4_Desert_Extermination_2", "LOOSE ENDS"),
                    Prospect("Tier3_RiverLands_Stockpile_2", "BIG SHOT"),
                    Prospect("GH_RG_B", "HABITAT"),
                    Prospect("OLY_SQ_Norex", "BLACK MARKET"),
                    Prospect("Tier1_Forest_Recon_0", "ON ANOTHER MAP", "Terrain_017")),
            },
            ["Quests/D_Quests.json"] = new()
            {
                ["Rows"] = new JsonArray(new JsonObject
                {
                    ["Name"] = "BW5_Quest",
                    ["Description"] = "NSLOCTEXT(\"D_Quests\", \"BW5_Quest-Description\", \"Blacksand\")",
                }),
            },
        };

        return WorldMissionNames.Read(
            new WorldTableSource(p => tables.TryGetValue(p, out var t) ? Encoding.UTF8.GetBytes(t.ToJsonString()) : null, _ => null),
            "Terrain_016");
    }

    [Theory]
    [InlineData("Faction Mission · Forest", "Range · Bear · Popup", "POTSHOT (Forest range)", "Bear · Popup")]
    [InlineData("Faction Mission · Forest", "WS Stockpile · Titanium Voxel2", "SPELUNKING (Forest ws stockpile)", "Titanium Voxel2")]
    [InlineData("Faction Mission · Forest", "Defense · Boss", "FORSAKEN (Forest defense)", "Boss")]
    [InlineData("Factionmission · Desert", "Extermination · Clue1", "DUST UP or LOOSE ENDS (Desert extermination)", "Clue1")]
    [InlineData("Faction Mission · Riverlands", "Stockpile2 · Ship", "BIG SHOT (Riverlands stockpile 2)", "Ship")]
    [InlineData("Great Hunts · Rock Golem", "B · Cave3", "HABITAT (Rock Golem hunt)", "Cave3")]
    [InlineData("Faction Mission · NPC", "Norex · Land", "BLACK MARKET (side mission)", "Land")]
    [InlineData("Objective · BW5", "Cave Exit", "Blacksand (story mission)", "Cave Exit")]
    [InlineData("Objective · BW6", "Extermination · Boss", "Story mission (BW6)", "Extermination · Boss")]
    [InlineData("Objective · Base Location", "Base Location", "Mission objective spots: base location", "Used by many missions")]
    [InlineData("Faction Mission · Styx", "River · Large Lake", "Styx mission spots: River", "Large Lake")]
    [InlineData("Faction Mission · Recon", "Loc1", "Recon mission spots", "Loc1")]
    [InlineData("Other", "Quest Marker91", "Other quest spots", "Quest Marker91")]
    public void MissionSpots_AreNamedAsPlayersSeeThem(string category, string name, string expectedCategory, string expectedSpot)
    {
        var (described, spot) = WorldMissionNames.Describe(category, name, Names());

        Assert.Equal(expectedCategory, described);
        Assert.Equal(expectedSpot, spot);
    }

    [Fact]
    public void OnlyThisWorldsProspects_AreUsed() =>
        Assert.DoesNotContain(Names().Values, v => v.Contains("ANOTHER MAP", StringComparison.Ordinal));
}
