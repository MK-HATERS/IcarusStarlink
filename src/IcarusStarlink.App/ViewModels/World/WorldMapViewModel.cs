using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A world in the map picker: its name, and "In development" when the game doesn't offer it yet.</summary>
public sealed record WorldMapItemViewModel(WorldMapInfo Map)
{
    public string Name => Map.Name;

    public bool InDevelopment => Map.InDevelopment;

    /// <summary>"Open world · 80 missions", "Outpost"; for a world no prospect uses yet, "Not playable yet".</summary>
    public string KindText => Map.KindText is { Length: > 0 } kind ? kind : "Not playable yet";

    /// <summary>What a screen reader says for it.</summary>
    public override string ToString() => $"{Name}, {KindText}{(InDevelopment ? ", in development" : "")}";
}

/// <summary>A dropship drop site on the map: placed in the map's 1000 x 1000 square, with its landing spots.</summary>
public sealed class WorldDropSiteViewModel(WorldDropSite site)
{
    public string Name => site.Name;

    public string Label => $"{site.Number} · {site.Name}";

    public double X => site.Centre.U * WorldMapViewModel.MapUnits;

    public double Y => site.Centre.V * WorldMapViewModel.MapUnits;

    public IReadOnlyList<Point> Spots { get; } = [.. site.Spots.Select(s => new Point(s.U * WorldMapViewModel.MapUnits, s.V * WorldMapViewModel.MapUnits))];

    public string ToolTip => $"Drop site {site.Number}: {site.Name}. {site.Spots.Count} landing spots (zoom in to see them).";
}

/// <summary>A layer the map can show (Creatures, Fishing, Biomes), ticked on or off.</summary>
public sealed partial class WorldLayerOption(WorldLayerKind kind, bool shown) : ObservableObject
{
    public WorldLayerKind Kind { get; } = kind;

    [ObservableProperty]
    private bool _isShown = shown;

    public string Title => TitleOf(Kind);

    public string ToolTip => Kind switch
    {
        WorldLayerKind.Creatures => "Where each kind of creature spawns.",
        WorldLayerKind.Fishing => "Which fish bite in each stretch of water.",
        _ => "The map's biomes.",
    };

    public static string TitleOf(WorldLayerKind kind) => kind switch
    {
        WorldLayerKind.Creatures => "Creatures",
        WorldLayerKind.Fishing => "Fishing",
        _ => "Biomes",
    };

    public override string ToString() => Title;
}

/// <summary>One line of a zone's list: what spawns there and its share of the zone's spawns.</summary>
public sealed record WorldZoneRowViewModel(string Name, double Percent)
{
    public string PercentText => Percent >= 10 || Percent == 0 ? $"{Percent:0}%" : $"{Percent:0.#}%";
}

/// <summary>The zone picked on the map.</summary>
public sealed class WorldZoneViewModel
{
    public WorldZoneViewModel(WorldZone zone, WorldLayerKind kind, uint color)
    {
        Name = zone.Name;
        RowName = zone.Row;
        LayerTitle = kind == WorldLayerKind.Biomes ? "Biome" : WorldLayerOption.TitleOf(kind);
        Color = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
        Color.Freeze();
        Rows = Shares(zone.Entries);
        ChangedByMods = zone.ChangedByMods;
        Details = kind switch
        {
            WorldLayerKind.Fishing when zone.FishQuality is { } q => $"Fish quality {q * 100:0}%",
            WorldLayerKind.Creatures when zone.MinLevel is { } min && zone.MaxLevel is { } max => $"Creature levels {min}-{max}",
            _ => null,
        };
        ListTitle = kind switch
        {
            WorldLayerKind.Creatures => "Share of the creatures that spawn here",
            WorldLayerKind.Fishing => "Chance for each catch",
            _ => null,
        };
        GameText = zone.ChangedByMods && zone.GameEntries is { } game
            ? "Without your mods: " + (game.Count == 0 ? "nothing" : string.Join(", ", Shares(game).Select(r => $"{r.Name} {r.PercentText}")))
            : null;
    }

    public string Name { get; }

    public string RowName { get; }

    /// <summary>Which layer it's from ("Creatures", "Fishing", "Biome").</summary>
    public string LayerTitle { get; }

    public SolidColorBrush Color { get; }

    public string? Details { get; }

    public string? ListTitle { get; }

    public IReadOnlyList<WorldZoneRowViewModel> Rows { get; }

    public bool ChangedByMods { get; }

    /// <summary>The game's own list, when a mod changed the zone.</summary>
    public string? GameText { get; }

