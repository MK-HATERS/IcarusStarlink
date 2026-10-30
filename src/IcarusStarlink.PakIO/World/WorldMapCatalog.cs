using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.World;

/// <summary>
/// The game's worlds and their zones, read from its tables:
/// D_WorldData (every world, its picture and height tiles) → D_Terrains (the name players see, the
/// biome image, which D_AISpawnConfig / D_FishSpawnConfig row it uses) → each config's zone image and
/// colour → zone row → D_AISpawnZones / D_FishSpawnZones / D_Biomes. Names come from D_BestiaryData
/// (through D_AISetup BestiaryGroup), D_Itemable (Item_&lt;fish&gt;) and D_Biomes.
/// Every table is read from the installed mods' copy first (Paks\mods: the merge and any other
/// mod pak), so the map shows the game as installed.
/// </summary>
public static partial class WorldMapCatalog
{
    /// <summary>
    /// Worlds the game has no name for yet, named by the user ("the new one is a map called ARK").
    /// A name from the game (D_Terrains TerrainName) always wins.
    /// </summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Terrain_022"] = "ARK",
    };

    /// <summary>Reads the worlds from the extracted game data and the installed mods' paks (none when contentPath isn't set).</summary>
    public static IReadOnlyList<WorldMapInfo> Read(string dataFolder, string? contentPath)
    {
        var modsFolder = contentPath is { Length: > 0 } ? Path.Combine(contentPath, "Paks", "mods") : null;
        using var mods = modsFolder is not null ? ModPakContents.TryOpen(modsFolder) : null;
        return Read(new WorldTableSource(
            relative => ReadFile(Path.Combine(dataFolder, relative.Replace('/', Path.DirectorySeparatorChar))),
            relative => mods?.TryRead("Data/" + relative)));
    }

    public static IReadOnlyList<WorldMapInfo> Read(WorldTableSource source)
    {
        var worlds = source.Table("World/D_WorldData.json");
        if (worlds is null)
        {
            return [];
        }

        var terrains = source.Table("Prospects/D_Terrains.json");
        var names = new WorldNames(source);
        var prospects = ProspectsByTerrain(source);
        var maps = new List<WorldMapInfo>();
        foreach (var (id, world) in worlds.Rows)
        {
            var terrain = terrains?.Row(id);
            var minimap = worlds.Get(world, "MinimapData") as JsonObject;
            var pictureTiles = Tiles(minimap?["MapTextures"]);
            var heightTiles = Tiles(minimap?["HeightMapTextures"]);

            var gameName = terrain is not null ? TextOf(terrains!.Get(terrain, "TerrainName")) : null;
            var unreleased = gameName?.StartsWith("[DNT]", StringComparison.OrdinalIgnoreCase) == true;
            var name = gameName is { Length: > 0 } && !unreleased
                ? gameName
                : KnownNames.GetValueOrDefault(id) ?? (unreleased ? gameName!["[DNT]".Length..].Trim() : Humanize(id));

            var layers = new List<WorldLayer>();
            if (terrain is not null)
            {
                if (RowRef(terrains!.Get(terrain, "SpawnConfig")) is { } spawnConfig
                    && SpawnLayer(source, "AI/D_AISpawnConfig.json", spawnConfig, WorldLayerKind.Creatures, names) is { } creatures)
                {
                    layers.Add(creatures);
                }

                if (RowRef(terrains.Get(terrain, "FishConfig")) is { } fishConfig
                    && SpawnLayer(source, "Fish/D_FishSpawnConfig.json", fishConfig, WorldLayerKind.Fishing, names) is { } fishing)
                {
                    layers.Add(fishing);
                }

                if (AssetPath(terrains.Get(terrain, "BiomeMap")) is { } biomeMap)
                {
                    layers.Add(new WorldLayer(WorldLayerKind.Biomes, biomeMap, names.BiomesByColor()));
                }
            }

            var (openWorld, missions, outpost) = prospects.GetValueOrDefault(id);
            maps.Add(new WorldMapInfo(id, name, terrain is null || unreleased || pictureTiles.Count == 0, pictureTiles, heightTiles, layers,
                openWorld, missions, outpost, DropSites(worlds, world)));
        }

        return maps;
    }

    /// <summary>
    /// D_WorldData DropGroups placed on the map. A world position goes to the map as
    /// u = (X - left) / width, v = (Y - top) / height, the edges from GridBounds' cells (checked on
    /// Olympus: every drop site lands in the biome its name says, e.g. 14_SnowBorder on arctic).
    /// </summary>
    private static List<WorldDropSite> DropSites(WorldTable worlds, JsonObject world)
    {
        if (Extent(worlds.Get(world, "GridBounds")) is not { } extent || worlds.Get(world, "DropGroups") is not JsonObject groups)
        {
            return [];
        }

        var sites = new List<WorldDropSite>();
        foreach (var (key, value) in groups)
        {
            if (value is not JsonObject group || group["Locations"] is not JsonArray locations)
            {
                continue;
            }

            var spots = locations.OfType<JsonObject>()
                .Select(l => (X: Number(l["X"]), Y: Number(l["Y"])))
                .Where(l => l.X is not null && l.Y is not null)
                .Select(l => new WorldMapPoint((l.X!.Value - extent.MinX) / extent.Width, (l.Y!.Value - extent.MinY) / extent.Height))
                .Where(p => p.U is >= 0 and <= 1 && p.V is >= 0 and <= 1)
                .ToList();
            if (spots.Count == 0)
            {
                continue;
            }

            var groupName = group["GroupName"] is JsonValue g && g.TryGetValue<string>(out var s) ? s : key;
            var number = int.TryParse(key, out var n) ? n : 0;
            sites.Add(new WorldDropSite(number, DropSiteName(groupName), spots));
        }

        return [.. sites.OrderBy(s => s.Number)];
    }

    /// <summary>The map's edges in world units, from its GridBounds cells (each an Origin and a BoxExtent).</summary>
    private static (double MinX, double MinY, double Width, double Height)? Extent(JsonNode? gridBounds)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var cell in gridBounds as JsonArray ?? [])
        {
            if (cell is JsonObject c && c["Origin"] is JsonObject o && c["BoxExtent"] is JsonObject e
                && Number(o["X"]) is { } x && Number(o["Y"]) is { } y && Number(e["X"]) is { } ex && Number(e["Y"]) is { } ey)
            {
                minX = Math.Min(minX, x - ex);
                maxX = Math.Max(maxX, x + ex);
                minY = Math.Min(minY, y - ey);
                maxY = Math.Max(maxY, y + ey);
            }
        }

        return maxX > minX && maxY > minY ? (minX, minY, maxX - minX, maxY - minY) : null;
    }

    /// <summary>"1_DesertSteps" → "Desert Steps", "25_BW3" → "BW3": the number in front left off, words split.</summary>
    internal static string DropSiteName(string groupName)
    {
        var underscore = groupName.IndexOf('_');
        var name = underscore > 0 && groupName[..underscore].All(char.IsDigit) ? groupName[(underscore + 1)..] : groupName;
        return SplitWords().Replace(name, " ").Replace('_', ' ').Trim();
    }

    /// <summary>A lower-case letter followed by an upper-case one ("DesertSteps").</summary>
    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex SplitWords();

    /// <summary>
    /// What each terrain is played as, from D_ProspectList's prospects on it (disabled ones left out):
    /// bIsOpenWorld = open world, else bIsPersistent = outpost, else a mission.
    /// </summary>
    private static Dictionary<string, (bool OpenWorld, int Missions, bool Outpost)> ProspectsByTerrain(WorldTableSource source)
    {
        var byTerrain = new Dictionary<string, (bool OpenWorld, int Missions, bool Outpost)>(StringComparer.OrdinalIgnoreCase);
        var list = source.Table("Prospects/D_ProspectList.json");
        foreach (var (_, prospect) in list?.Rows ?? [])
        {
            if (IsTrue(list!.Get(prospect, "bDisabled")) || RowRef(list.Get(prospect, "Terrain")) is not { } terrain)
            {
                continue;
            }

            var (openWorld, missions, outpost) = byTerrain.GetValueOrDefault(terrain);
            if (IsTrue(list.Get(prospect, "bIsOpenWorld")))
            {
                openWorld = true;
            }
            else if (IsTrue(list.Get(prospect, "bIsPersistent")))
            {
                outpost = true;
            }
            else
            {
                missions++;
            }

            byTerrain[terrain] = (openWorld, missions, outpost);
        }

        return byTerrain;
    }

    private static bool IsTrue(JsonNode? value) => value is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static WorldLayer? SpawnLayer(WorldTableSource source, string configTable, string configRow, WorldLayerKind kind, WorldNames names)
    {
        var configs = source.Table(configTable);
        if (configs?.Row(configRow) is not { } config)
        {
            return null;
        }

        var zoneTable = kind == WorldLayerKind.Creatures ? source.Table("AI/D_AISpawnZones.json") : source.Table("Fish/D_FishSpawnZones.json");
        var zones = new Dictionary<uint, WorldZone>();
        foreach (var entry in configs.Get(config, "SpawnZones") as JsonArray ?? [])
        {
            if (entry is not JsonObject e || ColorKey(e["Color"]) is not { } color || RowRef(e["SpawnZone"]) is not { } zoneRow
                || zoneTable is null || zones.ContainsKey(color))
            {
                continue;
            }

            if (zoneTable.Row(zoneRow) is { } row)
            {
                var gameRow = zoneTable.GameRow(zoneRow);
                var changed = zoneTable.IsFromMods(zoneRow) && (gameRow is null || !JsonNode.DeepEquals(row, gameRow));
                zones[color] = kind == WorldLayerKind.Creatures
                    ? CreatureZone(zoneTable, zoneRow, row, changed ? gameRow : null, changed, names)
                    : FishZone(zoneTable, zoneRow, row, changed ? gameRow : null, changed, names);
            }
        }

        return new WorldLayer(kind, AssetPath(configs.Get(config, "SpawnMap")), zones);
    }

    private static WorldZone CreatureZone(WorldTable table, string zoneRow, JsonObject row, JsonObject? gameRow, bool changed, WorldNames names)
    {
        List<WorldZoneEntry> Entries(JsonObject r)
        {
            var creatures = table.Get(r, "Creatures") as JsonObject;
            var list = creatures?["AISpawnList"] as JsonArray ?? (table.Defaults?["Creatures"] as JsonObject)?["AISpawnList"] as JsonArray ?? [];
            return [.. list.OfType<JsonObject>()
                .Select(e => (Row: Value(e["AISetup"]), Weight: Number(e["SpawnWeight"]) ?? 0))
                .Where(e => e.Row is { Length: > 0 } && e.Row != "None" && e.Weight > 0)
                .Select(e => new WorldZoneEntry(e.Row!, names.Creature(e.Row!), e.Weight))];
        }

        return new WorldZone(zoneRow, ZoneName(zoneRow), Entries(row), null,
            (int?)Number(table.Get(row, "MinLevel")), (int?)Number(table.Get(row, "MaxLevel")),
            changed, gameRow is not null ? Entries(gameRow) : null);
    }

    private static WorldZone FishZone(WorldTable table, string zoneRow, JsonObject row, JsonObject? gameRow, bool changed, WorldNames names)
    {
        List<WorldZoneEntry> Entries(JsonObject r) =>
            [.. (table.Get(r, "SpawnList") as JsonObject ?? [])
                .Select(kv => (Row: ValueInKey().Match(kv.Key) is { Success: true } m ? m.Groups[1].Value : kv.Key, Weight: Number(kv.Value) ?? 0))
                .Where(e => e.Weight > 0)
                .Select(e => new WorldZoneEntry(e.Row, names.Fish(e.Row), e.Weight))];

        return new WorldZone(zoneRow, ZoneName(zoneRow), Entries(row), Number(table.Get(row, "ZoneFishQuality")),
            ChangedByMods: changed, GameEntries: gameRow is not null ? Entries(gameRow) : null);
    }

    /// <summary>A fish spawn list key: (Value="Fish_01").</summary>
    [GeneratedRegex("Value=\"([^\"]*)\"")]
    private static partial Regex ValueInKey();

    private static IReadOnlyList<WorldTile> Tiles(JsonNode? list) =>
        [.. (list as JsonArray ?? [])
            .Select(n => AssetPath(n))
            .Where(p => p is not null)
            .Select(p => (Path: p!, Match: TileXY().Match(p!)))
            .Where(t => t.Match.Success)
            .Select(t => new WorldTile(int.Parse(t.Match.Groups[1].Value), int.Parse(t.Match.Groups[2].Value), t.Path))];

    [GeneratedRegex(@"_x(\d+)_y(\d+)\.uasset$", RegexOptions.IgnoreCase)]
    private static partial Regex TileXY();

    /// <summary>"/Game/Heatmaps/X/GT_Y.GT_Y" → "Heatmaps/X/GT_Y.uasset"; null for "None" or anything else.</summary>
    internal static string? AssetPath(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("/Game/", StringComparison.OrdinalIgnoreCase)
            ? PlayerGrowthReader.CurveAssetPath(s)
            : null;

    /// <summary>{"R":..,"G":..,"B":..} → 0xRRGGBB (missing channels are 0, as the game reads them).</summary>
    internal static uint? ColorKey(JsonNode? value) =>
        value is JsonObject c
            ? ((uint)(Number(c["R"]) ?? 0) << 16) | ((uint)(Number(c["G"]) ?? 0) << 8) | (uint)(Number(c["B"]) ?? 0)
            : null;

    internal static double? Number(JsonNode? value)
    {
        if (value is not JsonValue v)
        {
            return null;
        }

        // A number built in code (JsonValue<int>) fails TryGetValue<double>: read it from its text.
        return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>{"RowName": "X"} → "X"; null for none.</summary>
    private static string? RowRef(JsonNode? value) =>
        value is JsonObject o && o["RowName"] is JsonValue v && v.TryGetValue<string>(out var s) && s is { Length: > 0 } && s != "None" ? s : null;

    /// <summary>{"Value": "X"} → "X".</summary>
    private static string? Value(JsonNode? value) =>
        value is JsonObject o && o["Value"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>NSLOCTEXT(...) or INVTEXT("...") → the text; a plain string as it is.</summary>
    internal static string? TextOf(JsonNode? value)
    {
        if (value is not JsonValue v || !v.TryGetValue<string>(out var s))
        {
            return null;
        }

        if (NsLocText.TextOf(s) is { } text)
        {
            return text;
        }

        return InvText().Match(s.Trim()) is { Success: true } m ? m.Groups[1].Value : s;
    }

    [GeneratedRegex("""^INVTEXT\("((?:[^"\\]|\\.)*)"\)$""")]
    private static partial Regex InvText();

    /// <summary>"Deer_Conifer_Large" → "Deer Conifer Large".</summary>
    internal static string Humanize(string row) => row.Replace('_', ' ').Trim();

    /// <summary>"OLY_Conifer_Medium_Crocs" → "Conifer Medium Crocs", "OLY_CentralArctic" → "Central Arctic": the map's own short tag in front is left off.</summary>
    internal static string ZoneName(string row)
    {
        var underscore = row.IndexOf('_');
        var tag = underscore > 0 ? row[..underscore] : "";
        return SplitWords().Replace(Humanize(tag.Length is >= 2 and <= 5 && tag.All(char.IsUpper) ? row[(underscore + 1)..] : row), " ");
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Where the tables come from: the game's extracted data and the installed mods' copy, both by
/// "&lt;Folder&gt;/&lt;Table&gt;.json" ("AI/D_AISpawnZones.json"). Tests pass their own.
/// </summary>
public sealed class WorldTableSource(Func<string, byte[]?> readGame, Func<string, byte[]?> readMods)
{
    private readonly Dictionary<string, WorldTable?> _tables = new(StringComparer.OrdinalIgnoreCase);

    public WorldTable? Table(string relativePath)
    {
        if (!_tables.TryGetValue(relativePath, out var table))
        {
            table = WorldTable.From(readGame(relativePath), readMods(relativePath));
            _tables[relativePath] = table;
        }

        return table;
    }
}

/// <summary>A game table with the installed mods' copy over it: a row is the mods' when their copy has it, else the game's.</summary>
public sealed class WorldTable
{
    private readonly Dictionary<string, JsonObject> _game;
    private readonly Dictionary<string, JsonObject> _mods;

    private WorldTable(Dictionary<string, JsonObject> game, Dictionary<string, JsonObject> mods, JsonObject? defaults)
    {
        _game = game;
        _mods = mods;
        Defaults = defaults;
    }

    public JsonObject? Defaults { get; }

    /// <summary>Every row, in the game's order, then rows only the mods have.</summary>
    public IEnumerable<(string Name, JsonObject Row)> Rows =>
        _game.Keys.Concat(_mods.Keys.Where(k => !_game.ContainsKey(k))).Select(k => (k, Row(k)!));

    public JsonObject? Row(string name) => _mods.GetValueOrDefault(name) ?? _game.GetValueOrDefault(name);

    public JsonObject? GameRow(string name) => _game.GetValueOrDefault(name);

    public bool IsFromMods(string name) => _mods.ContainsKey(name);

    /// <summary>The row's field, or the table's Defaults for it.</summary>
    public JsonNode? Get(JsonObject row, string field) => row[field] ?? Defaults?[field];

    internal static WorldTable? From(byte[]? game, byte[]? mods)
    {
        var (gameRows, gameDefaults) = Parse(game);
        var (modRows, modDefaults) = Parse(mods);
        return gameRows is null && modRows is null
            ? null
            : new WorldTable(gameRows ?? new(StringComparer.OrdinalIgnoreCase), modRows ?? new(StringComparer.OrdinalIgnoreCase), modDefaults ?? gameDefaults);
    }

    private static (Dictionary<string, JsonObject>? Rows, JsonObject? Defaults) Parse(byte[]? json)
    {
        if (json is null)
        {
            return (null, null);
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["Rows"] is not JsonArray rows)
            {
                return (null, null);
            }

            var byName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (row["Name"] is JsonValue n && n.TryGetValue<string>(out var name) && name is { Length: > 0 })
                {
                    byName.TryAdd(name, row);
                }
            }

            return (byName, root["Defaults"] as JsonObject);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>The names players see for creatures, fish and biomes, read once per catalog read.</summary>
internal sealed class WorldNames(WorldTableSource source)
{
    private Dictionary<string, string>? _creatures;
    private Dictionary<uint, WorldZone>? _biomes;

    public string Creature(string aiSetupRow)
    {
        _creatures ??= CreatureNames();
        return _creatures.GetValueOrDefault(aiSetupRow) ?? WorldMapCatalog.Humanize(aiSetupRow);
    }

    public string Fish(string fishRow)
    {
        var itemable = source.Table("Traits/D_Itemable.json");
        return itemable?.Row("Item_" + fishRow) is { } row && WorldMapCatalog.TextOf(itemable.Get(row, "DisplayName")) is { Length: > 0 } name
            ? name
            : WorldMapCatalog.Humanize(fishRow);
    }

    /// <summary>D_AISetup BestiaryGroup → D_BestiaryData CreatureName ("Conifer_Wolf" → "Wolf").</summary>
    private Dictionary<string, string> CreatureNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var setup = source.Table("AI/D_AISetup.json");
        var bestiary = source.Table("Bestiary/D_BestiaryData.json");
        if (setup is null || bestiary is null)
        {
            return names;
        }

        foreach (var (row, data) in setup.Rows)
        {
            if (setup.Get(data, "BestiaryGroup") is JsonObject group && group["RowName"] is JsonValue g && g.TryGetValue<string>(out var groupRow)
                && bestiary.Row(groupRow) is { } entry && WorldMapCatalog.TextOf(bestiary.Get(entry, "CreatureName")) is { Length: > 0 } name)
            {
                names[row] = name;
            }
        }

        return names;
    }

    /// <summary>Every D_Biomes row by its colour on a biome image; one zone per biome, named as the game names it.</summary>
    public IReadOnlyDictionary<uint, WorldZone> BiomesByColor()
    {
        if (_biomes is not null)
        {
            return _biomes;
        }

        _biomes = [];
        var biomes = source.Table("Prospects/D_Biomes.json");
        foreach (var (row, data) in biomes?.Rows ?? [])
        {
            // Only a colour the row sets itself: the Defaults' (all 0) would make every biome without one "black".
            if (WorldMapCatalog.ColorKey(data["Color"]) is { } color && !_biomes.ContainsKey(color) && row != "NoBiome")
            {
                var name = WorldMapCatalog.TextOf(biomes!.Get(data, "DisplayName")) ?? WorldMapCatalog.Humanize(row);
                _biomes[color] = new WorldZone(row, name, []);
            }
        }

        return _biomes;
    }
}
