using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.PakIO.Tests.World;

/// <summary>
/// The kept copy of the drawn maps: made once, redrawn only when the game's files change, deleted
/// when the game drops a world, all redrawn by Rebuild all, and a one-line summary of what changed.
/// </summary>
public sealed class WorldMapStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}", "World");

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent))
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    /// <summary>Draws a 2x2 zone image and a tiny "picture"; its stamp per world can be changed.</summary>
    private sealed class FakeRenderer : IWorldMapRenderer
    {
        public Dictionary<string, string> Stamps { get; } = [];

        public List<string> Drawn { get; } = [];

        public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map)
        {
            Drawn.Add(map.Id);
            return map.PictureTiles.Count > 0 ? ([1, 2, 3], WorldPictureSource.Picture) : (null, WorldPictureSource.None);
        }

        public WorldLayerPixels? ReadLayer(string heatmapAssetPath) => new(2, [0x00FF90, 0x00FF90, 0, 0]);

        public string Stamp(WorldMapInfo map) => Stamps.GetValueOrDefault(map.Id, "1");
    }

    private static WorldMapInfo Map(string id, string name, params WorldZoneEntry[] creatures) =>
        new(id, name, false, [new WorldTile(0, 0, $"PROC/MapTextures/{id}/x0_y0.uasset")], [],
            [new WorldLayer(WorldLayerKind.Creatures, $"Heatmaps/{id}/GT.uasset",
                new Dictionary<uint, WorldZone> { [0x00FF90] = new("OLY_Conifer_Hard", "Conifer Hard", creatures) })]);

    private static WorldZoneEntry Wolf => new("Conifer_Wolf", "Wolf", 8);

    private static WorldZoneEntry DesertWolf => new("Desert_Wolf", "Desert Wolf", 4);

    [Fact]
    public void TheFirstReload_MakesEveryMap()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf), Map("Terrain_017", "Styx", Wolf)], renderer);

        var result = store.Reload(everything: false);

        Assert.Equal("Made 2 maps.", result.Summary);
        Assert.Equal(2, result.Redrawn);
        Assert.True(File.Exists(store.PicturePath("Terrain_016")));
        Assert.Equal([0x00FF90u, 0x00FF90, 0, 0], store.ReadLayer("Terrain_017", WorldLayerKind.Creatures)!.Pixels);
        Assert.Equal(WorldPictureSource.Picture, store.PictureSources()["Terrain_016"]);
    }

    [Fact]
    public void AReloadWithNothingChanged_DrawsNothing()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], renderer);
        store.Reload(everything: false);
        renderer.Drawn.Clear();

        var result = store.Reload(everything: false);

        Assert.Empty(renderer.Drawn);
        Assert.Equal("Nothing changed: the map is up to date.", result.Summary);
        Assert.False(store.Check().OutOfDate);
    }

    [Fact]
    public void AGameUpdateThatMovesAMapsFiles_RedrawsOnlyThatMap()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf), Map("Terrain_017", "Styx", Wolf)], renderer);
        store.Reload(everything: false);
        renderer.Drawn.Clear();
        renderer.Stamps["Terrain_017"] = "2";

        var check = store.Check();
        var result = store.Reload(everything: false);

        Assert.True(check.OutOfDate);
        Assert.Contains("Styx: map redrawn", check.Summary);
        Assert.Equal(["Terrain_017"], renderer.Drawn);
        Assert.Equal("Changed: Styx: map redrawn. 1 map unchanged.", result.Summary);
    }

    [Fact]
    public void AZoneThatGainsACreature_IsNamedInTheSummary_WithoutRedrawing()
    {
        var renderer = new FakeRenderer();
        var withDesertWolf = false;
        var store = new WorldMapStore(_root, () => [withDesertWolf ? Map("Terrain_016", "Olympus", Wolf, DesertWolf) : Map("Terrain_016", "Olympus", Wolf)], renderer);
        store.Reload(everything: false);
        renderer.Drawn.Clear();
        withDesertWolf = true;

        var result = store.Reload(everything: false);

        Assert.Empty(renderer.Drawn);
        Assert.Equal("Changed: Olympus: Conifer Hard: Desert Wolf added.", result.Summary);
        // Said once: the next Reload has nothing new to say.
        Assert.Equal("Nothing changed: the map is up to date.", store.Reload(everything: false).Summary);
    }

    [Fact]
    public void AWorldTheGameDropped_IsDeleted()
    {
        var renderer = new FakeRenderer();
        var maps = new List<WorldMapInfo> { Map("Terrain_016", "Olympus", Wolf), Map("Outpost_DEV", "Devland", Wolf) };
        var store = new WorldMapStore(_root, () => maps, renderer);
        store.Reload(everything: false);
        maps.RemoveAt(1);

        var result = store.Reload(everything: false);

        Assert.False(Directory.Exists(Path.GetDirectoryName(store.PicturePath("Outpost_DEV"))));
        Assert.Contains("Outpost_DEV: no longer in the game", result.Summary);
    }

    [Fact]
    public void RebuildAll_DeletesAndDrawsEveryMap_EvenWithNothingChanged()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf), Map("Terrain_017", "Styx", Wolf)], renderer);
        store.Reload(everything: false);
        var stray = Path.Combine(_root, "Terrain_016", "left-over.txt");
        File.WriteAllText(stray, "x");
        renderer.Drawn.Clear();

        var result = store.Reload(everything: true);

        Assert.Equal(["Terrain_016", "Terrain_017"], renderer.Drawn);
        Assert.False(File.Exists(stray));
        Assert.Equal("Rebuilt all 2 maps. Nothing had changed.", result.Summary);
    }

    [Fact]
    public void AMapWhoseFilesWereDeleted_IsDrawnAgain()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], renderer);
        store.Reload(everything: false);
        File.Delete(store.LayerPath("Terrain_016", WorldLayerKind.Creatures));
        renderer.Drawn.Clear();

        Assert.True(store.Check().OutOfDate);
        store.Reload(everything: false);

        Assert.Equal(["Terrain_016"], renderer.Drawn);
    }

    [Fact]
    public void NeverMade_IsOutOfDate()
    {
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], new FakeRenderer());

        var check = store.Check();

        Assert.True(check.OutOfDate);
        Assert.Empty(store.PictureSources());
    }

    [Fact]
    public void ZoneImages_KeepTheirExactColours_ThroughThePng()
    {
        var pixels = new WorldLayerPixels(2, [0x00E0A0, 0xE06500, 0x0BE000, 0xFFFFFF]);

        var back = WorldLayerPixels.FromPng(pixels.ToPng());

        Assert.Equal(pixels.Pixels, back!.Pixels);
    }

    [Theory]
    [InlineData(0, 0, 8, 0, 7)]
    [InlineData(0, 7, 8, 0, 0)]
    [InlineData(5, 2, 8, 5, 5)]
    [InlineData(1, 0, 2, 1, 1)]
    public void Tiles_GoToColumnX_AndRowNMinus1MinusY(int x, int y, int n, int column, int row) =>
        Assert.Equal((column, row), WorldTilePlacement.Place(x, y, n));

    [Fact]
    public void Tiles_AreTurnedAQuarterClockwise()
    {
        // The tile's top-left pixel shows at the top right, its bottom-left at the top left.
        Assert.Equal((0, 0), WorldTilePlacement.SourcePixel(3, 0, 4));
        Assert.Equal((0, 3), WorldTilePlacement.SourcePixel(0, 0, 4));
        Assert.Equal((3, 0), WorldTilePlacement.SourcePixel(3, 3, 4));
    }
}