    /// <summary>Same-named entries together (two deer rows are one "Deer" line), biggest share first.</summary>
    internal static IReadOnlyList<WorldZoneRowViewModel> Shares(IReadOnlyList<WorldZoneEntry> entries)
    {
        var total = entries.Sum(e => e.Weight);
        return total <= 0
            ? []
            : [.. entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new WorldZoneRowViewModel(g.Key, g.Sum(e => e.Weight) / total * 100))
                .OrderByDescending(r => r.Percent).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>
/// The Map page: every world the game has (unfinished ones tagged "In development"), its picture,
/// and where creatures spawn, fish bite and the biomes lie, read from the game's own files. The
/// drawn maps are kept in Cache\World (WorldMapStore); Reload redraws what changed, Rebuild all
/// redraws everything.
/// </summary>
public sealed partial class WorldMapViewModel : ObservableObject
{
    /// <summary>How see-through the zones are over the map (0-255).</summary>
    private const byte ZoneAlpha = 0x60;

    /// <summary>The map's size in the view (a square this many units across); markers are placed in it.</summary>
    public const double MapUnits = 1000;

    /// <summary>The picked world's dropship drop sites.</summary>
    public ObservableCollection<WorldDropSiteViewModel> DropSites { get; } = [];

    /// <summary>Whether the drop sites are drawn on the map.</summary>
    [ObservableProperty]
    private bool _showDropSites = true;

    /// <summary>"29 drop sites"; null when the world has none.</summary>
    public string? DropSitesText => DropSites.Count switch
    {
        0 => null,
        1 => "1 drop site",
        var n => $"{n} drop sites",
    };

    private readonly WorldMapStore _store;
    private readonly ISettingsService _settings;
    private readonly string _dataFolder;
    /// <summary>The picked world's zone images and zones, by layer (those the game has).</summary>
    private readonly Dictionary<WorldLayerKind, (WorldLayerPixels Pixels, IReadOnlyDictionary<uint, WorldZone> Zones)> _layers = [];

    /// <summary>The zone colour under the mouse / at the clicked spot, per shown layer.</summary>
    private Dictionary<WorldLayerKind, uint> _hoverColors = [];
    private Dictionary<WorldLayerKind, uint> _selectedColors = [];
    private bool _loadedOnce;

    public WorldMapViewModel(WorldMapStore store, ISettingsService settings, string dataFolder)
    {
        _store = store;
        _settings = settings;
        _dataFolder = dataFolder;
    }

    public string Title => "Map";

    public ObservableCollection<WorldMapItemViewModel> Maps { get; } = [];

    public ObservableCollection<WorldLayerOption> Layers { get; } = [];

    [ObservableProperty]
    private WorldMapItemViewModel? _selectedMap;

    [ObservableProperty]
    private ImageSource? _picture;

    [ObservableProperty]
    private ImageSource? _zonesImage;

    [ObservableProperty]
    private ImageSource? _highlightImage;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string? _searchResult;

    /// <summary>What's at the clicked spot: one entry per shown layer that has a zone there.</summary>
    public ObservableCollection<WorldZoneViewModel> SelectedZones { get; } = [];

    /// <summary>Something was clicked and found.</summary>
    public bool HasSelection => SelectedZones.Count > 0;

    /// <summary>The Drop sites box's tooltip.</summary>
    public string DropSitesToolTip => $"Where dropships can land: {DropSitesText}, from the game's own list. Zoom in to see each site's landing spots.";

    [ObservableProperty]
    private string? _hoverText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand), nameof(RebuildAllCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string? _outOfDateMessage;

    /// <summary>What the picture can't show ("In development…", "No map picture…"); null when nothing needs saying.</summary>
    [ObservableProperty]
    private string? _mapNote;

    /// <summary>What the side panel says before anything is clicked; null when the map has no zones (its note says so).</summary>
    public string? ZoneHint => !HasLayers || HasSelection || SelectedFeature is not null
        ? null
        : Layers.Any(l => l.IsShown)
            ? "Click the map to see what's there: the creatures that spawn, the fish that bite and the biome, for each ticked layer."
            : "Tick Creatures, Fishing or Biomes above to see the zones.";

    /// <summary>The map has zones to show (the layer boxes and search are shown only then).</summary>
    public bool HasLayers => Layers.Count > 0;

    /// <summary>The page was opened: the first time the maps are listed (and made if they never were); every time, the game's files are checked for changes.</summary>
    public void OnPageShown()
    {
        if (IsBusy)
        {
            return;
        }

        _ = _loadedOnce ? CheckAsync() : LoadAsync();
    }

    private bool CanRun() => !IsBusy;

    /// <summary>Redraws the maps whose game files changed and says what changed.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ReloadAsync() => RunReloadAsync(everything: false);

    /// <summary>Deletes every drawn map and draws them all again, changed or not.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RebuildAllAsync() => RunReloadAsync(everything: true);

    /// <summary>
    /// Update everything's step: Reload, but only when the maps were made before (making them all
    /// the first time takes about half a minute; that waits until the Map page is opened). Says
    /// whether anything was redrawn, and what changed.
    /// </summary>
    public async Task<(bool Redrawn, string Message)> ReloadForGameUpdateAsync()
    {
        if (Problem() is { } problem)
        {
            return (false, problem);
        }

        if (!_settings.Current.RedrawMapsDuringGameUpdate)
        {
            return (false, "Left for you: the Map page says which maps changed, and Reload redraws them (or tick Redraw during Update everything on the Map page).");
        }

        if (_store.PictureSources().Count == 0)
        {
            return (false, "The maps haven't been made yet: they're made the first time you open Map.");
        }

        if (IsBusy)
        {
            return (false, "The Map page is already busy drawing.");
        }

        await RunReloadAsync(everything: false);
        return (true, StatusMessage);
    }

    private async Task LoadAsync()
    {
        if (Problem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Reading the game's maps…";
            var neverMade = _store.PictureSources().Count == 0;
            if (neverMade)
            {
                IsBusy = false;
                await RunReloadAsync(everything: false);
                return;
            }

            var maps = await Task.Run(_store.ReadMaps);
            ShowMaps(maps);
            _loadedOnce = true;
            StatusMessage = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Couldn't read the maps: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        await CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (Problem() is not null)
        {
            return;
        }

        try
        {
            var check = await Task.Run(_store.Check);
            OutOfDateMessage = check.OutOfDate ? check.Summary : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OutOfDateMessage = null;
        }
    }

    private async Task RunReloadAsync(bool everything)
    {
        if (Problem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = everything ? "Drawing every map again…" : "Checking the game's map files…";
            var progress = new Progress<string>(text => StatusMessage = text);
            var result = await Task.Run(() => _store.Reload(everything, progress));
            ShowMaps(result.Maps);
            _loadedOnce = true;
            OutOfDateMessage = null;
            StatusMessage = result.Summary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Couldn't make the maps: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Why the maps can't be read at all, or null.</summary>
    private string? Problem()
    {
        if (string.IsNullOrWhiteSpace(_settings.Current.IcarusContentPath))
        {
            return "Set the game folder in Settings first: the maps are read from the game's files.";
        }

        return Directory.Exists(Path.Combine(_dataFolder, "World"))
            ? null
            : "Refresh game data in Settings first: the maps' zones are read from it.";
    }

    private void ShowMaps(IReadOnlyList<WorldMapInfo> maps)
    {
        var keep = SelectedMap?.Map.Id;
        Maps.Clear();
        // Open world maps, then mission-only maps, then outposts, then those no prospect uses yet; in development last within each.
        foreach (var map in maps.OrderBy(m => m.KindOrder).ThenBy(m => m.InDevelopment))
        {
            Maps.Add(new WorldMapItemViewModel(map));
        }

        SelectedMap = Maps.FirstOrDefault(m => m.Map.Id == keep) ?? Maps.FirstOrDefault();
        // The same world picked again: ShowMap didn't run, but its files may have been redrawn.
        if (SelectedMap?.Map.Id == keep)
        {
            ShowMap(SelectedMap);
        }
    }

    partial void OnSelectedMapChanged(WorldMapItemViewModel? value) => ShowMap(value);

    private void ShowMap(WorldMapItemViewModel? item)
    {
        // The boxes ticked for the last world stay ticked for the next. First time: creatures only (the
        // game's fishing zones cover the land too, so together they hide the creature zones).
        var shown = Layers.Count > 0
            ? Layers.Where(l => l.IsShown).Select(l => l.Kind).ToHashSet()
            : [WorldLayerKind.Creatures];
        foreach (var layer in Layers)
        {
            layer.PropertyChanged -= OnLayerTicked;
        }

        Layers.Clear();
        _layers.Clear();
        Picture = null;
        DropSites.Clear();
        foreach (var site in item?.Map.DropSites ?? [])
        {
            DropSites.Add(new WorldDropSiteViewModel(site));
        }

        OnPropertyChanged(nameof(DropSitesText));
        SelectedFeature = null;
        if (item is null)
        {
            MapNote = null;
            ShowLayers();
            ShowFeatures(null);
            return;
        }

        var map = item.Map;
        Picture = LoadImage(_store.PicturePath(map.Id));
        foreach (var kind in new[] { WorldLayerKind.Creatures, WorldLayerKind.Fishing, WorldLayerKind.Biomes })
        {
            if (map.Layer(kind) is { } layer && _store.ReadLayer(map.Id, kind) is { } pixels)
            {
                _layers[kind] = (pixels, layer.ZonesByColor);
                var option = new WorldLayerOption(kind, shown.Contains(kind));
                option.PropertyChanged += OnLayerTicked;
                Layers.Add(option);
            }
        }

        var source = _store.PictureSources().GetValueOrDefault(map.Id, WorldPictureSource.None);
        var notes = new List<string>();
        if (map.InDevelopment)
        {
            notes.Add("In development: the game has this world in its files but doesn't offer it yet, so the map may be unfinished or change.");
        }

        if (source == WorldPictureSource.Relief)
        {
            notes.Add("The game has no map picture for it yet: this one is drawn from its height data.");
        }
        else if (Picture is null)
        {
            notes.Add("The game has no map picture for this world yet.");
        }

        if (Layers.Count == 0)
        {
            notes.Add("No creature, fishing or biome zones for it in the game files yet.");
        }

        MapNote = notes.Count > 0 ? string.Join(" ", notes) : null;
        OnPropertyChanged(nameof(HasLayers));
        ShowLayers();
        ShowFeatures(map);
    }

    private void OnLayerTicked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorldLayerOption.IsShown))
        {
            ShowLayers();
        }
    }

    /// <summary>Draws the ticked layers again and forgets the click and hover (they were for other layers).</summary>
    private void ShowLayers()
    {
        SelectedZones.Clear();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ZoneHint));
        _selectedColors = [];
        _hoverColors = [];
        HoverText = null;
        ZonesImage = DrawZones();
        UpdateHighlight();
    }

