using System.Text.Json;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.PakIO.Tests.World;

/// <summary>Cave tiers by cave worms, mission categories from tags, the grid, and the features' round trip through the kept copy.</summary>
public sealed class WorldFeatureTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    [InlineData(10, 3)]
    public void CaveTier_GoesByTheMostCaveWorms(int wormsMax, int tier) =>
        Assert.Equal(tier, new WorldFeature(WorldFeatureKind.Cave, "Cave", new(0, 0)) { WormsMin = 0, WormsMax = wormsMax }.Tier);

    [Fact]
    public void ACaveWithoutASpawner_HasNoTier() =>
        Assert.Null(new WorldFeature(WorldFeatureKind.Cave, "Cave", new(0, 0)).Tier);

    [Theory]
    [InlineData("Objective.BaseLocation", "Objective · Base Location")]
    [InlineData("FactionMission.Forest.Hunt", "Faction Mission · Forest")]
    [InlineData("GreatHunts.RockGolem.B.Cave3", "Great Hunts · Rock Golem")]
    [InlineData(null, "Other")]
    public void MissionCategories_AreTheTagsFirstTwoParts(string? tag, string category) =>
        Assert.Equal(category, WorldFeatureReader.MissionCategory(tag));

    [Fact]
    public void TheGrid_Is50400UnitsASquare()
    {
        var olympus = new WorldMapInfo("Terrain_016", "Olympus", false, [], [], [], Extent: new WorldExtent(-403200, -403200, 806400, 806400));
        var outpost = new WorldMapInfo("Outpost_013", "Outpost 013", true, [], [], [], Extent: new WorldExtent(-151200, -151200, 302400, 302400));

        Assert.Equal((16, 16), olympus.GridSize);
        Assert.Equal((6, 6), outpost.GridSize);
        Assert.Equal((0, 0), new WorldMapInfo("X", "X", true, [], [], []).GridSize);
    }

    [Fact]
    public void WorldPositions_GoOnTheMap_XRightYDown()
    {
        var extent = new WorldExtent(-403200, -403200, 806400, 806400);

        // A real exotic spot, "OLY_I7": column I (the 9th of 16), row 7.
        var spot = extent.ToMap(18416, -67147);

        Assert.Equal(8, (int)(spot.U * 16));
        Assert.Equal(6, (int)(spot.V * 16));
    }

    [Fact]
    public void Features_SurviveTheKeptCopy()
    {
        var features = new WorldFeatures(
            [new WorldFeature(WorldFeatureKind.BigCave, "Big cave", new(0.2, 0.6)) { WormsMin = 4, WormsMax = 6, LengthMetres = 930, Style = "DC", Entrances = [new(0.1, 0.5), new(0.3, 0.7)] }],
            [new WorldWater(true, [new(0, 0), new(1, 0), new(1, 1)])]);

        var back = JsonSerializer.Deserialize<WorldFeatures>(JsonSerializer.Serialize(features))!;

        var cave = Assert.Single(back.Features);
        Assert.Equal(WorldFeatureKind.BigCave, cave.Kind);
        Assert.Equal(2, cave.Tier);
        Assert.Equal([new WorldMapPoint(0.1, 0.5), new WorldMapPoint(0.3, 0.7)], cave.Entrances);
        Assert.True(Assert.Single(back.Water).IsRiver);
    }
}
