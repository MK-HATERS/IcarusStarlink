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

/// <summary>A layer the map can show (Creatures, Fishing, Biomes).</summary>
public sealed record WorldLayerOption(WorldLayerKind Kind)
{
    public string Title => Kind switch
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

    private readonly WorldMapStore _store;
    private readonly ISettingsService _settings;
    private readonly string _dataFolder;
    private WorldLayerPixels? _layerPixels;
    private uint? _hoverColor;
    private uint? _selectedColor;
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
    private WorldLayerOption? _selectedLayer;

    [ObservableProperty]
    private ImageSource? _picture;

    [ObservableProperty]
    private ImageSource? _zonesImage;

    [ObservableProperty]
    private ImageSource? _highlightImage;

    [ObservableProperty]
    private bool _showZones = true;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string? _searchResult;

    [ObservableProperty]
    private WorldZoneViewModel? _selectedZone;

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

    /// <summary>What the side panel says before a zone is picked.</summary>
    /// <remarks>Null without a layer: the map's note already says it has no zones.</remarks>
    public string? ZoneHint => SelectedLayer?.Kind switch
    {
        WorldLayerKind.Fishing => "Click water on the map to see which fish bite there.",
        WorldLayerKind.Biomes => "Click the map to see which biome it is.",
        WorldLayerKind.Creatures => "Click a zone on the map to see which creatures spawn there.",
        _ => null,
    };

    /// <summary>The map has zones to show (the layer picker and search are shown only then).</summary>
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
        var keepLayer = SelectedLayer?.Kind;
        Layers.Clear();
        Picture = null;
        SelectedZone = null;
        if (item is null)
        {
            MapNote = null;
            SelectedLayer = null;
            return;
        }

        var map = item.Map;
        Picture = LoadImage(_store.PicturePath(map.Id));
        foreach (var kind in new[] { WorldLayerKind.Creatures, WorldLayerKind.Fishing, WorldLayerKind.Biomes })
        {
            if (map.Layer(kind) is not null && File.Exists(_store.LayerPath(map.Id, kind)))
            {
                Layers.Add(new WorldLayerOption(kind));
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
        SelectedLayer = Layers.FirstOrDefault(l => l.Kind == keepLayer) ?? Layers.FirstOrDefault();
        if (SelectedLayer is null)
        {
            ShowLayer();
        }
    }

    partial void OnSelectedLayerChanged(WorldLayerOption? value) => ShowLayer();

    private void ShowLayer()
    {
        OnPropertyChanged(nameof(ZoneHint));
        SelectedZone = null;
        _selectedColor = _hoverColor = null;
        HoverText = null;
        _layerPixels = SelectedMap is { } map && SelectedLayer is { } layer ? _store.ReadLayer(map.Map.Id, layer.Kind) : null;
        ZonesImage = _layerPixels is not null ? DrawZones(_layerPixels, CurrentZones()) : null;
        UpdateHighlight();
    }

    partial void OnSearchTextChanged(string value) => UpdateHighlight();

    /// <summary>The current layer's zones by colour; empty when there's no layer.</summary>
    private IReadOnlyDictionary<uint, WorldZone> CurrentZones() =>
        SelectedMap?.Map.Layer(SelectedLayer?.Kind ?? WorldLayerKind.Creatures) is { } layer && SelectedLayer is not null
            ? layer.ZonesByColor
            : new Dictionary<uint, WorldZone>();

    /// <summary>The zone at a point of the map (0-1 from its left and top), or null.</summary>
    private (uint Color, WorldZone Zone)? ZoneAt(double x, double y)
    {
        if (_layerPixels is not { } layer || x is < 0 or >= 1 || y is < 0 or >= 1)
        {
            return null;
        }

        var color = layer.Pixels[(int)(y * layer.Size) * layer.Size + (int)(x * layer.Size)];
        return CurrentZones().TryGetValue(color, out var zone) ? (color, zone) : null;
    }

    /// <summary>The mouse is over this point of the map (0-1); null when it left the map.</summary>
    public void HoverAt(double? x, double? y)
    {
        var hit = x is { } hx && y is { } hy ? ZoneAt(hx, hy) : null;
        HoverText = hit is { } h ? HoverLine(h.Zone) : null;
        if (hit?.Color != _hoverColor)
        {
            _hoverColor = hit?.Color;
            UpdateHighlight();
        }
    }

    /// <summary>A click on the map (0-1): picks the zone there, or clears the pick off the zones.</summary>
    public void SelectAt(double x, double y)
    {
        var hit = ZoneAt(x, y);
        _selectedColor = hit?.Color;
        SelectedZone = hit is { } h && SelectedLayer is { } layer ? new WorldZoneViewModel(h.Zone, layer.Kind, h.Color) : null;
        UpdateHighlight();
    }

    private static string HoverLine(WorldZone zone)
    {
        var names = WorldZoneViewModel.Shares(zone.Entries).Select(r => r.Name).Take(3).ToList();
        return names.Count == 0 ? zone.Name : $"{zone.Name}: {string.Join(", ", names)}{(zone.Entries.Count > 3 ? "…" : "")}";
    }

    /// <summary>Lights up the picked zone, the one under the mouse, and every zone the search finds.</summary>
    private void UpdateHighlight()
    {
        var zones = CurrentZones();
        var query = SearchText.Trim();
        var found = query.Length == 0
            ? []
            : zones.Where(kv => kv.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || kv.Value.Entries.Any(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv.Key).ToHashSet();
        SearchResult = query.Length == 0
            ? null
            : found.Count == 0 ? $"No zone here has \"{query}\"." : found.Count == 1 ? "Found in 1 zone." : $"Found in {found.Count} zones.";

        if (_layerPixels is not { } layer || (found.Count == 0 && _selectedColor is null && _hoverColor is null))
        {
            HighlightImage = null;
            return;
        }

        var size = layer.Size;
        var pixels = new uint[size * size];
        var selected = _selectedColor;
        var hover = _hoverColor;
        for (var i = 0; i < pixels.Length; i++)
        {
            var color = layer.Pixels[i];
            // Premultiplied BGRA: the accent orange, stronger for the picked zone.
            if (color == selected)
            {
                pixels[i] = 0xB0B05F2A;
            }
            else if (color == hover)
            {
                pixels[i] = 0x70703C1B;
            }
            else if (found.Contains(color))
            {
                pixels[i] = 0x90907040;
            }
        }

        HighlightImage = ToBitmap(pixels, size);
    }

    /// <summary>The zones in the game's own colours, see-through; anything not in a zone stays clear.</summary>
    private static ImageSource DrawZones(WorldLayerPixels layer, IReadOnlyDictionary<uint, WorldZone> zones)
    {
        var pixels = new uint[layer.Pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var color = layer.Pixels[i];
            if (zones.ContainsKey(color))
            {
                // Premultiplied BGRA.
                uint r = (color >> 16 & 0xFF) * ZoneAlpha / 255, g = (color >> 8 & 0xFF) * ZoneAlpha / 255, b = (color & 0xFF) * ZoneAlpha / 255;
                pixels[i] = (uint)ZoneAlpha << 24 | r << 16 | g << 8 | b;
            }
        }

        return ToBitmap(pixels, layer.Size);
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