    partial void OnSearchTextChanged(string value) => UpdateHighlight();

    /// <summary>The ticked layers, bottom first: biomes under creatures under fishing (fish zones are only on water).</summary>
    private IEnumerable<(WorldLayerKind Kind, WorldLayerPixels Pixels, IReadOnlyDictionary<uint, WorldZone> Zones)> ShownLayers() =>
        Layers.Where(l => l.IsShown)
            .OrderBy(l => l.Kind switch { WorldLayerKind.Biomes => 0, WorldLayerKind.Creatures => 1, _ => 2 })
            .Where(l => _layers.ContainsKey(l.Kind))
            .Select(l => (l.Kind, _layers[l.Kind].Pixels, _layers[l.Kind].Zones));

    /// <summary>A layer's pixel colour at a point of the map (0-1 from its left and top).</summary>
    private static uint ColorAt(WorldLayerPixels pixels, double x, double y) =>
        pixels.Pixels[Math.Min(pixels.Size - 1, (int)(y * pixels.Size)) * pixels.Size + Math.Min(pixels.Size - 1, (int)(x * pixels.Size))];

    /// <summary>The zone in each ticked layer at a point of the map (0-1), top layer first.</summary>
    private List<(WorldLayerKind Kind, uint Color, WorldZone Zone)> ZonesAt(double x, double y)
    {
        var hits = new List<(WorldLayerKind, uint, WorldZone)>();
        if (x is < 0 or >= 1 || y is < 0 or >= 1)
        {
            return hits;
        }

        foreach (var (kind, pixels, zones) in ShownLayers().Reverse())
        {
            var color = ColorAt(pixels, x, y);
            if (zones.TryGetValue(color, out var zone))
            {
                hits.Add((kind, color, zone));
            }
        }

        return hits;
    }

