using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.GameplayTags;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;

namespace IcarusStarlink.PakIO.World;

/// <summary>
/// Reads what's placed on a world from its level files (no table holds these positions): the main
/// level (geysers, Great Hunt dens, quest markers incl. world boss spots, deep ore and exotic spots)
/// and its generated levels (caves with their entrances and cave worm spawners, fishing volumes,
/// lakes, rivers). An actor's position is its root component's RelativeLocation, composed up its
/// AttachParent chain. Uses its own pak reader for the read, 8 levels at a time (about 100 s for a
/// big map), and lets it go after.
/// </summary>
public static partial class WorldFeatureReader
{
    private static readonly VersionContainer Versions = new(EGame.GAME_UE4_27);

    /// <summary>A cave's worm spawner within this many units (60 m) of the cave belongs to it.</summary>
    private const double SpawnerReach = 6000;

    /// <summary>The features of one world, or None when its levels can't be read.</summary>
    public static WorldFeatures Read(string paksFolder, WorldMapInfo map)
    {
        if (map.Extent is not { } extent || map.Levels.Count == 0 || !Directory.Exists(paksFolder))
        {
            return WorldFeatures.None;
        }

        using var provider = new DefaultFileProvider(paksFolder, SearchOption.TopDirectoryOnly, Versions, StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();

        var found = new ConcurrentBag<Found>();
        Parallel.ForEach(map.Levels, new ParallelOptions { MaxDegreeOfParallelism = 8 }, level =>
        {
            var key = "Icarus/Content/" + level.Replace('\\', '/').TrimStart('/');
            if (!provider.Files.ContainsKey(key))
            {
                return;
            }

            try
            {
                foreach (var export in provider.LoadPackage(key).GetExports())
                {
                    if (Classify(export) is { } item)
                    {
                        found.Add(item);
                    }
                }
            }
            catch (Exception)
            {
                // One level that can't be read leaves out only what's in it.
            }
        });

        return Build([.. found], extent);
    }

    /// <summary>What one export is, with its world position and what's needed from it; null for anything else.</summary>
    private sealed record Found(string Class, FVector At)
    {
        public string Name { get; init; } = "";
        public string? Tag { get; init; }
        public string? Row { get; init; }
        public (int Min, int Max)? Worms { get; init; }
        public double? LengthUnits { get; init; }
        public IReadOnlyList<FVector>? Points { get; init; }
        public IReadOnlyList<FVector>? Points2 { get; init; }
    }

    [GeneratedRegex(@"^(BP_CaveInstance_C|BP_Cave_C|BP_CaveEntrance_\w+|CaveEntrance_C|BP_IcarusCaveAISpawner_C|BP_OilGeyser_C|BP_EnzymeGeyser_C|BP_GH_DenEntrance_\w+|BP_QuestMarker_C|BP_DeepOreDepositSpawn_C|BP_Deep_Mining_Ore_Deposit\w*|BP_IcarusMetaSpawn_C|BP_MetaDeposit_\w+|BP_ExoticPlantSpawn_C|BP_FishVolume_C|BP_InteractableLake_C|BP_InteractableRiver_C)$")]
    private static partial Regex Wanted();

    private static Found? Classify(UObject export)
    {
        var cls = export.ExportType;
        if (!Wanted().IsMatch(cls) || ActorLocation(export) is not { } at)
        {
            return null;
        }

        var found = new Found(cls, at) { Name = export.Name };
        switch (cls)
        {
            case "BP_IcarusCaveAISpawner_C":
                return Json(export, "CaveActorSpawnMap") is JArray spawns ? found with { Worms = WormCount(spawns) } : found;
            case "BP_CaveInstance_C":
                return found with { LengthUnits = Length(Json(export, "VolumeData") as JArray, v => v["Transform"]) };
            case "BP_Cave_C":
                return found with
                {
                    LengthUnits = Length(Json(export, "CachedLocations") as JArray, v => v),
                    Points = [.. (Json(export, "CachedEntranceTransforms") as JArray ?? []).Select(t => Vector(t["Translation"])).OfType<FVector>()],
                };
            case "BP_EnzymeGeyser_C":
                return found with { Row = (Json(export, "HordeRowHandle") as JObject)?["RowName"]?.ToString() };
            case "BP_QuestMarker_C":
                return found with { Tag = Tags(export).FirstOrDefault() };
            case "BP_IcarusMetaSpawn_C":
                return found with { Row = (Json(export, "Meta Node Handle") as JObject)?["RowName"]?.ToString() };
            case "BP_InteractableLake_C":
                return found with { Points = Vectors(Json(export, "EdgeSplinePoints")) };
            case "BP_InteractableRiver_C":
                return found with { Points = Vectors(Json(export, "LeftSplinePoints")), Points2 = Vectors(Json(export, "RightSplinePoints")) };
            default:
                return found;
        }
    }

    private static WorldFeatures Build(IReadOnlyList<Found> found, WorldExtent extent)
    {
        WorldMapPoint P(FVector v) => extent.ToMap(v.X, v.Y);
        var features = new List<WorldFeature>();
        var spawners = found.Where(f => f.Class == "BP_IcarusCaveAISpawner_C" && f.Worms is not null).ToList();
        var entrances = found.Where(f => f.Class.StartsWith("BP_CaveEntrance_", StringComparison.Ordinal) || f.Class == "CaveEntrance_C").ToList();

        // A cave's spawner and entrance are its own actors: the nearest within reach of the cave (a big
        // cave: of any of its entrances, and further, as its middle can be far from them).
        (int Min, int Max)? WormsNear(IReadOnlyList<FVector> points, double reach) =>
            spawners.Select(s => (s.Worms, Distance: points.Min(p => Distance(s.At, p)))).Where(s => s.Distance < reach)
                .OrderBy(s => s.Distance).Select(s => s.Worms).FirstOrDefault();

        string? StyleNear(IReadOnlyList<FVector> points, double reach) =>
            entrances.Select(e => (e.Class, Distance: points.Min(p => Distance(e.At, p)))).Where(e => e.Distance < reach)
                .OrderBy(e => e.Distance).Select(e => EntranceStyle().Match(e.Class) is { Success: true } m ? m.Groups[1].Value : null)
                .FirstOrDefault(s => s is not null);

        // The same actor can sit in more than one level file (a big cave in two generated levels): one each.
        var unique = found
            .GroupBy(f => (f.Class, X: Math.Round(f.At.X / 100), Y: Math.Round(f.At.Y / 100)))
            .Select(g => g.First())
            .ToList();
        foreach (var f in unique)
        {
            var at = P(f.At);
            if (!extent.Contains(at))
            {
                continue;
            }

            WorldFeature? feature = f.Class switch
            {
                "BP_CaveInstance_C" => CaveFeature(WorldFeatureKind.Cave, "Cave", at, WormsNear([f.At], SpawnerReach), f.LengthUnits,
                    StyleNear([f.At], SpawnerReach * 3), null),
                "BP_Cave_C" => CaveFeature(WorldFeatureKind.BigCave, "Big cave", at,
                    WormsNear([f.At, .. f.Points ?? []], SpawnerReach * 3), f.LengthUnits, StyleNear([f.At, .. f.Points ?? []], SpawnerReach * 3),
                    f.Points?.Select(P).Where(extent.Contains).ToList()),
                "BP_OilGeyser_C" => new(WorldFeatureKind.OilGeyser, "Oil geyser", at),
                "BP_EnzymeGeyser_C" => new(WorldFeatureKind.EnzymeGeyser, "Enzyme geyser", at) { Category = f.Row is { } row ? WorldMapCatalog.Humanize(row) : null },
                _ when f.Class.StartsWith("BP_GH_DenEntrance_", StringComparison.Ordinal) =>
                    new(WorldFeatureKind.HuntDen, SplitWords(f.Class["BP_GH_DenEntrance_".Length..^2].Replace('_', ' ')) + " den", at),
                "BP_QuestMarker_C" when f.Tag?.StartsWith("WorldBoss.SpawnLocation.", StringComparison.OrdinalIgnoreCase) == true =>
                    new(WorldFeatureKind.BossSpawn, SplitWords(f.Tag["WorldBoss.SpawnLocation.".Length..]) + " spawn spot", at),
                "BP_QuestMarker_C" => new(WorldFeatureKind.MissionSpot, MissionName(f.Tag, f.Name), at) { Category = MissionCategory(f.Tag) },
                "BP_DeepOreDepositSpawn_C" => new(WorldFeatureKind.DeepOreSpot, "Possible deep ore spot", at),
                _ when f.Class.StartsWith("BP_Deep_Mining_Ore_Deposit", StringComparison.Ordinal) => new(WorldFeatureKind.DeepOreDeposit, "Deep ore deposit", at),
                "BP_IcarusMetaSpawn_C" => new(WorldFeatureKind.ExoticSpot, "Possible exotic spot", at)
                {
                    Category = f.Row is { } meta ? WorldMapCatalog.Humanize(meta.Replace("MetaDeposit_", "")) : null,
                },
                _ when f.Class.StartsWith("BP_MetaDeposit_", StringComparison.Ordinal) =>
                    new(WorldFeatureKind.ExoticSpot, SplitWords(f.Class["BP_MetaDeposit_".Length..^2].Replace('_', ' ')) + " deposit", at),
                "BP_ExoticPlantSpawn_C" => new(WorldFeatureKind.ExoticSpot, "Possible exotic plant spot", at) { Category = "Plant" },
                "BP_FishVolume_C" => new(WorldFeatureKind.FishingSpot, "Fishing spot", at),
                _ => null,
            };

            if (feature is not null)
            {
                features.Add(feature);
            }
        }

        var water = new List<WorldWater>();
        foreach (var f in unique.Where(f => f.Class is "BP_InteractableLake_C" or "BP_InteractableRiver_C"))
        {
            var outline = f.Class == "BP_InteractableRiver_C"
                ? [.. (f.Points ?? []).Select(P), .. (f.Points2 ?? []).Reverse().Select(P)]
                : (f.Points ?? []).Select(P).ToList();
            if (outline.Count >= 3)
            {
                water.Add(new WorldWater(f.Class == "BP_InteractableRiver_C", outline));
            }
        }

        return new WorldFeatures(features, water);
    }

    private static WorldFeature CaveFeature(WorldFeatureKind kind, string name, WorldMapPoint at, (int Min, int Max)? worms, double? lengthUnits, string? style, IReadOnlyList<WorldMapPoint>? entrances) =>
        new(kind, name, at)
        {
            WormsMin = worms?.Min,
            WormsMax = worms?.Max,
            LengthMetres = lengthUnits is { } units ? Math.Round(units / 100) : null,
            Style = style,
            Entrances = entrances,
        };

    /// <summary>BP_CaveEntrance_CF_SML_01_C → "CF".</summary>
    [GeneratedRegex(@"^BP_CaveEntrance_([A-Z]{2})_")]
    private static partial Regex EntranceStyle();

    /// <summary>Cave worms placed by a cave spawner's CaveActorSpawnMap (fewest, most), all creature kinds added up.</summary>
    private static (int Min, int Max)? WormCount(JArray spawns)
    {
        int min = 0, max = 0, kinds = 0;
        foreach (var entry in spawns.OfType<JObject>())
        {
            if (entry["Value"] is JObject value && value["MinSpawnNumber"] is { } lo && value["MaxSpawnNumber"] is { } hi)
            {
                min += lo.Value<int>();
                max += hi.Value<int>();
                kinds++;
            }
        }

        return kinds > 0 ? (min, max) : null;
    }

    /// <summary>
    /// How long a cave is across its inside boxes (each a BoxComponent of half-size 32 units times
    /// its scale, placed relative to the cave): the widest span of the boxes' corners on the ground.
    /// </summary>
    private static double? Length(JArray? volumes, Func<JToken, JToken?> transformOf)
    {
        var points = new List<(double X, double Y)>();
        foreach (var volume in volumes ?? [])
        {
            if (transformOf(volume) is not JObject t || Vector(t["Translation"]) is not { } at || Vector(t["Scale3D"]) is not { } scale)
            {
                continue;
            }

            var half = Math.Max(Math.Abs(scale.X), Math.Abs(scale.Y)) * 32;
            points.Add((at.X - half, at.Y - half));
            points.Add((at.X + half, at.Y + half));
        }

        if (points.Count == 0)
        {
            return null;
        }

        var dx = points.Max(p => p.X) - points.Min(p => p.X);
        var dy = points.Max(p => p.Y) - points.Min(p => p.Y);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>"Objective.BaseLocation" → "Objective · Base Location"; quest markers without a tag go under "Other".</summary>
    internal static string MissionCategory(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return "Other";
        }

        var parts = tag.Split('.');
        return string.Join(" · ", parts.Take(2).Select(SplitWords));
    }

    /// <summary>A mission spot's name: the rest of its tag ("Horde · Spawn Location"), else its category.</summary>
    private static string MissionName(string? tag, string exportName)
    {
        var parts = tag?.Split('.') ?? [];
        return parts.Length > 2 ? string.Join(" · ", parts.Skip(2).Select(SplitWords)) : parts.Length > 0 ? SplitWords(parts[^1]) : SplitWords(exportName.Replace("BP_", "").Replace('_', ' '));
    }

    internal static string SplitWords(string text) => Words().Replace(text, " ").Trim();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex Words();

    // ---- Reading CUE4Parse objects ----

    private static FVector? ActorLocation(UObject actor)
    {
        try
        {
            if (actor.TryGetValue(out FPackageIndex root, "RootComponent") && !root.IsNull && root.TryLoad(out UObject? component) && component is not null)
            {
                return WorldLocation(component, 0);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static FVector WorldLocation(UObject component, int depth)
    {
        var location = component.GetOrDefault("RelativeLocation", FVector.ZeroVector);
        if (depth < 10 && component.TryGetValue(out FPackageIndex parent, "AttachParent") && !parent.IsNull && parent.TryLoad(out UObject? up) && up is not null)
        {
            var parentLocation = WorldLocation(up, depth + 1);
            var rotation = up.GetOrDefault("RelativeRotation", FRotator.ZeroRotator).Quaternion();
            var scale = up.GetOrDefault("RelativeScale3D", FVector.OneVector);
            return rotation.RotateVector(new FVector(location.X * scale.X, location.Y * scale.Y, location.Z * scale.Z)) + parentLocation;
        }

        return location;
    }

    private static IEnumerable<string> Tags(UObject export)
    {
        try
        {
            if (export.GetOrDefault<FGameplayTagContainer>("GameplayTags") is { } container)
            {
                return container.GameplayTags.Select(t => t.TagName.Text);
            }
        }
        catch (Exception)
        {
        }

        return Json(export, "GameplayTags") is JArray tags ? tags.Select(t => t.ToString()) : [];
    }

    /// <summary>A property as JSON (CUE4Parse's own converters), or null when it isn't there or won't convert.</summary>
    private static JToken? Json(UObject export, string property)
    {
        var tag = export.Properties.FirstOrDefault(p => p.Name.Text == property)?.Tag?.GenericValue;
        if (tag is null)
        {
            return null;
        }

        try
        {
            return JToken.FromObject(tag);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FVector? Vector(JToken? token) =>
        token is JObject o && o["X"] is { } x && o["Y"] is { } y
            ? new FVector(x.Value<float>(), y.Value<float>(), o["Z"]?.Value<float>() ?? 0)
            : null;

    /// <summary>
    /// A list of points in any of the shapes the game uses: plain vectors, transforms (a lake's
    /// EdgeSplinePoints: the point is its Translation) or a river's structs (the point is the field
    /// named "Location_&lt;id&gt;").
    /// </summary>
    private static List<FVector>? Vectors(JToken? token) =>
        token is JArray array ? [.. array.Select(PointOf).OfType<FVector>()] : null;

    private static FVector? PointOf(JToken item) =>
        item is not JObject o
            ? null
            : o["Translation"] is { } translation
                ? Vector(translation)
                : o.Properties().FirstOrDefault(p => p.Name.StartsWith("Location", StringComparison.Ordinal)) is { } location
                    ? Vector(location.Value)
                    : Vector(o);

    private static double Distance(FVector a, FVector b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
