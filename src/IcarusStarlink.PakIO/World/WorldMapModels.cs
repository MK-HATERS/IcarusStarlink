namespace IcarusStarlink.PakIO.World;

/// <summary>What a map layer shows: where creatures spawn, where fish bite, or the biomes.</summary>
public enum WorldLayerKind
{
    Creatures,
    Fishing,
    Biomes,
}

/// <summary>One thing in a zone: a creature or fish (its row and the name players see) and its spawn weight.</summary>
public sealed record WorldZoneEntry(string Row, string Name, double Weight);

/// <param name="Row">The zone's row (D_AISpawnZones, D_FishSpawnZones or D_Biomes).</param>
/// <param name="Entries">What spawns there, as installed (the mods' copy of the table when there is one).</param>
/// <param name="FishQuality">D_FishSpawnZones ZoneFishQuality (0-1); null for other layers.</param>
/// <param name="ChangedByMods">An installed mod's copy of the row differs from the game's.</param>
/// <param name="GameEntries">The game's own entries, when a mod changed the row.</param>
public sealed record WorldZone(
    string Row, string Name, IReadOnlyList<WorldZoneEntry> Entries, double? FishQuality = null,
    int? MinLevel = null, int? MaxLevel = null, bool ChangedByMods = false, IReadOnlyList<WorldZoneEntry>? GameEntries = null)
{
    /// <summary>The zone's contents as one line ("Deer:6|Wolf:8"), to tell whether it changed between two reloads.</summary>
    public string Fingerprint() =>
        string.Join("|", Entries.OrderBy(e => e.Row, StringComparer.OrdinalIgnoreCase).Select(e => FormattableString.Invariant($"{e.Row}:{e.Weight:0.###}")))
        + (FishQuality is { } q ? FormattableString.Invariant($"|~q{q:0.###}") : "");
}

/// <param name="HeatmapAssetPath">The zone image (a GameplayTexture), Content-relative ("Heatmaps/Terrain_016_OLY/GT_Terrain016_Fish.uasset"); null when the map has none.</param>
/// <param name="ZonesByColor">Zone by the colour that marks it on the zone image (0xRRGGBB).</param>
public sealed record WorldLayer(WorldLayerKind Kind, string? HeatmapAssetPath, IReadOnlyDictionary<uint, WorldZone> ZonesByColor);

/// <summary>A point on the map, 0-1 from its left and top.</summary>
public readonly record struct WorldMapPoint(double U, double V);

/// <summary>
/// A dropship drop site (D_WorldData DropGroups): its name ("Desert Steps" from "1_DesertSteps"),
/// its number in the game's list, and the landing spots in it.
/// </summary>
public sealed record WorldDropSite(int Number, string Name, IReadOnlyList<WorldMapPoint> Spots)
{
    /// <summary>The middle of its landing spots.</summary>
    public WorldMapPoint Centre => new(Spots.Average(s => s.U), Spots.Average(s => s.V));
}

/// <summary>One tile of a map's picture: x/y as the game names it (_x3_y5) and its Content-relative asset path.</summary>
public sealed record WorldTile(int X, int Y, string AssetPath);

/// <param name="Id">The D_WorldData row ("Terrain_016", "Outpost_009").</param>
/// <param name="Name">The name players see ("Olympus"), or a plain one made from the id.</param>
/// <param name="InDevelopment">The game has the world but doesn't offer it yet (no D_Terrains row, a "[DNT]" name, or no map picture listed).</param>
/// <param name="PictureTiles">D_WorldData MinimapData MapTextures.</param>
/// <param name="HeightTiles">D_WorldData MinimapData HeightMapTextures: drawn as a relief map when the picture's tiles aren't in the game.</param>
/// <param name="OpenWorld">D_ProspectList has an open world prospect on it (bIsOpenWorld).</param>
/// <param name="Missions">How many mission prospects it has (neither open world nor persistent).</param>
/// <param name="Outpost">D_ProspectList has an outpost on it (persistent, not open world).</param>
public sealed record WorldMapInfo(
    string Id, string Name, bool InDevelopment,
    IReadOnlyList<WorldTile> PictureTiles, IReadOnlyList<WorldTile> HeightTiles, IReadOnlyList<WorldLayer> Layers,
    bool OpenWorld = false, int Missions = 0, bool Outpost = false, IReadOnlyList<WorldDropSite>? Drops = null,
    WorldExtent? Extent = null, IReadOnlyList<string>? LevelFiles = null,
    IReadOnlyDictionary<string, IReadOnlyList<WorldZoneEntry>>? CaveOreChances = null,
    IReadOnlyDictionary<string, string>? MissionNameLookup = null)
{
    /// <summary>This world's mission names to match spots against (WorldMissionNames.Read).</summary>
    public IReadOnlyDictionary<string, string> MissionNames => MissionNameLookup ?? new Dictionary<string, string>();

    /// <summary>
    /// The ores a cave can have by its entrance style (CF, DC, AC), from D_VoxelDistributionRegion's
    /// Conifer/Desert/Arctic_Caves_1 rows (weights); empty when the game lists none for a style.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<WorldZoneEntry>> CaveOres => CaveOreChances ?? new Dictionary<string, IReadOnlyList<WorldZoneEntry>>();

    /// <summary>
    /// The level files its placed features are read from (D_WorldData MainLevel + GeneratedLevels),
    /// Content-relative ("Maps/Terrain_016_OLY/Terrain_016.umap").
    /// </summary>
    public IReadOnlyList<string> Levels => LevelFiles ?? [];

    /// <summary>The in-game grid's square, in world units (proven: every "OLY_I7"-style exotic spot id matches it).</summary>
    public const double GridSquare = 50400;

    /// <summary>
    /// The grid's squares across and down (16 x 16 on Olympus); 0 without an extent, or when it
    /// wouldn't fit the game's naming (letters A-Z across, up to 99 down): a mod's huge GridBounds
    /// would otherwise ask the page for billions of grid lines.
    /// </summary>
    public (int Columns, int Rows) GridSize
    {
        get
        {
            if (Extent is not { } e)
            {
                return (0, 0);
            }

            var columns = Math.Round(e.Width / GridSquare);
            var rows = Math.Round(e.Height / GridSquare);
            return columns is >= 1 and <= 26 && rows is >= 1 and <= 99 ? ((int)columns, (int)rows) : (0, 0);
        }
    }

    /// <summary>Where dropships land (D_WorldData DropGroups); empty when the game lists none.</summary>
    public IReadOnlyList<WorldDropSite> DropSites => Drops ?? [];

    public WorldLayer? Layer(WorldLayerKind kind) => Layers.FirstOrDefault(l => l.Kind == kind);

    /// <summary>What it's played as: "Open world · 80 missions", "Outpost", "Missions (3)"; empty when no prospect uses it.</summary>
    public string KindText
    {
        get
        {
            var parts = new List<string>();
            if (OpenWorld)
            {
                parts.Add("Open world");
            }

            if (Outpost)
            {
                parts.Add("Outpost");
            }

            if (Missions > 0)
            {
                parts.Add(Missions == 1 ? "1 mission" : $"{Missions} missions");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>For the map picker's order: open world maps, then mission-only maps, then outposts, then the rest.</summary>
    public int KindOrder => OpenWorld ? 0 : Missions > 0 ? 1 : Outpost ? 2 : 3;
}
