using System.Text;
using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.PakIO.Tests.World;

/// <summary>
/// The worlds and zones read from small copies of the real tables: D_WorldData's worlds,
/// D_Terrains' names and zone links, colour → zone rows, names players see, and the installed
/// mods' copy of a table over the game's.
/// </summary>
public sealed class WorldMapCatalogTests
{
    private static JsonObject Table(object? defaults, params JsonObject[] rows) =>
        new() { ["Defaults"] = defaults is null ? new JsonObject() : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(defaults)), ["Rows"] = new JsonArray(rows) };

    private static JsonObject Row(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>Olympus with its picture, a creature zone, a fish zone and a biome; ARK with nothing but its tiles.</summary>
    private static Dictionary<string, JsonObject> GameTables() => new()
    {
        ["World/D_WorldData.json"] = Table(null,
            Row("""
                {"Name":"Terrain_016","MinimapData":{
                  "MapTextures":["/Game/PROC/MapTextures/Terrain_016/VisualMapTex_T016_x0_y0.VisualMapTex_T016_x0_y0","/Game/PROC/MapTextures/Terrain_016/VisualMapTex_T016_x1_y1.VisualMapTex_T016_x1_y1"],
                  "HeightMapTextures":["/Game/PROC/MapTextures/Terrain_016/HeightMapTex_T016_x0_y0.HeightMapTex_T016_x0_y0"]}}
                """),
            Row("""{"Name":"Terrain_022","MinimapData":{"MapTextures":["/Game/PROC/MapTextures/Terrain_022/T_VisualMapTex_T022_x0_y0.T_VisualMapTex_T022_x0_y0"]}}"""),
            Row("""{"Name":"Outpost_011","MinimapData":{"MapTextures":["/Game/PROC/MapTextures/Outpost_011/VisualMapTex_O011_x0_y0.VisualMapTex_O011_x0_y0"]}}""")),
        ["Prospects/D_Terrains.json"] = Table(new { SpawnConfig = new { RowName = "None" }, FishConfig = new { RowName = "None" } },
            Row("""
                {"Name":"Terrain_016","TerrainName":"NSLOCTEXT(\"D_Terrains\", \"Terrain_016-TerrainName\", \"Olympus\")",
                 "BiomeMap":"/Game/Heatmaps/Terrain_016_OLY/GT_Terrain016_Biome.GT_Terrain016_Biome",
                 "SpawnConfig":{"RowName":"Olympus"},"FishConfig":{"RowName":"Olympus"}}
                """),
            Row("""{"Name":"Outpost_011","TerrainName":"INVTEXT(\"[DNT] Outpost 011\")"}""")),
        ["AI/D_AISpawnConfig.json"] = Table(null,
            Row("""
                {"Name":"Olympus","SpawnMap":"/Game/Heatmaps/Terrain_016_OLY/GT_Terrain016_AISpawnMap.GT_Terrain016_AISpawnMap",
                 "SpawnZones":[{"Color":{"B":144,"G":255,"R":0,"A":255},"SpawnZone":{"RowName":"OLY_Conifer_Hard","DataTableName":"D_AISpawnZones"}}]}
                """)),
        ["AI/D_AISpawnZones.json"] = Table(new { Creatures = new { AISpawnList = Array.Empty<object>(), BiomeSpawnDensity = 15 }, MinLevel = 1, MaxLevel = 10 },
            Row("""
                {"Name":"OLY_Conifer_Hard","MinLevel":60,"MaxLevel":80,"Creatures":{"AISpawnList":[
                  {"AISetup":{"Value":"Conifer_Wolf"},"SpawnWeight":8},
                  {"AISetup":{"Value":"Rock_Dog"},"SpawnWeight":2},
                  {"AISetup":{"Value":"None"},"SpawnWeight":5}]}}
                """)),
        ["Fish/D_FishSpawnConfig.json"] = Table(null,
            Row("""
                {"Name":"Olympus","SpawnMap":"/Game/Heatmaps/Terrain_016_OLY/GT_Terrain016_Fish.GT_Terrain016_Fish",
                 "SpawnZones":[{"Color":{"B":160,"G":224,"R":0,"A":255},"SpawnZone":{"RowName":"OLY_Conifer_Main"}}]}
                """)),
        ["Fish/D_FishSpawnZones.json"] = Table(new { SpawnList = new { }, ZoneFishQuality = 0.5 },
            Row("""{"Name":"OLY_Conifer_Main","SpawnList":{"(Value=\"Fish_01\")":100,"(Value=\"Fish_99\")":25}}""")),
        ["AI/D_AISetup.json"] = Table(null, Row("""{"Name":"Conifer_Wolf","BestiaryGroup":{"RowName":"Forest_Wolf"}}""")),
        ["Bestiary/D_BestiaryData.json"] = Table(null,
            Row("""{"Name":"Forest_Wolf","CreatureName":"NSLOCTEXT(\"D_BestiaryData\", \"Forest_Wolf-CreatureName\", \"Wolf\")"}""")),
        ["Traits/D_Itemable.json"] = Table(null,
            Row("""{"Name":"Item_Fish_01","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Fish_01-DisplayName\", \"Spotted Snapper\")"}""")),
        ["Prospects/D_Biomes.json"] = Table(new { Color = new { R = 0, G = 0, B = 0, A = 0 } },
            Row("""{"Name":"NoBiome","Color":{"A":255}}"""),
            Row("""{"Name":"Conifer","DisplayName":"NSLOCTEXT(\"D_Biomes\", \"Conifer-DisplayName\", \"Forest\")","Color":{"R":51,"G":153,"B":0}}"""),
            Row("""{"Name":"Unused"}""")),
    };

    private static IReadOnlyList<WorldMapInfo> Read(Dictionary<string, JsonObject> game, Dictionary<string, JsonObject>? mods = null) =>
        WorldMapCatalog.Read(new WorldTableSource(
            path => game.TryGetValue(path, out var t) ? Encoding.UTF8.GetBytes(t.ToJsonString()) : null,
            path => mods is not null && mods.TryGetValue(path, out var t) ? Encoding.UTF8.GetBytes(t.ToJsonString()) : null));

    [Fact]
    public void EveryWorldInWorldData_IsAMap_WithTheNamePlayersSee()
    {
        var maps = Read(GameTables());

        Assert.Equal(["Terrain_016", "Terrain_022", "Outpost_011"], maps.Select(m => m.Id));
        var olympus = maps[0];
        Assert.Equal("Olympus", olympus.Name);
        Assert.False(olympus.InDevelopment);
        Assert.Equal([(0, 0), (1, 1)], olympus.PictureTiles.Select(t => (t.X, t.Y)));
        Assert.Equal("PROC/MapTextures/Terrain_016/VisualMapTex_T016_x1_y1.uasset", olympus.PictureTiles[1].AssetPath);
        Assert.Single(olympus.HeightTiles);
    }

    [Fact]
    public void AWorldTheGameDoesntOfferYet_IsInDevelopment()
    {
        var maps = Read(GameTables());

        // No D_Terrains row: named by the user (the game has no name for it yet).
        var ark = maps.Single(m => m.Id == "Terrain_022");
        Assert.True(ark.InDevelopment);
        Assert.Equal("ARK", ark.Name);
        Assert.Empty(ark.Layers);

        // "[DNT]" (do not translate: not finished) in its name: in development, shown without the tag.
        var outpost = maps.Single(m => m.Id == "Outpost_011");
        Assert.True(outpost.InDevelopment);
        Assert.Equal("Outpost 011", outpost.Name);
    }

    [Fact]
    public void CreatureZones_ComeFromTheSpawnConfigsColours_WithBestiaryNames()
    {
        var olympus = Read(GameTables())[0];

        var layer = olympus.Layer(WorldLayerKind.Creatures)!;
        Assert.Equal("Heatmaps/Terrain_016_OLY/GT_Terrain016_AISpawnMap.uasset", layer.HeatmapAssetPath);
        var zone = layer.ZonesByColor[0x00FF90];
        Assert.Equal("OLY_Conifer_Hard", zone.Row);
        Assert.Equal("Conifer Hard", zone.Name);
        Assert.Equal((60, 80), (zone.MinLevel, zone.MaxLevel));
        // "None" entries are left out; a creature without a bestiary entry gets its row name made readable.
        Assert.Equal([("Wolf", 8.0), ("Rock Dog", 2.0)], zone.Entries.Select(e => (e.Name, e.Weight)));
        Assert.False(zone.ChangedByMods);
    }

    [Fact]
    public void FishZones_HaveItemNames_AndTheDefaultQuality()
    {
        var olympus = Read(GameTables())[0];

        var zone = olympus.Layer(WorldLayerKind.Fishing)!.ZonesByColor[0x00E0A0];
        Assert.Equal([("Spotted Snapper", 100.0), ("Fish 99", 25.0)], zone.Entries.Select(e => (e.Name, e.Weight)));
        Assert.Equal(0.5, zone.FishQuality);
    }

    [Fact]
    public void Biomes_AreKeyedByTheirOwnColour_NeverTheDefaults()
    {
        var biomes = Read(GameTables())[0].Layer(WorldLayerKind.Biomes)!;

        Assert.Equal("Heatmaps/Terrain_016_OLY/GT_Terrain016_Biome.uasset", biomes.HeatmapAssetPath);
        var forest = Assert.Single(biomes.ZonesByColor);
        Assert.Equal(0x339900u, forest.Key);
        Assert.Equal("Forest", forest.Value.Name);
    }

    [Fact]
    public void AModsCopyOfAZone_IsWhatShows_AndTheGamesStaysForComparing()
    {
        var mods = new Dictionary<string, JsonObject>
        {
            ["AI/D_AISpawnZones.json"] = Table(null,
                Row("""{"Name":"OLY_Conifer_Hard","Creatures":{"AISpawnList":[{"AISetup":{"Value":"Conifer_Wolf"},"SpawnWeight":20}]}}""")),
        };

        var zone = Read(GameTables(), mods)[0].Layer(WorldLayerKind.Creatures)!.ZonesByColor[0x00FF90];

        Assert.True(zone.ChangedByMods);
        Assert.Equal([("Wolf", 20.0)], zone.Entries.Select(e => (e.Name, e.Weight)));
        Assert.Equal(["Wolf", "Rock Dog"], zone.GameEntries!.Select(e => e.Name));
    }

    [Fact]
    public void AModsCopyThatMatchesTheGame_IsNotAChange()
    {
        var game = GameTables();
        var mods = new Dictionary<string, JsonObject> { ["AI/D_AISpawnZones.json"] = (JsonObject)game["AI/D_AISpawnZones.json"].DeepClone() };

        var zone = Read(game, mods)[0].Layer(WorldLayerKind.Creatures)!.ZonesByColor[0x00FF90];

        Assert.False(zone.ChangedByMods);
        Assert.Null(zone.GameEntries);
    }

    [Fact]
    public void WhatAWorldIsPlayedAs_ComesFromTheProspectList()
    {
        var game = GameTables();
        game["Prospects/D_ProspectList.json"] = Table(new { bIsOpenWorld = false, bIsPersistent = false, bDisabled = false },
            Row("""{"Name":"OpenWorld_Olympus","Terrain":{"RowName":"Terrain_016"},"bIsOpenWorld":true,"bIsPersistent":true}"""),
            Row("""{"Name":"Mission_1","Terrain":{"RowName":"Terrain_016"}}"""),
            Row("""{"Name":"Mission_2","Terrain":{"RowName":"Terrain_016"}}"""),
            Row("""{"Name":"Mission_Off","Terrain":{"RowName":"Terrain_016"},"bDisabled":true}"""),
            Row("""{"Name":"Outpost011","Terrain":{"RowName":"Outpost_011"},"bIsPersistent":true}"""));

        var maps = Read(game);

        Assert.Equal("Open world · 2 missions", maps.Single(m => m.Id == "Terrain_016").KindText);
        Assert.Equal("Outpost", maps.Single(m => m.Id == "Outpost_011").KindText);
        Assert.Equal("", maps.Single(m => m.Id == "Terrain_022").KindText);
        Assert.Equal([0, 2, 3], maps.Select(m => m.KindOrder).Order());
    }

    [Fact]
    public void DropSites_ArePlacedOnTheMap_FromTheGridsEdges()
    {
        var game = GameTables();
        // Two cells side by side: the map spans X -200..200, Y -100..100.
        game["World/D_WorldData.json"] = Table(null, Row("""
            {"Name":"Terrain_016",
             "GridBounds":[{"Origin":{"X":-100,"Y":0},"BoxExtent":{"X":100,"Y":100}},{"Origin":{"X":100,"Y":0},"BoxExtent":{"X":100,"Y":100}}],
             "DropGroups":{
               "1":{"GroupName":"1_DesertSteps","Locations":[{"X":-200,"Y":-100},{"X":200,"Y":100}]},
               "25":{"GroupName":"25_BW3","Locations":[{"X":0,"Y":50}]},
               "9":{"GroupName":"9_OffTheMap","Locations":[{"X":900,"Y":0}]}}}
            """));

        var sites = Read(game).Single().DropSites;

        Assert.Equal([1, 25], sites.Select(s => s.Number));
        Assert.Equal("Desert Steps", sites[0].Name);
        Assert.Equal([new WorldMapPoint(0, 0), new WorldMapPoint(1, 1)], sites[0].Spots);
        Assert.Equal(new WorldMapPoint(0.5, 0.5), sites[0].Centre);
        Assert.Equal(new WorldMapPoint(0.5, 0.75), Assert.Single(sites[1].Spots));
    }

    [Theory]
    [InlineData("1_DesertSteps", "Desert Steps")]
    [InlineData("20_OasisHotdrop", "Oasis Hotdrop")]
    [InlineData("25_BW3", "BW3")]
    [InlineData("Stranded", "Stranded")]
    public void DropSiteNames_AreReadable(string group, string name) => Assert.Equal(name, WorldMapCatalog.DropSiteName(group));

    /// <summary>Reads with the mods' tables given as raw text (any shape a mod can ship).</summary>
    private static IReadOnlyList<WorldMapInfo> ReadWithRawMods(Dictionary<string, JsonObject> game, Dictionary<string, string> mods) =>
        WorldMapCatalog.Read(new WorldTableSource(
            path => game.TryGetValue(path, out var t) ? Encoding.UTF8.GetBytes(t.ToJsonString()) : null,
            path => mods.TryGetValue(path, out var t) ? Encoding.UTF8.GetBytes(t) : null));

    [Fact]
    public void AModsTableWithAKeyTwice_IsLeftOut_NotTheWholeMap()
    {
        // Parses fine, but reading MinimapData threw ArgumentException out of Read: no maps at all.
        var maps = ReadWithRawMods(GameTables(), new()
        {
            ["World/D_WorldData.json"] = """{"Rows":[{"Name":"Terrain_016","MinimapData":{"MapTextures":[],"MapTextures":[]}}]}""",
        });

        Assert.Equal(["Terrain_016", "Terrain_022", "Outpost_011"], maps.Select(m => m.Id));
        Assert.Equal(2, maps[0].PictureTiles.Count);
    }

    [Fact]
    public void AModsProspectWithANumberForItsTerrain_DoesntStopTheMap()
    {
        var maps = ReadWithRawMods(GameTables(), new()
        {
            ["Prospects/D_ProspectList.json"] = """{"Rows":[{"Name":"Tier1_Forest_Range","Terrain":{"RowName":16},"DropName":"POTSHOT"}]}""",
        });

        Assert.Equal(3, maps.Count);
    }

    [Fact]
    public void TilesWithHugeOrOddNumbers_AreLeftOut()
    {
        // _x99999999999 threw OverflowException out of Read; _x500 asked for a 256000 px canvas.
        var maps = ReadWithRawMods(GameTables(), new()
        {
            ["World/D_WorldData.json"] = """
                {"Rows":[{"Name":"Terrain_016","MinimapData":{"MapTextures":[
                  "/Game/PROC/MapTextures/Terrain_016/VisualMapTex_T016_x0_y0.VisualMapTex_T016_x0_y0",
                  "/Game/PROC/MapTextures/Terrain_016/VisualMapTex_T016_x99999999999_y0.VisualMapTex_T016_x99999999999_y0",
                  "/Game/PROC/MapTextures/Terrain_016/VisualMapTex_T016_x500_y1.VisualMapTex_T016_x500_y1"]}}]}
                """,
        });

        Assert.Equal([(0, 0)], maps[0].PictureTiles.Select(t => (t.X, t.Y)));
    }

    [Theory]
    [InlineData(403200, 403200, 16, 16)]
    [InlineData(1e300, 403200, 0, 0)] // a mod's huge GridBounds: no grid, not billions of lines
    [InlineData(403200, 1e12, 0, 0)]
    [InlineData(100, 100, 0, 0)]
    public void TheGrid_IsOnlyWhatTheGameCanName(double halfWidth, double halfHeight, int columns, int rows)
    {
        var map = new WorldMapInfo("T", "T", false, [], [], [], Extent: new WorldExtent(-halfWidth, -halfHeight, halfWidth * 2, halfHeight * 2));

        Assert.Equal((columns, rows), map.GridSize);
    }

    [Fact]
    public void NoWorldData_NoMaps()
    {
        var game = GameTables();
        game.Remove("World/D_WorldData.json");

        Assert.Empty(Read(game));
    }

    [Theory]
    [InlineData("OLY_Conifer_Medium_Crocs", "Conifer Medium Crocs")]
    [InlineData("STYX_Swamp", "Swamp")]
    [InlineData("Outpost_Forest", "Outpost Forest")]
    [InlineData("Test_Zone", "Test Zone")]
    [InlineData("OLY_CentralArctic", "Central Arctic")]
    public void ZoneNames_LeaveOffTheMapsShortTag(string row, string name) => Assert.Equal(name, WorldMapCatalog.ZoneName(row));
}
