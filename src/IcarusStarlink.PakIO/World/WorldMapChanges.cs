using System.Text.Json.Serialization;

namespace IcarusStarlink.PakIO.World;

/// <summary>A creature or fishing zone whose contents changed between two reloads.</summary>
/// <param name="Layer">The layer it's in ("Creatures", "Fishing").</param>
/// <param name="Row">The zone's row.</param>
/// <param name="Name">The zone's name as the map shows it.</param>
/// <param name="What">"added", "gone", or what changed in it ("Desert Wolf added, Bear gone", "weights changed").</param>
public sealed record WorldZoneChange(string Layer, string Row, string Name, string What)
{
    /// <summary>"Conifer Hard added", "Conifer Hard: Desert Wolf added, Bear gone".</summary>
    [JsonIgnore]
    public string Summary => What is "added" or "gone" ? $"{Name} {What}" : $"{Name}: {What}";
}

/// <summary>
/// Where one of a world's images (its picture, or a zone image) differs from the one drawn before:
/// the whole of it, or some cells of a grid laid over it.
/// </summary>
public sealed record WorldImageChange
{
    /// <summary>Which image: "Picture", or the layer ("Creatures", "Fishing", "Biomes").</summary>
    public string What { get; init; } = "";

    /// <summary>All of it changed (or its size did, or it's new or gone): no cells are listed.</summary>
    public bool Whole { get; init; }

    /// <summary>"new" when the world had no such image before, "gone" when it has none now; null otherwise.</summary>
    public string? Note { get; init; }

    /// <summary>The grid the cells are counted in (the in-game grid when the world has one).</summary>
    public int Columns { get; init; }

    public int Rows { get; init; }

    /// <summary>The changed cells, each as row * Columns + column (left to right, top to bottom).</summary>
    public List<int> Cells { get; init; } = [];
}

/// <summary>The game files of one kind ("Picture tiles", "Level files"…) that changed, came or went, by short name.</summary>
public sealed record WorldFileChange
{
    /// <summary>The most names kept per list (the counts still say how many there were).</summary>
    public const int MaxNames = 40;

    public string Kind { get; init; } = "";

    public List<string> Changed { get; init; } = [];

    public List<string> Added { get; init; } = [];

    public List<string> Gone { get; init; } = [];

    public int ChangedCount { get; init; }

    public int AddedCount { get; init; }

    public int GoneCount { get; init; }
}

/// <summary>
/// What changed on one world the last time a Reload found it changed: where its picture and zone
/// shapes differ, which game files changed, the zones whose spawns changed, and the caves, geysers
/// and other spots that came or went; or that the world is new. Kept in the index until the world
/// changes again.
/// </summary>
public sealed record WorldMapChangeRecord
{
    /// <summary>The most spots kept per list (the counts still say how many there were).</summary>
    public const int MaxSpots = 300;

    /// <summary>When the Reload that found it ran (UTC).</summary>
    public DateTime When { get; init; }

    /// <summary>The game added this world since the Reload before.</summary>
    public bool NewMap { get; init; }

    /// <summary>The game replaced some of the world's files (false: only its zones' spawns changed).</summary>
    public bool FilesChanged { get; init; }

    /// <summary>Where the picture differs; null when it looks the same.</summary>
    public WorldImageChange? Picture { get; init; }

    /// <summary>The zone images whose shapes differ.</summary>
    public List<WorldImageChange> Shapes { get; init; } = [];

    /// <summary>The game files that changed, came or went, by kind; empty when they weren't known before.</summary>
    public List<WorldFileChange> Files { get; init; } = [];

    public List<WorldZoneChange> Zones { get; init; } = [];

    /// <summary>Spots the world has now and didn't have before (the first MaxSpots).</summary>
    public List<WorldFeature> Added { get; init; } = [];

    /// <summary>Spots the world had before and no longer has (the first MaxSpots).</summary>
    public List<WorldFeature> Removed { get; init; } = [];

    /// <summary>How many spots were added in all.</summary>
    public int AddedCount { get; init; }

    /// <summary>How many spots were removed in all.</summary>
    public int RemovedCount { get; init; }
}

public static class WorldMapChanges
{
    /// <summary>
    /// The spots in <paramref name="after"/> that weren't in <paramref name="before"/>, and the other
    /// way round. A spot is the same when its kind, name, category and place match (to a ten-thousandth
    /// of the map: under a metre on the big worlds), so a moved spot is one removed and one added.
    /// Nothing is compared when either list is empty: a world whose spots couldn't be read (or were
    /// never read) would otherwise show every spot as new or gone.
    /// </summary>
    public static (List<WorldFeature> Added, List<WorldFeature> Removed) Spots(IReadOnlyList<WorldFeature> before, IReadOnlyList<WorldFeature> after)
    {
        if (before.Count == 0 || after.Count == 0)
        {
            return ([], []);
        }

        // Counted, not just looked up: a world can have two alike spots at one place.
        var left = new Dictionary<SpotKey, int>();
        foreach (var spot in before)
        {
            var key = SpotKey.Of(spot);
            left[key] = left.GetValueOrDefault(key) + 1;
        }

        var added = new List<WorldFeature>();
        foreach (var spot in after)
        {
            var key = SpotKey.Of(spot);
            if (left.GetValueOrDefault(key) > 0)
            {
                left[key]--;
            }
            else
            {
                added.Add(spot);
            }
        }

        // What's left over went (of several alike ones, as many as are left over).
        var removed = new List<WorldFeature>();
        var kept = new Dictionary<SpotKey, int>();
        foreach (var spot in before)
        {
            var key = SpotKey.Of(spot);
            var seen = kept.GetValueOrDefault(key);
            kept[key] = seen + 1;
            if (seen < left.GetValueOrDefault(key))
            {
                removed.Add(spot);
            }
        }

        return (added, removed);
    }

    private readonly record struct SpotKey(WorldFeatureKind Kind, string Name, string? Category, long U, long V)
    {
        public static SpotKey Of(WorldFeature spot) =>
            new(spot.Kind, spot.Name, spot.Category, (long)Math.Round(spot.At.U * 10000), (long)Math.Round(spot.At.V * 10000));
    }
}
