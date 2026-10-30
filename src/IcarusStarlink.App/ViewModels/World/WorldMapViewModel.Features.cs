using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A marker on the map for one placed feature: where (in the 1000 x 1000 square), how it looks, what it says.</summary>
public sealed class WorldFeatureMarkerViewModel(WorldFeature feature, string glyph, Brush fill, double size, string? label, string toolTip)
{
    public WorldFeature Feature { get; } = feature;

    public double X => Feature.At.U * WorldMapViewModel.MapUnits;

    public double Y => Feature.At.V * WorldMapViewModel.MapUnits;

    public string Glyph { get; } = glyph;

    public Brush Fill { get; } = fill;

    public double Size { get; } = size;

    /// <summary>Half the size, negative: centres the marker on its point.</summary>
    public double Offset => -Size / 2;

    /// <summary>A name beside the marker (dens, big caves); null for none.</summary>
    public string? Label { get; } = label;

    public string ToolTip { get; } = toolTip;
}

/// <summary>A grid label: a column letter along the top or a row number down the left.</summary>
public sealed record WorldGridLabel(string Text, double X, double Y);

/// <summary>A choice in a filter list ("Tier 2 and up", "Objective · Base Location (103)").</summary>
public sealed record WorldFilterOption(string Key, string Title)
{
    public override string ToString() => Title;
}

/// <summary>What the side panel shows for a clicked feature: a title and its facts, one per line.</summary>
public sealed record WorldFeatureDetailsViewModel(string Title, string? Subtitle, IReadOnlyList<string> Lines);

public sealed partial class WorldMapViewModel
{
    private static readonly Brush[] TierBrushes = [Frozen("#9AA4B2"), Frozen("#F5B83D"), Frozen("#FF5C72")];
    private static readonly Brush BigCaveBrush = Frozen("#FF8A3D");
    private static readonly Brush OilBrush = Frozen("#2A2A2A");
    private static readonly Brush EnzymeBrush = Frozen("#52D68A");
    private static readonly Brush DenBrush = Frozen("#B98CFF");
    private static readonly Brush BossBrush = Frozen("#E0453A");
    private static readonly Brush OreBrush = Frozen("#C08A4A");
    private static readonly Brush ExoticBrush = Frozen("#E35DD8");
    private static readonly Brush FishBrush = Frozen("#5FA8FF");
    private static readonly Brush MissionBrush = Frozen("#F2E27A");

