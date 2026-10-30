namespace IcarusStarlink.PakIO.World;

/// <summary>What a placed map feature is.</summary>
public enum WorldFeatureKind
{
    /// <summary>A generated cave (BP_CaveInstance_C): a pocket with one entrance.</summary>
    Cave,

    /// <summary>A hand-made cave (BP_Cave_C): bigger, often several entrances (a tunnel).</summary>
    BigCave,

    OilGeyser,
    EnzymeGeyser,

    /// <summary>A Great Hunt den entrance (BP_GH_DenEntrance_*).</summary>
    HuntDen,

    /// <summary>A spot a world boss can spawn (a quest marker tagged WorldBoss.SpawnLocation.*).</summary>
    BossSpawn,

    /// <summary>A spot a deep ore deposit can appear (which ones, and the ore, are picked when the world is made).</summary>
    DeepOreSpot,

    /// <summary>A deep ore deposit that's always there (Prometheus's high-yield ones).</summary>
    DeepOreDeposit,

    /// <summary>A spot an exotic deposit can appear (BP_IcarusMetaSpawn_C; the prospect picks a few).</summary>
    ExoticSpot,

    /// <summary>A fishing volume (BP_FishVolume_C).</summary>
    FishingSpot,

    /// <summary>A mission or quest spot (BP_QuestMarker_C), by its tag's category.</summary>
    MissionSpot,
}

/// <param name="Name">What it's called on the map ("Oil geyser", "Rock Golem den", "Base location").</param>
/// <param name="At">Where it is on the map (0-1).</param>
/// <param name="Category">A mission spot's category ("Objective · Base Location"), a boss's or exotic's kind; null otherwise.</param>
/// <param name="WormsMin">A cave's cave worms: fewest and most its spawner places.</param>
/// <param name="LengthMetres">A cave's length, from its inside boxes.</param>
/// <param name="Style">A cave's entrance style: CF forest, DC desert, AC arctic, LC lava, IC ice (from its entrance's class).</param>
/// <param name="Entrances">A big cave's entrances, on the map.</param>
public sealed record WorldFeature(WorldFeatureKind Kind, string Name, WorldMapPoint At)
{
    public string? Category { get; init; }

    public int? WormsMin { get; init; }

    public int? WormsMax { get; init; }

    public double? LengthMetres { get; init; }

    public string? Style { get; init; }

    public IReadOnlyList<WorldMapPoint>? Entrances { get; init; }

    /// <summary>
    /// A cave's tier by its cave worms (the user's rule: the good caves have more worms):
    /// 1 up to 3 worms, 2 for 4-6, 3 for 7 or more. Null for anything that isn't a cave or has no spawner.
    /// </summary>
    public int? Tier => WormsMax switch
    {
        null => null,
        <= 3 => 1,
        <= 6 => 2,
        _ => 3,
    };
}

/// <summary>A lake's edge or a river's two banks, on the map (0-1).</summary>
/// <param name="IsRiver">A river: Outline is its left bank then its right bank backwards (one closed shape).</param>
public sealed record WorldWater(bool IsRiver, IReadOnlyList<WorldMapPoint> Outline);

/// <summary>Everything placed on a world that the map can show, read from its level files.</summary>
public sealed record WorldFeatures(IReadOnlyList<WorldFeature> Features, IReadOnlyList<WorldWater> Water)
{
    public static WorldFeatures None { get; } = new([], []);
}

/// <summary>Where a world sits in world units (from D_WorldData GridBounds), to put world positions on the map.</summary>
public sealed record WorldExtent(double MinX, double MinY, double Width, double Height)
{
    /// <summary>A world position on the map (0-1); X to the right, Y down.</summary>
    public WorldMapPoint ToMap(double x, double y) => new((x - MinX) / Width, (y - MinY) / Height);

    public bool Contains(WorldMapPoint p) => p.U is >= 0 and <= 1 && p.V is >= 0 and <= 1;
}