    /// <summary>The mouse is over this point of the map (0-1); null when it left the map.</summary>
    public void HoverAt(double? x, double? y)
    {
        var hits = x is { } hx && y is { } hy ? ZonesAt(hx, hy) : [];
        HoverText = hits.Count == 0 ? null : string.Join("  ·  ", hits.Select(h => HoverLine(h.Kind, h.Zone)));
        var colors = hits.ToDictionary(h => h.Kind, h => h.Color);
        if (!colors.OrderBy(kv => kv.Key).SequenceEqual(_hoverColors.OrderBy(kv => kv.Key)))
        {
            _hoverColors = colors;
            UpdateHighlight();
        }
    }

    /// <summary>A click on the map (0-1): what's there in each ticked layer, or nothing off the zones.</summary>
    public void SelectAt(double x, double y)
    {
        var hits = ZonesAt(x, y);
        SelectedFeature = null;
        _selectedColors = hits.ToDictionary(h => h.Kind, h => h.Color);
        SelectedZones.Clear();
        foreach (var (kind, color, zone) in hits)
        {
            SelectedZones.Add(new WorldZoneViewModel(zone, kind, color));
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ZoneHint));
        UpdateHighlight();
    }

    private static string HoverLine(WorldLayerKind kind, WorldZone zone)
    {
        if (kind == WorldLayerKind.Biomes)
        {
            return zone.Name;
        }

        var names = WorldZoneViewModel.Shares(zone.Entries).Select(r => r.Name).Take(3).ToList();
        return names.Count == 0 ? zone.Name : $"{zone.Name}: {string.Join(", ", names)}{(names.Count < zone.Entries.Select(e => e.Name).Distinct().Count() ? "…" : "")}";
    }

    /// <summary>Lights up the clicked zones, those under the mouse, and every zone the search finds, in the ticked layers.</summary>
    private void UpdateHighlight()
    {
        var query = SearchText.Trim();
        var layers = ShownLayers().ToList();
        var found = layers.ToDictionary(l => l.Kind, l => query.Length == 0
            ? []
            : l.Zones.Where(kv => kv.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || kv.Value.Entries.Any(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv.Key).ToHashSet());
        var foundCount = found.Values.Sum(f => f.Count);
        SearchResult = query.Length == 0
            ? null
            : foundCount == 0 ? $"No ticked layer has \"{query}\"." : foundCount == 1 ? "Found in 1 zone." : $"Found in {foundCount} zones.";

        if (layers.Count == 0 || (foundCount == 0 && _selectedColors.Count == 0 && _hoverColors.Count == 0))
        {
            HighlightImage = null;
            return;
        }

        var size = layers.Max(l => l.Pixels.Size);
        var pixels = new uint[size * size];
        foreach (var (kind, layer, _) in layers)
        {
            var selected = _selectedColors.TryGetValue(kind, out var s) ? s : (uint?)null;
            var hover = _hoverColors.TryGetValue(kind, out var h) ? h : (uint?)null;
            var hits = found[kind];
            if (selected is null && hover is null && hits.Count == 0)
            {
                continue;
            }

            for (var y = 0; y < size; y++)
            {
                var row = y * layer.Size / size * layer.Size;
                for (var x = 0; x < size; x++)
                {
                    var color = layer.Pixels[row + x * layer.Size / size];
                    // Premultiplied BGRA: the accent orange, stronger for the clicked zone.
                    if (color == selected)
                    {
                        pixels[y * size + x] = 0xB0B05F2A;
                    }
                    else if (color == hover && pixels[y * size + x] == 0)
                    {
                        pixels[y * size + x] = 0x70703C1B;
                    }
                    else if (hits.Contains(color) && pixels[y * size + x] == 0)
                    {
                        pixels[y * size + x] = 0x90907040;
                    }
                }
            }
        }

        HighlightImage = ToBitmap(pixels, size);
    }

    /// <summary>The ticked layers' zones in the game's own colours, see-through, one over another; anything not in a zone stays clear.</summary>
    private ImageSource? DrawZones()
    {
        var layers = ShownLayers().ToList();
        if (layers.Count == 0)
        {
            return null;
        }

        var size = layers.Max(l => l.Pixels.Size);
        var pixels = new uint[size * size];
        foreach (var (kind, layer, zones) in layers)
        {
            // Biomes cover everything: fainter, so the zones over them still read.
            var alpha = kind == WorldLayerKind.Biomes ? (uint)0x40 : ZoneAlpha;
            for (var y = 0; y < size; y++)
            {
                var row = y * layer.Size / size * layer.Size;
                for (var x = 0; x < size; x++)
                {
                    var color = layer.Pixels[row + x * layer.Size / size];
                    if (!zones.ContainsKey(color))
                    {
                        continue;
                    }

                    // Premultiplied "over": this layer's colour on top of what's below.
                    var below = pixels[y * size + x];
                    var keep = 255 - alpha;
                    uint Channel(int shift) => ((color >> shift & 0xFF) * alpha + (below >> shift & 0xFF) * keep) / 255;
                    var a = alpha + (below >> 24) * keep / 255;
                    pixels[y * size + x] = a << 24 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
                }
            }
        }

        return ToBitmap(pixels, size);
    }

    private static BitmapSource ToBitmap(uint[] pixels, int size)
    {
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The picture, read into memory (so Reload can delete and redraw the file), or null.</summary>
    private static BitmapImage? LoadImage(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
