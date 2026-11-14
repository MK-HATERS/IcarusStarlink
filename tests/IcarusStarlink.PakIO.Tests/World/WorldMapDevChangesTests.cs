using IcarusStarlink.PakIO.World;
using SkiaSharp;

namespace IcarusStarlink.PakIO.Tests.World;

/// <summary>
/// What the game's developers worked on: where a redrawn world's picture and zone shapes differ,
/// which of its game files changed, came or went, worlds that are new or dropped, a record even
/// when no spawn or spot changed, and stamps taken ahead of the next game update without drawing.
/// </summary>
public sealed class WorldMapDevChangesTests : IDisposable
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

    private static readonly DateTime Today = new(2030, 5, 17, 9, 30, 0, DateTimeKind.Utc);

    /// <summary>A 64 x 64 picture as a JPEG (made as the renderer makes them): grey, with the given squares painted over.</summary>
    private static byte[] Jpeg(int size = 64, byte shade = 120, params (int X, int Y, int Size, SKColor Color)[] patches)
    {
        using var bitmap = new SKBitmap(size, size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(shade, shade, shade));
            foreach (var (x, y, patch, color) in patches)
            {
                using var paint = new SKPaint { Color = color };
                canvas.DrawRect(x, y, patch, patch, paint);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }

    [Fact]
    public void ThePictureDrawnAgainFromTheSameTiles_HasNoChange() =>
        Assert.Null(WorldImageCompare.Pictures(Jpeg(), Jpeg(), 4, 4));

    [Fact]
    public void AChangeInOneGridSquare_NamesThatSquareOnly()
    {
        // 4 x 4 squares of 16 pixels: the patch sits inside column 2, row 1.
        var change = WorldImageCompare.Pictures(Jpeg(), Jpeg(patches: (34, 18, 12, SKColors.DarkGreen)), 4, 4);

        Assert.NotNull(change);
        Assert.False(change.Whole);
        Assert.Equal((4, 4), (change.Columns, change.Rows));
        Assert.Equal([1 * 4 + 2], change.Cells);
    }

    /// <summary>Two encodings never match to the bit: a slightly different shade all over is not a change.</summary>
    [Fact]
    public void SmallDifferencesAllOver_AreNotAChange() =>
        Assert.Null(WorldImageCompare.Pictures(Jpeg(shade: 120), Jpeg(shade: 120 + WorldImageCompare.Tolerance - 4), 4, 4));

    [Fact]
    public void APictureOfAnotherSize_OrWithNoOldOne_IsWhollyChanged()
    {
        var resized = WorldImageCompare.Pictures(Jpeg(64), Jpeg(128), 4, 4);
        var first = WorldImageCompare.Pictures(null, Jpeg(), 4, 4);
        var all = WorldImageCompare.Pictures(Jpeg(shade: 40), Jpeg(shade: 200), 4, 4);

        Assert.True(resized!.Whole);
        Assert.Empty(resized.Cells);
        Assert.Equal((true, "new"), (first!.Whole, first.Note));
        Assert.True(all!.Whole);
    }

    [Fact]
    public void AWorldWithoutAGrid_IsComparedInEightByEight()
    {
        var change = WorldImageCompare.Pictures(Jpeg(), Jpeg(patches: (1, 1, 6, SKColors.DarkGreen)), 0, 0);

        Assert.Equal((8, 8), (change!.Columns, change.Rows));
        Assert.Equal([0], change.Cells);
    }

    [Fact]
    public void AZoneImage_SaysWhereItsShapesMoved()
    {
        var before = new WorldLayerPixels(4, [1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var moved = new WorldLayerPixels(4, [1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);

        Assert.Null(WorldImageCompare.Layers("Creatures", before, before with { }, 2, 2));
        var change = WorldImageCompare.Layers("Creatures", before, moved, 2, 2);
        Assert.Equal(("Creatures", false), (change!.What, change.Whole));
        Assert.Equal([3], change.Cells);
        Assert.True(WorldImageCompare.Layers("Creatures", before, new WorldLayerPixels(2, [1, 0, 0, 0]), 2, 2)!.Whole);
        Assert.Equal("gone", WorldImageCompare.Layers("Creatures", before, null, 2, 2)!.Note);
    }

    /// <summary>Draws what the test hands it: a picture and a zone image per world, a stamp per world and per file.</summary>
    private sealed class Game : IWorldMapRenderer
    {
        public Dictionary<string, string> Stamps { get; } = [];

        /// <summary>Content path → that file's stamp; a file missing here isn't in the game.</summary>
        public Dictionary<string, string> Files { get; } = [];

        public Dictionary<string, byte[]> Pictures { get; } = [];

        public Dictionary<string, WorldLayerPixels> Layers { get; } = [];

        public List<string> Drawn { get; } = [];

        public HashSet<string> Broken { get; } = [];

        public bool Readable { get; set; } = true;

        /// <summary>Each drawing waits for this after saying it started.</summary>
        public ManualResetEventSlim? Hold { get; set; }

        public ManualResetEventSlim Drawing { get; } = new(false);

        public bool CanReadGame() => Readable;

        public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map)
        {
            Drawn.Add(map.Id);
            Drawing.Set();
            Hold?.Wait(TimeSpan.FromSeconds(10));
            if (Broken.Contains(map.Id))
            {
                throw new IOException("The pak is locked.");
            }

            return (Pictures.GetValueOrDefault(map.Id, [1, 2, 3]), WorldPictureSource.Picture);
        }

        public WorldLayerPixels? ReadLayer(string heatmapAssetPath) => Layers.GetValueOrDefault(heatmapAssetPath, new(2, [0x00FF90, 0x00FF90, 0, 0]));

        public string Stamp(WorldMapInfo map) => Stamps.GetValueOrDefault(map.Id, "c2:1");

        public string? FileStamp(string assetPath) => Files.GetValueOrDefault(assetPath);
    }

    /// <summary>A world with one picture tile, a creature zone image and two level files; a 4 x 4 in-game grid.</summary>
    private static WorldMapInfo Map(string id, string name) =>
        new(id, name, true, [new WorldTile(0, 0, $"PROC/MapTextures/{id}/T_x0_y0.uasset")], [],
            [new WorldLayer(WorldLayerKind.Creatures, $"Heatmaps/{id}/GT_AI.uasset",
                new Dictionary<uint, WorldZone> { [0x00FF90] = new("Conifer_Hard", "Conifer Hard", [new WorldZoneEntry("Wolf", "Wolf", 8)]) })],
            Extent: new WorldExtent(0, 0, 4 * WorldMapInfo.GridSquare, 4 * WorldMapInfo.GridSquare),
            LevelFiles: [$"Maps/{id}/{id}.umap", $"Maps/{id}/Generated/{id}_Cave_A.umap"]);

    private static void HasFiles(Game game, WorldMapInfo map, string stamp = "1")
    {
        foreach (var path in map.PictureTiles.Select(t => t.AssetPath).Concat(map.Layers.Select(l => l.HeatmapAssetPath!)).Concat(map.Levels))
        {
            game.Files[path] = stamp;
        }
    }

    [Fact]
    public void AWorldWhosePictureAloneChanged_GetsARecord_WithTheSquaresThatChanged()
    {
        var game = new Game();
        game.Pictures["Styx"] = Jpeg();
        var store = new WorldMapStore(_root, () => [Map("Styx", "Styx")], game, () => Today);
        store.Reload(everything: false);
        Assert.Empty(store.ChangeRecords());

        game.Stamps["Styx"] = "c2:2";
        game.Pictures["Styx"] = Jpeg(patches: (34, 18, 12, SKColors.DarkGreen));
        store.Reload(everything: false);

        var record = store.ChangeRecords()["Styx"];
        Assert.Equal(Today, record.When);
        Assert.True(record.FilesChanged);
        Assert.False(record.NewMap);
        Assert.Equal([1 * 4 + 2], record.Picture!.Cells);
        Assert.Empty(record.Shapes);
        Assert.Empty(record.Zones);
        // The new picture replaced the old one only after the two were compared.
        Assert.Equal(game.Pictures["Styx"], File.ReadAllBytes(store.PicturePath("Styx")));
    }

    [Fact]
    public void AZoneWhoseShapeMoved_IsRecorded()
    {
        var game = new Game();
        var store = new WorldMapStore(_root, () => [Map("Styx", "Styx")], game);
        store.Reload(everything: false);

        game.Stamps["Styx"] = "c2:2";
        game.Layers["Heatmaps/Styx/GT_AI.uasset"] = new(2, [0x00FF90, 0x00FF90, 0, 0x00FF90]);
        store.Reload(everything: false);

        var shape = Assert.Single(store.ChangeRecords()["Styx"].Shapes);
        Assert.Equal(("Creatures", false), (shape.What, shape.Whole));
        Assert.Equal([3], shape.Cells);
        Assert.Null(store.ChangeRecords()["Styx"].Picture);
    }

    /// <summary>The game replaced a world's files and nothing drawn differs: still recorded, so the page can say so.</summary>
    [Fact]
    public void FilesThatChangedWithNothingToSee_AreStillRecorded()
    {
        var game = new Game();
        var store = new WorldMapStore(_root, () => [Map("Styx", "Styx")], game);
        store.Reload(everything: false);

        game.Stamps["Styx"] = "c2:2";
        store.Reload(everything: false);

        var record = store.ChangeRecords()["Styx"];
        Assert.True(record.FilesChanged);
        Assert.Null(record.Picture);
        Assert.Empty(record.Files);
    }

    [Fact]
    public void TheFilesThatChangedCameAndWent_AreListedByKind_ByShortName()
    {
        var game = new Game();
        var levels = new List<string> { "Maps/Styx/Styx.umap", "Maps/Styx/Generated/Styx_Cave_A.umap", "Maps/Styx/Generated/Styx_Old.umap" };
        var store = new WorldMapStore(_root, () => [Map("Styx", "Styx") with { LevelFiles = [.. levels] }], game);
        HasFiles(game, Map("Styx", "Styx"));
        game.Files["Maps/Styx/Generated/Styx_Old.umap"] = "1";
        store.Reload(everything: false);

        game.Stamps["Styx"] = "c2:2";
        game.Files["PROC/MapTextures/Styx/T_x0_y0.uasset"] = "2";
        game.Files["Maps/Styx/Styx.umap"] = "2";
        levels.Remove("Maps/Styx/Generated/Styx_Old.umap");
        levels.Add("Maps/Styx/Generated/Styx_Volcano_Lair.umap");
        game.Files["Maps/Styx/Generated/Styx_Volcano_Lair.umap"] = "1";
        store.Reload(everything: false);

        var files = store.ChangeRecords()["Styx"].Files;
        Assert.Equal(["Picture tiles", "Level files"], files.Select(f => f.Kind));
        Assert.Equal(["T_x0_y0"], files[0].Changed);
        Assert.Equal((1, 0, 0), (files[0].ChangedCount, files[0].AddedCount, files[0].GoneCount));
        Assert.Equal(["Styx"], files[1].Changed);
        Assert.Equal(["Styx_Volcano_Lair"], files[1].Added);
        Assert.Equal(["Styx_Old"], files[1].Gone);
    }

    /// <summary>The index keeps one short stamp per file under its short name: no path is written twice.</summary>
    [Fact]
    public void TheIndex_KeepsAShortStampPerFile_WithoutItsPath()
    {
        var game = new Game();
        HasFiles(game, Map("Styx", "Styx"));
        var store = new WorldMapStore(_root, () => [Map("Styx", "Styx")], game);

        store.Reload(everything: false);

        var index = File.ReadAllText(Path.Combine(_root, "index.json"));
        Assert.Contains("\"Styx_Cave_A\": \"", index);
        Assert.DoesNotContain("Maps/Styx", index);
        Assert.DoesNotContain("PROC/", index);
    }

    [Fact]
    public void ManyChangedFiles_AreCounted_AndOnlyTheFirstFortyNamed()
    {
        var before = new Dictionary<string, Dictionary<string, string>> { ["Level files"] = Enumerable.Range(0, 100).ToDictionary(i => $"L{i:000}", _ => "A") };
        var after = new Dictionary<string, Dictionary<string, string>> { ["Level files"] = Enumerable.Range(0, 100).ToDictionary(i => $"L{i:000}", _ => "B") };

        var change = Assert.Single(WorldMapStore.FileChanges(before, after));

        Assert.Equal(100, change.ChangedCount);
        Assert.Equal(WorldFileChange.MaxNames, change.Changed.Count);
        Assert.Equal("L000", change.Changed[0]);
        Assert.Empty(WorldMapStore.FileChanges(null, after));
    }

    [Fact]
    public void AWorldTheGameAdded_IsRecordedAsNew_AndADroppedOneIsNamed()
    {
        var game = new Game();
        var maps = new List<WorldMapInfo> { Map("Olympus", "Olympus"), Map("Dev_01", "Devland") };
        var store = new WorldMapStore(_root, () => maps, game, () => Today);
        store.Reload(everything: false);

        // The first build: every world would be "new".
        Assert.Empty(store.ChangeRecords());

        maps.RemoveAt(1);
        maps.Add(Map("Terrain_021", "Elysium"));
        var result = store.Reload(everything: false);

        var record = Assert.Single(store.ChangeRecords());
        Assert.Equal("Terrain_021", record.Key);
        Assert.True(record.Value.NewMap);
        Assert.Equal(Today, record.Value.When);
        Assert.Equal("Changed: Elysium: new map. 1 map unchanged. No longer in the game: Devland.", result.Summary);

        // It stays "new" until it changes: another Reload, or Rebuild all, keeps the record.
        store.Reload(everything: false);
        store.Reload(everything: true);
        Assert.True(store.ChangeRecords()["Terrain_021"].NewMap);
    }

    [Fact]
    public void ADroppedWorldAlone_IsNamedInTheSummary()
    {
        var maps = new List<WorldMapInfo> { Map("Olympus", "Olympus"), Map("Dev_01", "Devland") };
        var store = new WorldMapStore(_root, () => maps, new Game());
        store.Reload(everything: false);
        maps.RemoveAt(1);

        Assert.Contains("Devland: no longer in the game", store.Check().Summary);
        Assert.Equal("No longer in the game: Devland. The map that's left is up to date.", store.Reload(everything: false).Summary);
    }

    /// <summary>A world that couldn't be drawn in the first build isn't "new" when it's drawn later; a new one that failed still is.</summary>
    [Fact]
    public void AWorldThatFailedToDraw_IsNewOnlyIfTheGameAddedIt()
    {
        var game = new Game();
        var maps = new List<WorldMapInfo> { Map("Olympus", "Olympus"), Map("Styx", "Styx") };
        var store = new WorldMapStore(_root, () => maps, game);
        game.Broken.Add("Styx");
        store.Reload(everything: false);
        game.Broken.Clear();
        store.Reload(everything: false);

        Assert.Empty(store.ChangeRecords());

        maps.Add(Map("Terrain_021", "Elysium"));
        game.Broken.Add("Terrain_021");
        store.Reload(everything: false);
        game.Broken.Clear();
        store.Reload(everything: false);

        Assert.True(store.ChangeRecords()["Terrain_021"].NewMap);
    }

    /// <summary>Two worlds drawn under stamps made the old way (a bare hash).</summary>
    private (WorldMapStore Store, Game Game) DrawnUnderOldStamps()
    {
        var game = new Game();
        HasFiles(game, Map("Olympus", "Olympus"));
        HasFiles(game, Map("Styx", "Styx"));
        game.Stamps["Olympus"] = "0123456789ABCDEF0123456789ABCDEF";
        game.Stamps["Styx"] = "FEDCBA9876543210FEDCBA9876543210";
        var store = new WorldMapStore(_root, () => [Map("Olympus", "Olympus"), Map("Styx", "Styx")], game, () => Today);
        store.Reload(everything: false);
        // As an index from before the files' stamps were kept.
        var path = Path.Combine(_root, "index.json");
        var index = System.Text.Json.JsonSerializer.Deserialize<WorldMapIndex>(File.ReadAllText(path))!;
        foreach (var id in index.Maps.Keys.ToList())
        {
            index.Maps[id] = index.Maps[id] with { Files = null };
        }

        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(index));
        game.Drawn.Clear();
        game.Stamps["Olympus"] = "c2:AAAA";
        game.Stamps["Styx"] = "c2:BBBB";
        return (store, game);
    }

    /// <summary>
    /// Regression: maps drawn under old stamps took today's only on a Reload. A game update that
    /// landed before any Reload was then taken as "nothing changed", and what it changed was missed.
    /// </summary>
    [Fact]
    public void Adopt_TakesTodaysStampsWithoutDrawing_SoTheNextGameUpdateIsCompared()
    {
        var (store, game) = DrawnUnderOldStamps();
        Assert.True(store.NeedsAdoption());

        Assert.Equal(WorldMapStore.AdoptResult.Adopted, store.Adopt());

        Assert.Empty(game.Drawn);
        Assert.False(store.NeedsAdoption());
        Assert.Equal(WorldMapStore.AdoptResult.NotNeeded, store.Adopt());

        // The game update: Styx's main level changed. It's seen, and named.
        game.Stamps["Styx"] = "c2:CCCC";
        game.Files["Maps/Styx/Styx.umap"] = "2";
        Assert.Contains("Styx: map redrawn", store.Check().Summary);
        store.Reload(everything: false);

        Assert.Equal(["Styx"], game.Drawn);
        var files = Assert.Single(store.ChangeRecords()["Styx"].Files);
        Assert.Equal(("Level files", "Styx"), (files.Kind, Assert.Single(files.Changed)));
    }

    /// <summary>A world already on today's stamps but drawn before its files' stamps were kept gets them; one whose files changed since waits for Reload.</summary>
    [Fact]
    public void Adopt_FillsInTheFilesStamps_ButLeavesAWorldWhoseFilesChanged()
    {
        var (store, game) = DrawnUnderOldStamps();
        store.Reload(everything: false); // takes today's stamps (and the files' stamps)
        var path = Path.Combine(_root, "index.json");
        var index = System.Text.Json.JsonSerializer.Deserialize<WorldMapIndex>(File.ReadAllText(path))!;
        foreach (var id in index.Maps.Keys.ToList())
        {
            index.Maps[id] = index.Maps[id] with { Files = null };
        }

        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(index));
        game.Stamps["Styx"] = "c2:CCCC";

        Assert.Equal(WorldMapStore.AdoptResult.Adopted, store.Adopt());

        var after = System.Text.Json.JsonSerializer.Deserialize<WorldMapIndex>(File.ReadAllText(path))!;
        Assert.NotNull(after.Maps["Olympus"].Files);
        Assert.Null(after.Maps["Styx"].Files);
        Assert.Equal("c2:BBBB", after.Maps["Styx"].Stamp);
        Assert.True(store.Check().OutOfDate);
        Assert.Empty(game.Drawn);
    }

    [Fact]
    public void Adopt_ChangesNothing_WhileTheGamesFilesCantBeRead()
    {
        var (store, game) = DrawnUnderOldStamps();
        var before = File.ReadAllText(Path.Combine(_root, "index.json"));
        game.Readable = false;

        Assert.Equal(WorldMapStore.AdoptResult.Unreadable, store.Adopt());

        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "index.json")));
    }

    [Fact]
    public async Task Adopt_StepsAside_WhileAReloadRuns()
    {
        var (store, game) = DrawnUnderOldStamps();
        using var hold = new ManualResetEventSlim(false);
        game.Hold = hold;
        game.Drawing.Reset();
        var reload = Task.Run(() => store.Reload(everything: true));
        Assert.True(game.Drawing.Wait(TimeSpan.FromSeconds(10)));

        // Half-way through a Reload the index isn't complete, and the Reload holds it.
        var during = store.Adopt();
        hold.Set();
        await reload;

        Assert.NotEqual(WorldMapStore.AdoptResult.Adopted, during);
        Assert.Equal(WorldMapStore.AdoptResult.NotNeeded, store.Adopt());
    }

    [Fact]
    public async Task AdoptInTheBackground_WaitsUntilTheGameCanBeRead_AndRunsOnce()
    {
        var (store, game) = DrawnUnderOldStamps();
        var reads = 0;
        var readable = new WorldMapStore(_root, () =>
        {
            // The game's files come back on the second try.
            game.Readable = ++reads > 1;
            return [Map("Olympus", "Olympus"), Map("Styx", "Styx")];
        }, game);

        var result = await readable.AdoptWhenReadableAsync(TimeSpan.Zero, TimeSpan.Zero, tries: 3);

        Assert.Equal(WorldMapStore.AdoptResult.Adopted, result);
        Assert.Equal(2, reads);
        Assert.Empty(game.Drawn);
        // Nothing left to do: the game isn't read again.
        Assert.Equal(WorldMapStore.AdoptResult.NotNeeded, await readable.AdoptWhenReadableAsync(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(2, reads);
        Assert.False(store.NeedsAdoption());
    }
}
