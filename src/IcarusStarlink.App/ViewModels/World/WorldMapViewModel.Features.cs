using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A marker on the map for one placed feature: where (in the 1000 x 1000 square), how it looks, what it says.</summary>
public sealed partial class WorldFeatureMarkerViewModel(WorldFeature feature, string glyph, Brush fill, double size, string? label, string toolTip, Brush? glyphBrush = null)
    : ObservableObject
{
    /// <summary>The glyph's colour: white, or dark on a light marker.</summary>
    public Brush GlyphBrush { get; } = glyphBrush ?? Brushes.White;

    /// <summary>How far (screen pixels) the label is moved down so it doesn't sit on a nearby one.</summary>
    [ObservableProperty]
    private double _labelShift;

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

/// <summary>One line of the map's key: a marker as it looks on the map, and what it means.</summary>
public sealed record WorldLegendItem(string Glyph, Brush Fill, Brush GlyphBrush, string Text);

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
    private static readonly Brush MissionBrush = Frozen("#FFD23F");
    private static readonly Brush DarkGlyph = Frozen("#17110A");

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
    private bool _showCaves;

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

        var keep = _settings.Current.MapMissionCategory ?? MissionCategory?.Key;
        _listingCategories = true;
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
        _listingCategories = false;

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

    partial void OnShowCavesChanged(bool value) => Changed(RebuildMarkers);
    partial void OnShowGeysersChanged(bool value) => Changed(RebuildMarkers);
    partial void OnShowDensAndBossesChanged(bool value) => Changed(RebuildMarkers);
    partial void OnShowDeepOreChanged(bool value) => Changed(RebuildMarkers);
    partial void OnShowExoticChanged(bool value) => Changed(RebuildMarkers);
    partial void OnShowFishingSpotsChanged(bool value) => Changed(RebuildMarkers);
    partial void OnShowMissionsChanged(bool value) => Changed(RebuildMarkers);
    partial void OnCaveTierChanged(WorldFilterOption? value) => Changed(RebuildMarkers);
    partial void OnShowWaterChanged(bool value) => Changed(DrawWater);
    partial void OnShowGridChanged(bool value) => Changed(() => DrawGrid(SelectedMap?.Map));

    partial void OnMissionCategoryChanged(WorldFilterOption? value)
    {
        RebuildMarkers();
        // Only a pick the user made is kept (a world without that kind falls back to "all" without forgetting it).
        if (!_restoring && !_listingCategories && value is not null)
        {
            _settings.Current.MapMissionCategory = value.Key;
            _settings.Save();
        }
    }

    /// <summary>The choices are being put back from settings (or the category list refilled): nothing to save.</summary>
    private bool _restoring;
    private bool _listingCategories;

    /// <summary>A box or the cave tier changed: redraw, and remember what's ticked.</summary>
    private void Changed(Action redraw)
    {
        redraw();
        if (!_restoring)
        {
            SaveChoices();
        }
    }

    private const string DropSitesKey = "DropSites", GridKey = "Grid", CavesKey = "Caves", GeysersKey = "Geysers", DensKey = "Dens",
        DeepOreKey = "DeepOre", ExoticKey = "Exotic", WaterKey = "Water", FishingSpotsKey = "FishingSpots", MissionsKey = "Missions";

    /// <summary>What was ticked last time (first time: creature zones, drop sites and caves).</summary>
    private HashSet<string> SavedChoices() =>
        _settings.Current.MapShown is { } shown
            ? new HashSet<string>(shown, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([nameof(WorldLayerKind.Creatures), DropSitesKey, CavesKey], StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts back what was ticked and picked last time.</summary>
    private void RestoreChoices()
    {
        _restoring = true;
        try
        {
            var shown = SavedChoices();
            ShowDropSites = shown.Contains(DropSitesKey);
            ShowGrid = shown.Contains(GridKey);
            ShowCaves = shown.Contains(CavesKey);
            ShowGeysers = shown.Contains(GeysersKey);
            ShowDensAndBosses = shown.Contains(DensKey);
            ShowDeepOre = shown.Contains(DeepOreKey);
            ShowExotic = shown.Contains(ExoticKey);
            ShowWater = shown.Contains(WaterKey);
            ShowFishingSpots = shown.Contains(FishingSpotsKey);
            ShowMissions = shown.Contains(MissionsKey);
            CaveTier = CaveTierOptions.FirstOrDefault(o => o.Key == _settings.Current.MapCaveTier) ?? CaveTierOptions[0];
        }
        finally
        {
            _restoring = false;
        }
    }

    /// <summary>Remembers every box (zone layers too) and the cave tier.</summary>
    private void SaveChoices()
    {
        var shown = new List<string>();
        // Zone layers: the ticked ones of this world; a layer this world doesn't have keeps its last choice.
        var saved = SavedChoices();
        foreach (var kind in new[] { WorldLayerKind.Creatures, WorldLayerKind.Fishing, WorldLayerKind.Biomes })
        {
            var layer = Layers.FirstOrDefault(l => l.Kind == kind);
            if (layer?.IsShown ?? saved.Contains(kind.ToString()))
            {
                shown.Add(kind.ToString());
            }
        }

        foreach (var (key, on) in new[]
        {
            (DropSitesKey, ShowDropSites), (GridKey, ShowGrid), (CavesKey, ShowCaves), (GeysersKey, ShowGeysers), (DensKey, ShowDensAndBosses),
            (DeepOreKey, ShowDeepOre), (ExoticKey, ShowExotic), (WaterKey, ShowWater), (FishingSpotsKey, ShowFishingSpots), (MissionsKey, ShowMissions),
        })
        {
            if (on)
            {
                shown.Add(key);
            }
        }

        _settings.Current.MapShown = shown;
        _settings.Current.MapCaveTier = CaveTier?.Key;
        _settings.Save();
    }

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
                WorldFeatureKind.DeepOreSpot when ShowDeepOre => new WorldFeatureMarkerViewModel(f, "", OreBrush, 8, null, f.Name),
                WorldFeatureKind.DeepOreDeposit when ShowDeepOre => new WorldFeatureMarkerViewModel(f, "D", OreBrush, 14, null, f.Name),
                WorldFeatureKind.ExoticSpot when ShowExotic => new WorldFeatureMarkerViewModel(f, "X", ExoticBrush, 11, null, f.Category is { } k ? $"{f.Name} ({k})" : f.Name),
                WorldFeatureKind.FishingSpot when ShowFishingSpots => new WorldFeatureMarkerViewModel(f, "F", FishBrush, 12, null, f.Name),
                WorldFeatureKind.MissionSpot when ShowMissions && (string.IsNullOrEmpty(MissionCategory?.Key) || f.Category == MissionCategory.Key) =>
                    new WorldFeatureMarkerViewModel(f, "M", MissionBrush, 12, null, $"{f.Category}: {f.Name}", DarkGlyph),
                _ => null,
            };

            if (marker is not null)
            {
                Markers.Add(marker);
            }
        }

        SpreadAllLabels();
    }

    /// <summary>The key to the markers on the map now (only what's shown).</summary>
    public ObservableCollection<WorldLegendItem> Legend { get; } = [];

    private static readonly Brush DropSiteBrush = Frozen("#FF8A3D");

    private void BuildLegend()
    {
        Legend.Clear();
        var shown = Markers.Select(m => m.Feature.Kind).ToHashSet();
        if (ShowDropSites && DropSites.Count > 0)
        {
            Legend.Add(new("▲", DropSiteBrush, DarkGlyph, "Drop site (zoom in for its landing spots)"));
        }

        if (shown.Contains(WorldFeatureKind.Cave))
        {
            Legend.Add(new("1", TierBrushes[0], Brushes.White, "Cave, tier 1: up to 3 cave worms"));
            Legend.Add(new("2", TierBrushes[1], Brushes.White, "Cave, tier 2: 4-6 cave worms"));
            Legend.Add(new("3", TierBrushes[2], Brushes.White, "Cave, tier 3: 7 or more cave worms"));
        }

        void Add(WorldFeatureKind kind, string glyph, Brush fill, string text, Brush? glyphBrush = null)
        {
            if (shown.Contains(kind))
            {
                Legend.Add(new(glyph, fill, glyphBrush ?? Brushes.White, text));
            }
        }

        Add(WorldFeatureKind.BigCave, "B", BigCaveBrush, "Big cave (hand-made, often several entrances)");
        Add(WorldFeatureKind.OilGeyser, "O", OilBrush, "Oil geyser");
        Add(WorldFeatureKind.EnzymeGeyser, "E", EnzymeBrush, "Enzyme geyser");
        Add(WorldFeatureKind.HuntDen, "D", DenBrush, "Great Hunt den");
        Add(WorldFeatureKind.BossSpawn, "W", BossBrush, "World boss spawn spot");
        Add(WorldFeatureKind.DeepOreSpot, "", OreBrush, "Possible deep ore spot");
        Add(WorldFeatureKind.DeepOreDeposit, "D", OreBrush, "Deep ore deposit (always there)");
        Add(WorldFeatureKind.ExoticSpot, "X", ExoticBrush, "Possible exotic spot");
        Add(WorldFeatureKind.FishingSpot, "F", FishBrush, "Fishing spot");
        Add(WorldFeatureKind.MissionSpot, "M", MissionBrush, "Mission spot", DarkGlyph);
    }

    /// <summary>The shown drop sites' and markers' names, spread so none sits on another.</summary>
    private void SpreadAllLabels()
    {
        BuildLegend();
        SpreadLabels(
            (ShowDropSites ? DropSites.Select(d => (d.Centre, (Action<double>)(shift => d.LabelShift = shift))) : [])
            .Concat(Markers.Where(m => m.Label is not null).Select(m => (m.Feature.At, (Action<double>)(shift => m.LabelShift = shift)))));
    }

    partial void OnShowDropSitesChanged(bool value) => Changed(SpreadAllLabels);

    /// <summary>
    /// Moves a label down a line for each label already placed close beside it (the whole map in view: a
    /// label is about a tenth of the map wide), so two names near each other don't sit on top of each other.
    /// </summary>
    internal static void SpreadLabels(IEnumerable<(WorldMapPoint At, Action<double> SetShift)> labelled)
    {
        const double LabelWidth = 0.12, LineHeight = 0.02, ShiftPixels = 16;
        var placed = new List<(WorldMapPoint At, int Line)>();
        foreach (var (at, setShift) in labelled.OrderBy(l => l.At.V).ThenBy(l => l.At.U))
        {
            var line = 0;
            while (placed.Any(p => p.Line == line && Math.Abs(p.At.U - at.U) < LabelWidth && Math.Abs(p.At.V + p.Line * LineHeight - (at.V + line * LineHeight)) < LineHeight))
            {
                line++;
            }

            placed.Add((at, line));
            setShift(line * ShiftPixels);
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
