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

    /// <summary>The search found it: drawn with a ring.</summary>
    public bool IsFound { get; init; }

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

    /// <summary>Each mission spot in plain words (its mission's name, or what kind of spot it is), worked out once per world.</summary>
    private Dictionary<WorldFeature, (string Category, string Spot)> _missions = [];

    private (string Category, string Spot) Mission(WorldFeature f) => _missions.TryGetValue(f, out var m) ? m : (f.Category ?? "Other quest spots", f.Name);

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

    /// <summary>What the mission box says when this world has no mission spots; null while it has some.</summary>
    public string? MissionPlaceholder => MissionCategories.Count == 0 ? "No mission spots on this map" : null;

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
        var names = map?.MissionNames ?? new Dictionary<string, string>();
        // By the object, not its value: two spots with the same tag at the same place are equal records
        // (a world had two), and a value-keyed dictionary threw on the second.
        _missions = new Dictionary<WorldFeature, (string, string)>(ReferenceEqualityComparer.Instance);
        foreach (var f in missions)
        {
            _missions[f] = WorldMissionNames.Describe(f.Category, f.Name, names);
        }
        if (missions.Count > 0)
        {
            MissionCategories.Add(new WorldFilterOption("", $"All kinds ({missions.Count})"));
            // Named missions first (A-Z), then the spots shared by many missions and the other groups.
            foreach (var group in missions.GroupBy(f => _missions[f].Category)
                .OrderBy(g => g.Key.Contains(" spots", StringComparison.Ordinal) ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                MissionCategories.Add(new WorldFilterOption(group.Key, $"{group.Key} ({group.Count()})"));
            }
        }

        MissionCategory = MissionCategories.FirstOrDefault(c => c.Key == keep) ?? MissionCategories.FirstOrDefault();
        _listingCategories = false;
        OnPropertyChanged(nameof(MissionPlaceholder));

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
        // The search is looked for again on this world (FindPlaces redraws the markers).
        FindPlaces();
    }

    partial void OnSelectedFeatureChanged(WorldFeatureDetailsViewModel? value)
    {
        OnPropertyChanged(nameof(ZoneHint));
        OnPropertyChanged(nameof(HasAnySelection));
    }

    /// <summary>HasAnySelection follows HasSelection, wherever that's announced.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(HasSelection))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasAnySelection)));
        }
    }

    /// <summary>A spot or a marker is picked (the Clear button shows then).</summary>
    public bool HasAnySelection => HasSelection || SelectedFeature is not null;

    /// <summary>Clear (or Esc): nothing picked, the side panel back to its hint.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void ClearSelection()
    {
        SelectedFeature = null;
        SelectedZones.Clear();
        _selectedColors = [];
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasAnySelection));
        OnPropertyChanged(nameof(ZoneHint));
        UpdateHighlight();
    }

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

    /// <summary>Places the search finds (whatever is ticked: they're shown either way, with a ring).</summary>
    private HashSet<WorldFeature> _found = [];

    /// <summary>
    /// What a place is called, for the search: its name, kind, mission name, cave tier ("tier 3"),
    /// style ("desert cave") and grid square.
    /// </summary>
    private string PlaceWords(WorldFeature f)
    {
        var words = new List<string> { f.Name, f.Category ?? "", f.Kind switch
        {
            WorldFeatureKind.OilGeyser or WorldFeatureKind.EnzymeGeyser => "geyser",
            WorldFeatureKind.HuntDen => "den great hunt",
            WorldFeatureKind.BossSpawn => "world boss",
            WorldFeatureKind.FishingSpot => "fishing",
            WorldFeatureKind.ExoticSpot => "exotic",
            WorldFeatureKind.DeepOreSpot or WorldFeatureKind.DeepOreDeposit => "deep ore",
            _ => "",
        } };
        if (f.Kind is WorldFeatureKind.Cave or WorldFeatureKind.BigCave)
        {
            words.Add(CaveLine(f));
            words.Add($"{StyleName(f.Style)} cave");
        }

        if (f.Kind == WorldFeatureKind.MissionSpot)
        {
            words.Add(MissionLine(f));
            words.Add("mission");
        }

        return string.Join(" ", words);
    }

    /// <summary>The search typed: places matching it (two letters or more), or a grid square ("I7") to go to.</summary>
    private void FindPlaces()
    {
        var query = Query();
        _found = query.Length < 2 || GridSquareAt(query) is not null
            ? []
            : [.. _features.Features.Where(f => PlaceWords(f).Contains(query, StringComparison.OrdinalIgnoreCase))];
        RebuildMarkers();
        if (GridSquareAt(query) is { } square)
        {
            ZoomRequested?.Invoke(square, 1.5 / Math.Max(1, SelectedMap?.Map.GridSize.Columns ?? 1));
        }
    }

    private string Query() => SearchText.Trim();

    /// <summary>The middle of a grid square typed as "I7" (letter across, number down); null when it isn't one on this map.</summary>
    private WorldMapPoint? GridSquareAt(string text)
    {
        var (columns, rows) = SelectedMap?.Map.GridSize ?? (0, 0);
        if (text.Length is < 2 or > 3 || !char.IsLetter(text[0]) || !int.TryParse(text[1..], out var row))
        {
            return null;
        }

        var column = char.ToUpperInvariant(text[0]) - 'A';
        return column >= 0 && column < columns && row >= 1 && row <= rows
            ? new WorldMapPoint((column + 0.5) / columns, (row - 0.5) / rows)
            : null;
    }

    /// <summary>The map should show this point, this much of the map across (0-1). The view does the zooming.</summary>
    public event Action<WorldMapPoint, double>? ZoomRequested;

    /// <summary>Enter in the search: zoom to what it found (a grid square, or all the places and zones found).</summary>
    public void ZoomToSearch()
    {
        var points = _found.Select(f => f.At).ToList();
        if (GridSquareAt(Query()) is { } square)
        {
            points.Add(square);
        }

        if (points.Count == 0)
        {
            return;
        }

        var (minU, maxU, minV, maxV) = (points.Min(p => p.U), points.Max(p => p.U), points.Min(p => p.V), points.Max(p => p.V));
        ZoomRequested?.Invoke(new WorldMapPoint((minU + maxU) / 2, (minV + maxV) / 2), Math.Max(0.08, Math.Max(maxU - minU, maxV - minV) * 1.3));
    }

    /// <summary>The clicked place, for Zoom to it and Copy.</summary>
    private WorldFeature? _picked;

    /// <summary>Zoom to it: the clicked place in the middle, close up.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ZoomToPicked()
    {
        if (_picked is { } f)
        {
            ZoomRequested?.Invoke(f.At, 0.08);
        }
    }

    /// <summary>Copy: "Olympus D10: Tier 3 cave · 6-10 cave worms", to paste to friends.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void CopyPicked()
    {
        if (_picked is not { } f || SelectedFeature is not { } details)
        {
            return;
        }

        var text = $"{SelectedMap?.Name} {GridSquare(f.At)}: {details.Title}";
        try
        {
            Clipboard.SetText(text);
            StatusMessage = $"Copied: {text}";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            StatusMessage = "Couldn't copy: another program is using the clipboard. Try again.";
        }
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
            // A place the search found shows whatever its box and filters say.
            var found = _found.Contains(f);
            var shown = found || f.Kind switch
            {
                WorldFeatureKind.Cave or WorldFeatureKind.BigCave => ShowCaves && (f.Tier ?? 1) >= minTier,
                WorldFeatureKind.OilGeyser or WorldFeatureKind.EnzymeGeyser => ShowGeysers,
                WorldFeatureKind.HuntDen or WorldFeatureKind.BossSpawn => ShowDensAndBosses,
                WorldFeatureKind.DeepOreSpot or WorldFeatureKind.DeepOreDeposit => ShowDeepOre,
                WorldFeatureKind.ExoticSpot => ShowExotic,
                WorldFeatureKind.FishingSpot => ShowFishingSpots,
                WorldFeatureKind.MissionSpot => ShowMissions && (string.IsNullOrEmpty(MissionCategory?.Key) || Mission(f).Category == MissionCategory.Key),
                _ => false,
            };
            if (!shown)
            {
                continue;
            }

            var (glyph, fill, size, label, tip, glyphBrush) = f.Kind switch
            {
                WorldFeatureKind.Cave => (f.Tier?.ToString() ?? "C", TierBrushes[(f.Tier ?? 1) - 1], 14d, (string?)null, CaveLine(f), (Brush?)null),
                WorldFeatureKind.BigCave => ("B", BigCaveBrush, 18d, f.Entrances is { Count: > 1 } e ? $"Big cave · {e.Count} entrances" : "Big cave", CaveLine(f), null),
                WorldFeatureKind.OilGeyser => ("O", OilBrush, 13d, null, "Oil geyser", null),
                WorldFeatureKind.EnzymeGeyser => ("E", EnzymeBrush, 13d, null, $"Enzyme geyser{(f.Category is { } c ? $" ({c})" : "")}", null),
                WorldFeatureKind.HuntDen => ("D", DenBrush, 18d, f.Name, f.Name, null),
                WorldFeatureKind.BossSpawn => ("W", BossBrush, 13d, null, f.Name, null),
                WorldFeatureKind.DeepOreSpot => ("", OreBrush, 8d, null, f.Name, null),
                WorldFeatureKind.DeepOreDeposit => ("D", OreBrush, 14d, null, f.Name, null),
                WorldFeatureKind.ExoticSpot => ("X", ExoticBrush, 11d, null, f.Category is { } k ? $"{f.Name} ({k})" : f.Name, null),
                WorldFeatureKind.FishingSpot => ("F", FishBrush, 12d, null, f.Name, null),
                _ => ("M", MissionBrush, 12d, null, MissionLine(f), DarkGlyph),
            };

            Markers.Add(new WorldFeatureMarkerViewModel(f, glyph, fill, found ? size + 4 : size, label, tip, glyphBrush) { IsFound = found });
        }

        SpreadAllLabels();
    }

    /// <summary>The key to the markers on the map now (only what's shown).</summary>
    public ObservableCollection<WorldLegendItem> Legend { get; } = [];

    private static readonly Brush DropSiteBrush = Frozen("#FF8A3D");

    /// <summary>Said under Key when no marker is shown; null while some are.</summary>
    public string? LegendEmptyText => Legend.Count == 0 ? "empty" : null;

    private void BuildLegend()
    {
        Legend.Clear();
        Legend.CollectionChanged -= OnLegendChanged;
        Legend.CollectionChanged += OnLegendChanged;
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

    private void OnLegendChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(LegendEmptyText));

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

    /// <summary>"POTSHOT (Forest range) · Bear · Popup".</summary>
    private string MissionLine(WorldFeature f)
    {
        var (category, spot) = Mission(f);
        return spot.Length > 0 ? $"{category} · {spot}" : category;
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
                var (category, spot) = Mission(feature);
                title = category;
                if (spot.Length > 0)
                {
                    lines.Add($"Spot: {spot}.");
                }

                lines.Add(category.Contains(" spots", StringComparison.Ordinal)
                    ? "A spot missions and quests use (the game's tag doesn't name one mission)."
                    : "A spot this mission uses (matched from the spot's tag to the game's mission list).");
                break;
        }

        if (BiomeAt(feature.At) is { } biome && feature.Kind is not (WorldFeatureKind.Cave or WorldFeatureKind.BigCave))
        {
            lines.Add($"Biome: {biome}.");
        }

        _picked = feature;
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