    private static Brush Frozen(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private WorldFeatures _features = WorldFeatures.None;

    /// <summary>The ticked features' markers.</summary>
    public ObservableCollection<WorldFeatureMarkerViewModel> Markers { get; } = [];

    [ObservableProperty]
    private bool _showCaves = true;

    [ObservableProperty]
    private bool _showGeysers;

    [ObservableProperty]
    private bool _showDensAndBosses;

    [ObservableProperty]
    private bool _showDeepOre;

    [ObservableProperty]
    private bool _showExotic;

    [ObservableProperty]
    private bool _showWater;

    [ObservableProperty]
    private bool _showFishingSpots;

    [ObservableProperty]
    private bool _showMissions;

    [ObservableProperty]
    private bool _showGrid;

    /// <summary>Which caves: every tier, tier 2 and up, or only tier 3.</summary>
    public IReadOnlyList<WorldFilterOption> CaveTierOptions { get; } =
    [
        new("1", "All caves"),
        new("2", "Tier 2 and 3"),
        new("3", "Tier 3 only"),
    ];

    [ObservableProperty]
    private WorldFilterOption? _caveTier;

    /// <summary>Mission spot categories on this world, with counts; the first is all of them.</summary>
    public ObservableCollection<WorldFilterOption> MissionCategories { get; } = [];

    [ObservableProperty]
    private WorldFilterOption? _missionCategory;

    /// <summary>Lakes and rivers as one shape in the map's square; null when hidden or none.</summary>
    [ObservableProperty]
    private Geometry? _waterGeometry;

    /// <summary>The grid's lines in the map's square; null when hidden.</summary>
    [ObservableProperty]
    private Geometry? _gridGeometry;

    public ObservableCollection<WorldGridLabel> GridLabels { get; } = [];

    /// <summary>The feature clicked on the map; null when a spot (zones) was clicked or nothing.</summary>
    [ObservableProperty]
    private WorldFeatureDetailsViewModel? _selectedFeature;

    /// <summary>The ticked layers' and features' counts, for the boxes' tooltips: "143 caves (10 tier 3)".</summary>
    public string CavesText => Count(WorldFeatureKind.Cave, WorldFeatureKind.BigCave) is var n and > 0
        ? $"{n} caves: tier by cave worms (1: up to 3, 2: 4-6, 3: 7 or more; more worms usually means a better cave). Hand-made big caves are orange."
        : "No caves read for this world.";

    public string GeysersText => $"{Count(WorldFeatureKind.OilGeyser)} oil geysers (black) and {Count(WorldFeatureKind.EnzymeGeyser)} enzyme geysers (green).";

    public string DensText => $"Great Hunt dens ({Count(WorldFeatureKind.HuntDen)}) and spots world bosses can spawn ({Count(WorldFeatureKind.BossSpawn)}).";

    public string DeepOreText => Count(WorldFeatureKind.DeepOreDeposit) > 0
        ? $"{Count(WorldFeatureKind.DeepOreSpot)} spots a deep ore deposit can appear, and {Count(WorldFeatureKind.DeepOreDeposit)} deposits that are always there. Which spots get one, and which ore, is picked when the world is made."
        : $"{Count(WorldFeatureKind.DeepOreSpot)} spots a deep ore deposit can appear. Which spots get one, and which ore, is picked when the world is made.";

    public string ExoticText => $"{Count(WorldFeatureKind.ExoticSpot)} spots exotics can appear. Each prospect uses only a few of them, picked by the game.";

    public string WaterText => $"{_features.Water.Count(w => !w.IsRiver)} lakes and {_features.Water.Count(w => w.IsRiver)} rivers.";

    public string FishingSpotsText => $"{Count(WorldFeatureKind.FishingSpot)} fishing spots: where the game puts fish in the water.";

    public string MissionsText => $"{Count(WorldFeatureKind.MissionSpot)} mission and quest spots. Pick a kind beside the box.";

    /// <summary>Whether this world has anything placed to show (the feature boxes show only then).</summary>
    public bool HasFeatures => _features.Features.Count > 0 || _features.Water.Count > 0;

    private int Count(params WorldFeatureKind[] kinds) => _features.Features.Count(f => kinds.Contains(f.Kind));

    /// <summary>Settings: Update everything redraws the changed maps by itself (off: left to Reload).</summary>
    public bool RedrawDuringUpdate
    {
        get => _settings.Current.RedrawMapsDuringGameUpdate;
        set
        {
            if (_settings.Current.RedrawMapsDuringGameUpdate == value)
            {
                return;
            }

            _settings.Current.RedrawMapsDuringGameUpdate = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>The picked world's features, from its drawn copy.</summary>
    private void ShowFeatures(WorldMapInfo? map)
    {
        _features = map is null ? WorldFeatures.None : _store.ReadFeatures(map.Id);
        CaveTier ??= CaveTierOptions[0];

        var keep = MissionCategory?.Key;
        MissionCategories.Clear();
        var missions = _features.Features.Where(f => f.Kind == WorldFeatureKind.MissionSpot).ToList();
        if (missions.Count > 0)
        {
            MissionCategories.Add(new WorldFilterOption("", $"All kinds ({missions.Count})"));
            foreach (var group in missions.GroupBy(f => f.Category ?? "Other").OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            {
                MissionCategories.Add(new WorldFilterOption(group.Key, $"{group.Key} ({group.Count()})"));
            }
        }

        MissionCategory = MissionCategories.FirstOrDefault(c => c.Key == keep) ?? MissionCategories.FirstOrDefault();

        foreach (var name in new[]
        {
            nameof(CavesText), nameof(GeysersText), nameof(DensText), nameof(DeepOreText), nameof(ExoticText), nameof(WaterText),
            nameof(FishingSpotsText), nameof(MissionsText), nameof(HasFeatures),
        })
        {
            OnPropertyChanged(name);
        }

        DrawWater();
        DrawGrid(map);
        RebuildMarkers();
    }

    partial void OnSelectedFeatureChanged(WorldFeatureDetailsViewModel? value) => OnPropertyChanged(nameof(ZoneHint));

    partial void OnShowCavesChanged(bool value) => RebuildMarkers();
    partial void OnShowGeysersChanged(bool value) => RebuildMarkers();
    partial void OnShowDensAndBossesChanged(bool value) => RebuildMarkers();
    partial void OnShowDeepOreChanged(bool value) => RebuildMarkers();
    partial void OnShowExoticChanged(bool value) => RebuildMarkers();
    partial void OnShowFishingSpotsChanged(bool value) => RebuildMarkers();
    partial void OnShowMissionsChanged(bool value) => RebuildMarkers();
    partial void OnCaveTierChanged(WorldFilterOption? value) => RebuildMarkers();
    partial void OnMissionCategoryChanged(WorldFilterOption? value) => RebuildMarkers();
    partial void OnShowWaterChanged(bool value) => DrawWater();
    partial void OnShowGridChanged(bool value) => DrawGrid(SelectedMap?.Map);

    private void RebuildMarkers()
    {
        Markers.Clear();
        var minTier = int.TryParse(CaveTier?.Key, out var t) ? t : 1;
        // Many small markers first, so the few big ones (dens, big caves) are drawn on top.
        foreach (var f in _features.Features.OrderBy(f => f.Kind switch
        {
            WorldFeatureKind.MissionSpot or WorldFeatureKind.DeepOreSpot or WorldFeatureKind.ExoticSpot => 0,
            WorldFeatureKind.Cave or WorldFeatureKind.FishingSpot => 1,
            _ => 2,
        }))
        {
            var marker = f.Kind switch
            {
                WorldFeatureKind.Cave when ShowCaves && (f.Tier ?? 1) >= minTier =>
                    new WorldFeatureMarkerViewModel(f, f.Tier?.ToString() ?? "C", TierBrushes[(f.Tier ?? 1) - 1], 14, null, CaveLine(f)),
                WorldFeatureKind.BigCave when ShowCaves && (f.Tier ?? 1) >= minTier =>
                    new WorldFeatureMarkerViewModel(f, "B", BigCaveBrush, 18, f.Entrances is { Count: > 1 } e ? $"Big cave · {e.Count} entrances" : "Big cave", CaveLine(f)),
                WorldFeatureKind.OilGeyser when ShowGeysers => new WorldFeatureMarkerViewModel(f, "O", OilBrush, 13, null, "Oil geyser"),
                WorldFeatureKind.EnzymeGeyser when ShowGeysers => new WorldFeatureMarkerViewModel(f, "E", EnzymeBrush, 13, null, $"Enzyme geyser{(f.Category is { } c ? $" ({c})" : "")}"),
                WorldFeatureKind.HuntDen when ShowDensAndBosses => new WorldFeatureMarkerViewModel(f, "D", DenBrush, 18, f.Name, f.Name),
                WorldFeatureKind.BossSpawn when ShowDensAndBosses => new WorldFeatureMarkerViewModel(f, "W", BossBrush, 13, null, f.Name),
                WorldFeatureKind.DeepOreSpot when ShowDeepOre => new WorldFeatureMarkerViewModel(f, "", OreBrush, 7, null, f.Name),
                WorldFeatureKind.DeepOreDeposit when ShowDeepOre => new WorldFeatureMarkerViewModel(f, "D", OreBrush, 14, null, f.Name),
                WorldFeatureKind.ExoticSpot when ShowExotic => new WorldFeatureMarkerViewModel(f, "X", ExoticBrush, 11, null, f.Category is { } k ? $"{f.Name} ({k})" : f.Name),
                WorldFeatureKind.FishingSpot when ShowFishingSpots => new WorldFeatureMarkerViewModel(f, "F", FishBrush, 12, null, f.Name),
                WorldFeatureKind.MissionSpot when ShowMissions && (string.IsNullOrEmpty(MissionCategory?.Key) || f.Category == MissionCategory.Key) =>
                    new WorldFeatureMarkerViewModel(f, "", MissionBrush, 7, null, $"{f.Category}: {f.Name}"),
                _ => null,
            };

            if (marker is not null)
            {
                Markers.Add(marker);
            }
        }
    }

    private static string CaveLine(WorldFeature cave)
    {
        var tier = cave.Tier is { } t ? $"Tier {t} " : "";
        var worms = cave.WormsMax is { } max ? $" · {WormsText(cave.WormsMin ?? 0, max)}" : "";
        return $"{tier}{(cave.Kind == WorldFeatureKind.BigCave ? "big cave" : "cave")}{worms}";
    }

    private static string WormsText(int min, int max) => max == 0 ? "no cave worms" : min == max ? $"{max} cave worms" : $"{min}-{max} cave worms";

    /// <summary>A marker was clicked: its facts in the side panel (the zones' are cleared).</summary>
    public void SelectFeature(WorldFeature feature)
    {
        SelectedZones.Clear();
        _selectedColors = [];
        OnPropertyChanged(nameof(HasSelection));
        UpdateHighlight();

        var lines = new List<string>();
        string title = feature.Name;
        string? subtitle = GridSquare(feature.At) is { } square ? $"Grid {square}" : null;
        switch (feature.Kind)
        {
            case WorldFeatureKind.Cave or WorldFeatureKind.BigCave:
                title = CaveLine(feature);
                title = char.ToUpperInvariant(title[0]) + title[1..];
                if (feature.LengthMetres is { } length)
                {
                    lines.Add($"About {length:0} m long inside.");
                }

                if (StyleName(feature.Style) is { } style)
                {
                    lines.Add($"{style} cave (its entrance's style).");
                }

                if (feature.Entrances is { Count: > 0 } entrances)
                {
                    var biomes = entrances.Select(e => BiomeAt(e) ?? "?").ToList();
                    lines.Add(entrances.Count == 1
                        ? $"1 entrance, in {biomes[0]}."
                        : $"{entrances.Count} entrances, in: {string.Join(", ", biomes)}. Worked out from where each entrance is on the biome map; the game doesn't say which caves connect.");
                }

                if (ZoneAt(WorldLayerKind.Creatures, feature.At) is { } area)
                {
                    lines.Add($"In the {area.Name} creature zone{(area.MinLevel is { } lo && area.MaxLevel is { } hi ? $" (creature levels {lo}-{hi} above ground)" : "")}.");
                }

                if (feature.Style is { } code && SelectedMap?.Map.CaveOres.GetValueOrDefault(code) is { Count: > 0 } ores)
                {
                    var total = ores.Sum(o => o.Weight);
                    lines.Add("Ores it can have: " + string.Join(", ", ores.OrderByDescending(o => o.Weight).Select(o => $"{o.Name} {o.Weight / total * 100:0}%")) + ".");
                }

                if (feature.WormsMax is null)
                {
                    lines.Add("No cave worm spawner found next to it, so no tier.");
                }

                break;
            case WorldFeatureKind.EnzymeGeyser when feature.Category is { } horde:
                lines.Add($"Its horde: {horde}.");
                break;
            case WorldFeatureKind.DeepOreSpot:
                lines.Add("A deep ore deposit can appear here. Which spots get one, and which ore, is picked when the world is made.");
                break;
            case WorldFeatureKind.ExoticSpot:
                if (feature.Category is { } kind)
                {
                    lines.Add($"Kind: {kind}.");
                }

                lines.Add("Exotics can appear here. Each prospect uses only a few of these spots, picked by the game.");
                break;
            case WorldFeatureKind.BossSpawn:
                lines.Add("One of the spots this world boss can appear.");
                break;
            case WorldFeatureKind.MissionSpot:
                lines.Add($"{feature.Category}.");
                lines.Add("A spot missions and quests of this kind use.");
                break;
        }

        if (BiomeAt(feature.At) is { } biome && feature.Kind is not (WorldFeatureKind.Cave or WorldFeatureKind.BigCave))
        {
            lines.Add($"Biome: {biome}.");
        }

        SelectedFeature = new WorldFeatureDetailsViewModel(title, subtitle, lines);
    }

    /// <summary>"Desert", "Forest" (style codes as the game names its cave entrances); null for an unknown one.</summary>
    private static string? StyleName(string? code) => code switch
    {
        "CF" => "Forest",
        "DC" => "Desert",
        "AC" => "Arctic",
        "LC" => "Lava",
        "IC" => "Ice",
        "TU" => "Tundra",
        _ => null,
    };

    /// <summary>The zone of a layer at a point, if the world has that layer.</summary>
    private WorldZone? ZoneAt(WorldLayerKind kind, WorldMapPoint at) =>
        _layers.TryGetValue(kind, out var layer) && at.U is >= 0 and < 1 && at.V is >= 0 and < 1
            && layer.Zones.TryGetValue(ColorAt(layer.Pixels, at.U, at.V), out var zone)
            ? zone
            : null;

    private string? BiomeAt(WorldMapPoint at) => ZoneAt(WorldLayerKind.Biomes, at)?.Name;

    /// <summary>The in-game grid square at a point ("I7"): letters across from the left, numbers down from the top.</summary>
    private string? GridSquare(WorldMapPoint at)
    {
        var (columns, rows) = SelectedMap?.Map.GridSize ?? (0, 0);
        if (columns is 0 or > 26 || rows == 0 || at.U is < 0 or >= 1 || at.V is < 0 or >= 1)
        {
            return null;
        }

        return $"{(char)('A' + (int)(at.U * columns))}{(int)(at.V * rows) + 1}";
    }

    private void DrawWater()
    {
        if (!ShowWater || _features.Water.Count == 0)
        {
            WaterGeometry = null;
            return;
        }

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var draw = geometry.Open())
        {
            foreach (var water in _features.Water)
            {
                draw.BeginFigure(ToPoint(water.Outline[0]), isFilled: true, isClosed: true);
                draw.PolyLineTo([.. water.Outline.Skip(1).Select(ToPoint)], isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        WaterGeometry = geometry;
    }

    private static Point ToPoint(WorldMapPoint p) => new(p.U * MapUnits, p.V * MapUnits);

    private void DrawGrid(WorldMapInfo? map)
    {
        GridLabels.Clear();
        var (columns, rows) = map?.GridSize ?? (0, 0);
        if (!ShowGrid || columns is 0 or > 26 || rows == 0)
        {
            GridGeometry = null;
            return;
        }

        var geometry = new StreamGeometry();
        using (var draw = geometry.Open())
        {
            for (var c = 1; c < columns; c++)
            {
                draw.BeginFigure(new Point(c * MapUnits / columns, 0), false, false);
                draw.LineTo(new Point(c * MapUnits / columns, MapUnits), true, false);
            }

            for (var r = 1; r < rows; r++)
            {
                draw.BeginFigure(new Point(0, r * MapUnits / rows), false, false);
                draw.LineTo(new Point(MapUnits, r * MapUnits / rows), true, false);
            }
        }

        geometry.Freeze();
        GridGeometry = geometry;
        for (var c = 0; c < columns; c++)
        {
            GridLabels.Add(new WorldGridLabel(((char)('A' + c)).ToString(), (c + 0.5) * MapUnits / columns, 0));
        }

        for (var r = 0; r < rows; r++)
        {
            GridLabels.Add(new WorldGridLabel((r + 1).ToString(), 0, (r + 0.5) * MapUnits / rows));
        }
    }
}
