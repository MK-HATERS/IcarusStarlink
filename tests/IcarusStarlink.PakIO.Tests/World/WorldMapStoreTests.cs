using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.PakIO.Tests.World;

/// <summary>
/// The kept copy of the drawn maps: made once, redrawn only when the game's files change, deleted
/// when the game drops a world, all redrawn by Rebuild all, one that can't be drawn left for the next
/// Reload, look-alike ids kept in their own folders, and a one-line summary of what changed.
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

        /// <summary>Worlds whose drawing throws (a mod's odd tiles, a locked pak).</summary>
        public HashSet<string> Broken { get; } = [];

        /// <summary>The picture is the world's id as bytes, so a test can tell whose picture a file is.</summary>
        public bool PictureIsId { get; init; }

        /// <summary>
        /// False: the game's files can't be read (the game folder moved, its drive unplugged). Like the
        /// real renderer then, nothing is found and every stamp changes.
        /// </summary>
        public bool Readable { get; set; } = true;

        public bool CanReadGame() => Readable;

        public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map)
        {
            Drawn.Add(map.Id);
            if (Broken.Contains(map.Id))
            {
                throw new IOException("The pak is locked.");
            }

            return map.PictureTiles.Count > 0 && Readable
                ? (PictureIsId ? System.Text.Encoding.UTF8.GetBytes(map.Id) : [1, 2, 3], WorldPictureSource.Picture)
                : (null, WorldPictureSource.None);
        }

        public WorldLayerPixels? ReadLayer(string heatmapAssetPath) => Readable ? new(2, [0x00FF90, 0x00FF90, 0, 0]) : null;

        public string Stamp(WorldMapInfo map) => Readable ? Stamps.GetValueOrDefault(map.Id, "1") : "unreadable";

        /// <summary>The spots each world has (none unless set).</summary>
        public Dictionary<string, WorldFeatures> Features { get; } = [];

        public WorldFeatures ReadFeatures(WorldMapInfo map) => Features.GetValueOrDefault(map.Id, WorldFeatures.None);
    }

    private static readonly DateTime Today = new(2030, 5, 17, 9, 30, 0, DateTimeKind.Utc);

    private static WorldFeature Cave(double u, int worms = 5) => new(WorldFeatureKind.Cave, "Cave", new(u, 0.5)) { WormsMin = 1, WormsMax = worms };

    private static WorldFeature Geyser(double u) => new(WorldFeatureKind.OilGeyser, "Oil geyser", new(u, 0.25));

    /// <summary>Throws (as closing Starlink would stop it) on the given call: Reload reports just before each world it draws.</summary>
    private sealed class StopAt(int call) : IProgress<string>
    {
        private int _calls;

        public void Report(string value)
        {
            if (++_calls == call)
            {
                throw new OperationCanceledException();
            }
        }
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
    public void AMapThatCantBeDrawn_DoesntStopTheOthers_AndIsTriedAgainNextTime()
    {
        var renderer = new FakeRenderer();
        renderer.Broken.Add("Terrain_016");
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf), Map("Terrain_017", "Styx", Wolf)], renderer);

        // Rebuild all: one broken world mustn't leave the rest undrawn and no index.
        var result = store.Reload(everything: true);

        Assert.Equal(1, result.Redrawn);
        Assert.Contains("Couldn't draw Olympus (The pak is locked.)", result.Summary);
        Assert.True(File.Exists(store.PicturePath("Terrain_017")));
        Assert.False(store.PictureSources().ContainsKey("Terrain_016"));
        Assert.Equal(WorldPictureSource.Picture, store.PictureSources()["Terrain_017"]);
        Assert.True(store.Check().OutOfDate);

        renderer.Broken.Clear();
        renderer.Drawn.Clear();
        var again = store.Reload(everything: false);

        Assert.Equal(["Terrain_016"], renderer.Drawn);
        Assert.Equal(1, again.Redrawn);
        Assert.False(store.Check().OutOfDate);
    }

    [Fact]
    public void WorldsWhoseIdsLookAlikeAsFolders_KeepTheirOwnPictures()
    {
        var renderer = new FakeRenderer { PictureIsId = true };
        var store = new WorldMapStore(_root, () => [Map("Mod.World", "A", Wolf), Map("Mod_World", "B", Wolf)], renderer);

        store.Reload(everything: false);

        Assert.NotEqual(store.PicturePath("Mod.World"), store.PicturePath("Mod_World"));
        Assert.Equal("Mod.World", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.PicturePath("Mod.World"))));
        Assert.Equal("Mod_World", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.PicturePath("Mod_World"))));
        // A plain row name keeps its plain folder.
        Assert.Equal(Path.Combine(_root, "Mod_World", "picture.jpg"), store.PicturePath("Mod_World"));
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

    /// <summary>
    /// Regression: stamps used to name the pak and place of each file, so a game update that only
    /// repacked its paks redrew every map. Maps drawn under those stamps keep their drawing the first
    /// time the new stamps are used, and take the new stamp.
    /// </summary>
    [Fact]
    public void MapsDrawnUnderOldStamps_AreKeptWithoutDrawing_AndZoneChangesAreStillSaid()
    {
        var renderer = new FakeRenderer();
        var withDesertWolf = false;
        var store = new WorldMapStore(_root,
            () => [withDesertWolf ? Map("Terrain_016", "Olympus", Wolf, DesertWolf) : Map("Terrain_016", "Olympus", Wolf), Map("Terrain_017", "Styx", Wolf)], renderer);
        renderer.Stamps["Terrain_016"] = "0123456789ABCDEF0123456789ABCDEF";
        renderer.Stamps["Terrain_017"] = "FEDCBA9876543210FEDCBA9876543210";
        store.Reload(everything: false);
        renderer.Drawn.Clear();
        renderer.Stamps["Terrain_016"] = "c2:AAAA";
        renderer.Stamps["Terrain_017"] = "c2:BBBB";

        Assert.False(store.Check().OutOfDate);
        withDesertWolf = true;
        var check = store.Check();
        var result = store.Reload(everything: false);

        Assert.DoesNotContain("redrawn", check.Summary);
        Assert.Empty(renderer.Drawn);
        Assert.Equal(0, result.Redrawn);
        Assert.Equal("Changed: Olympus: Conifer Hard: Desert Wolf added. 1 map unchanged.", result.Summary);

        // The new stamps were stored: from now a changed file redraws its map, and only that one.
        renderer.Stamps["Terrain_017"] = "c2:CCCC";
        var next = store.Reload(everything: false);

        Assert.Equal(["Terrain_017"], renderer.Drawn);
        Assert.Equal("Changed: Styx: map redrawn. 1 map unchanged.", next.Summary);
    }

    /// <summary>A world whose last drawing failed has no stamp: it's drawn, never taken as it is.</summary>
    [Fact]
    public void AMapWhoseLastDrawingFailed_IsStillDrawn_WhenTheStampsChangeScheme()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], renderer);
        store.Reload(everything: false);
        renderer.Stamps["Terrain_016"] = "2";
        renderer.Broken.Add("Terrain_016");
        store.Reload(everything: false);
        renderer.Broken.Clear();
        renderer.Drawn.Clear();
        renderer.Stamps["Terrain_016"] = "c2:AAAA";

        store.Reload(everything: false);

        Assert.Equal(["Terrain_016"], renderer.Drawn);
    }

    [Fact]
    public void AChangedWorld_RecordsItsZoneChanges_AndTheSpotsThatCameAndWent()
    {
        var renderer = new FakeRenderer();
        var withDesertWolf = false;
        var store = new WorldMapStore(_root,
            () => [withDesertWolf ? Map("Terrain_016", "Olympus", Wolf, DesertWolf) : Map("Terrain_016", "Olympus", Wolf), Map("Terrain_017", "Styx", Wolf)],
            renderer, () => Today);
        renderer.Features["Terrain_016"] = new([Cave(0.1), Geyser(0.2)], []);
        store.Reload(everything: false);

        // A first drawing records nothing: every spot would read as new.
        Assert.Empty(store.ChangeRecords());

        renderer.Stamps["Terrain_016"] = "2";
        renderer.Features["Terrain_016"] = new([Cave(0.1), Cave(0.7, worms: 9)], []);
        withDesertWolf = true;
        store.Reload(everything: false);

        var records = store.ChangeRecords();
        var record = Assert.Single(records).Value;
        Assert.Equal("Terrain_016", records.Keys.Single());
        Assert.Equal(Today, record.When);
        Assert.Equal([new WorldZoneChange("Creatures", "OLY_Conifer_Hard", "Conifer Hard", "Desert Wolf added")], record.Zones);
        Assert.Equal([Cave(0.7, worms: 9)], record.Added);
        Assert.Equal([Geyser(0.2)], record.Removed);
        Assert.Equal((1, 1), (record.AddedCount, record.RemovedCount));
    }

    [Fact]
    public void AZoneChangeWithoutARedraw_IsRecordedToo()
    {
        var renderer = new FakeRenderer();
        var withDesertWolf = false;
        var store = new WorldMapStore(_root, () => [withDesertWolf ? Map("Terrain_016", "Olympus", Wolf, DesertWolf) : Map("Terrain_016", "Olympus", Wolf)], renderer);
        store.Reload(everything: false);
        withDesertWolf = true;

        store.Reload(everything: false);

        Assert.Equal("Conifer Hard: Desert Wolf added", Assert.Single(store.ChangeRecords()["Terrain_016"].Zones).Summary);
    }

    [Fact]
    public void WhatChanged_IsKeptUntilTheWorldChangesAgain()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], renderer);
        renderer.Features["Terrain_016"] = new([Cave(0.1), Geyser(0.2)], []);
        store.Reload(everything: false);
        renderer.Stamps["Terrain_016"] = "2";
        renderer.Features["Terrain_016"] = new([Cave(0.1)], []);
        store.Reload(everything: false);
        Assert.Single(store.ChangeRecords());

        // Nothing changed, then every map drawn again with nothing changed: still there.
        store.Reload(everything: false);
        store.Reload(everything: true);

        Assert.Equal([Geyser(0.2)], store.ChangeRecords()["Terrain_016"].Removed);

        // The game replaces its files again, with the same spots: the old record goes, and the new
        // one says only that the files changed.
        renderer.Stamps["Terrain_016"] = "3";
        store.Reload(everything: false);

        var record = store.ChangeRecords()["Terrain_016"];
        Assert.True(record.FilesChanged);
        Assert.Empty(record.Removed);
        Assert.Equal(0, record.RemovedCount);
    }

    [Fact]
    public void SpotsThatCameAndWent_AreToldApartByKindNameAndPlace()
    {
        var mission = new WorldFeature(WorldFeatureKind.MissionSpot, "Foresty", new(0.5, 0.5)) { Category = "Objective · Foresty" };

        var (added, removed) = WorldMapChanges.Spots(
            [Cave(0.1), Cave(0.3), mission, mission with { }],
            [Cave(0.1), Cave(0.30004), Cave(0.4), mission, mission with { Category = "Objective · Other" }]);

        // 0.3 → 0.30004 is the same place (under a metre); the second of two alike mission spots went, and one with another category came.
        Assert.Equal([Cave(0.4), mission with { Category = "Objective · Other" }], added);
        Assert.Equal([mission], removed);
    }

    /// <summary>A world whose spots couldn't be read last time (or this time) would show every spot as new (or gone).</summary>
    [Fact]
    public void SpotsAreNotCompared_WhenEitherSideHasNone()
    {
        Assert.Equal((0, 0), Counts(WorldMapChanges.Spots([], [Cave(0.1), Geyser(0.2)])));
        Assert.Equal((0, 0), Counts(WorldMapChanges.Spots([Cave(0.1), Geyser(0.2)], [])));

        static (int, int) Counts((List<WorldFeature> Added, List<WorldFeature> Removed) spots) => (spots.Added.Count, spots.Removed.Count);
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
        Assert.Contains("No longer in the game: Devland.", result.Summary);
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

    /// <summary>Regression: with the game folder moved or its drive unplugged, Reload "redrew" every map as nothing.</summary>
    [Fact]
    public void AGameThatCantBeRead_KeepsTheDrawnMaps()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], renderer);
        store.Reload(everything: false);
        renderer.Readable = false;

        Assert.Throws<IOException>(store.Check);
        Assert.Throws<IOException>(() => store.Reload(everything: false));
        Assert.Throws<IOException>(() => store.Reload(everything: true));

        Assert.True(File.Exists(store.PicturePath("Terrain_016")));
        Assert.NotNull(store.ReadLayer("Terrain_016", WorldLayerKind.Creatures));
        Assert.Equal(WorldPictureSource.Picture, store.PictureSources()["Terrain_016"]);
    }

    /// <summary>Regression: an unreadable world list (D_WorldData.json) read as "the game has no worlds" and every map was deleted.</summary>
    [Fact]
    public void AnUnreadableWorldList_DeletesNoMaps()
    {
        var maps = new List<WorldMapInfo> { Map("Terrain_016", "Olympus", Wolf) };
        var store = new WorldMapStore(_root, () => maps, new FakeRenderer());
        store.Reload(everything: false);
        maps.Clear();

        Assert.Throws<IOException>(() => store.Reload(everything: false));
        Assert.Throws<IOException>(() => store.Reload(everything: true));

        Assert.True(File.Exists(store.PicturePath("Terrain_016")));
        Assert.True(store.PictureSources().ContainsKey("Terrain_016"));
    }

    /// <summary>Regression: a map that failed to redraw lost its last drawing (its folder was deleted before anything was read).</summary>
    [Fact]
    public void AMapThatFailsToRedraw_KeepsItsLastDrawing()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], renderer);
        store.Reload(everything: false);
        renderer.Stamps["Terrain_016"] = "2";
        renderer.Broken.Add("Terrain_016");

        var result = store.Reload(everything: false);

        Assert.Contains("Couldn't draw Olympus", result.Summary);
        Assert.True(File.Exists(store.PicturePath("Terrain_016")));
        Assert.Equal(WorldPictureSource.Picture, store.PictureSources()["Terrain_016"]);
        Assert.True(store.Check().OutOfDate);

        // Rebuild all that fails keeps it too.
        store.Reload(everything: true);
        Assert.True(File.Exists(store.PicturePath("Terrain_016")));

        renderer.Broken.Clear();
        renderer.Drawn.Clear();
        store.Reload(everything: false);

        Assert.Equal(["Terrain_016"], renderer.Drawn);
        Assert.False(store.Check().OutOfDate);
    }

    /// <summary>Regression: Starlink closed during the first (minutes-long) build left no index, and the next one started over from the first world.</summary>
    [Fact]
    public void AnInterruptedReload_KeepsTheWorldsItDrew_AndTheNextOneDrawsTheRest()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("A", "Alpha", Wolf), Map("B", "Beta", Wolf), Map("C", "Gamma", Wolf)], renderer);

        Assert.ThrowsAny<OperationCanceledException>(() => store.Reload(everything: false, new StopAt(3)));

        Assert.Equal(2, store.PictureSources().Count);
        Assert.False(store.IsComplete());

        renderer.Drawn.Clear();
        store.Reload(everything: false);

        Assert.Equal(["C"], renderer.Drawn);
        Assert.True(store.IsComplete());
    }

    /// <summary>Stop on the Map page: the world being drawn is finished and kept, the next isn't started, and the next Reload draws the rest.</summary>
    [Fact]
    public void AStoppedReload_FinishesTheWorldItIsOn_AndTheNextOneDrawsTheRest()
    {
        var renderer = new FakeRenderer();
        var store = new WorldMapStore(_root, () => [Map("A", "Alpha", Wolf), Map("B", "Beta", Wolf), Map("C", "Gamma", Wolf)], renderer);
        using var stop = new CancellationTokenSource();

        // Stop is clicked while the first world is being drawn.
        Assert.ThrowsAny<OperationCanceledException>(() => store.Reload(everything: false, new Progress(stop.Cancel), stop.Token));

        Assert.Equal(["A"], renderer.Drawn);
        Assert.Equal(["A"], store.PictureSources().Keys);
        Assert.False(store.IsComplete());

        renderer.Drawn.Clear();
        store.Reload(everything: false);

        Assert.Equal(["B", "C"], renderer.Drawn);
        Assert.True(store.IsComplete());
    }

    private sealed class Progress(Action onReport) : IProgress<string>
    {
        public void Report(string value) => onReport();
    }

    /// <summary>Old index.json files have no Complete flag: they read as a finished build.</summary>
    [Fact]
    public void AnIndexFromBeforeTheCompleteFlag_ReadsAsComplete()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.json"), """{ "Maps": { "Terrain_016": { "Stamp": "1" } } }""");
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], new FakeRenderer());

        Assert.True(store.IsComplete());
    }

    /// <summary>Rebuild all also clears stray files left in the folder itself (a half-written index's temp file).</summary>
    [Fact]
    public void RebuildAll_ClearsStrayFiles_ButKeepsTheIndex()
    {
        var store = new WorldMapStore(_root, () => [Map("Terrain_016", "Olympus", Wolf)], new FakeRenderer());
        store.Reload(everything: false);
        var stray = Path.Combine(_root, "index.json.1234.tmp");
        File.WriteAllText(stray, "x");

        store.Reload(everything: true);

        Assert.False(File.Exists(stray));
        Assert.True(store.IsComplete());
    }

    [Fact]
    public void ZoneImages_KeepTheirExactColours_ThroughThePng()
    {
        var pixels = new WorldLayerPixels(2, [0x00E0A0, 0xE06500, 0x0BE000, 0xFFFFFF]);

        var back = WorldLayerPixels.FromPng(pixels.ToPng());

        Assert.Equal(pixels.Pixels, back!.Pixels);
    }

    /// <summary>An odd width: each row of the decoded image is read from its own start.</summary>
    [Fact]
    public void ZoneImages_KeepTheirExactColours_OnAnOddWidth()
    {
        var pixels = new WorldLayerPixels(3, [0x00E0A0, 0xE06500, 0x0BE000, 0xFFFFFF, 0x000001, 0x123456, 0xABCDEF, 0x010203, 0xFEDCBA]);

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
