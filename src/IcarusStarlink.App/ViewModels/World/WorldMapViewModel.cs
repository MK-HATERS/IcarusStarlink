using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Activity;
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
public sealed partial class WorldDropSiteViewModel(WorldDropSite site) : ObservableObject
{
    public string Name => site.Name;

    public WorldMapPoint Centre => site.Centre;

    /// <summary>How far (screen pixels) the name is moved down so it doesn't sit on a nearby one.</summary>
    [ObservableProperty]
    private double _labelShift;

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
/// where creatures spawn, fish bite and the biomes lie, and what's placed on it (drop sites, caves,
/// geysers, spots, water; the Features partial), read from the game's own files. The drawn maps are
/// kept in Cache\World (WorldMapStore); Reload redraws what changed, Rebuild all redraws everything.
/// </summary>
public sealed partial class WorldMapViewModel : ObservableObject, IcarusStarlink.Core.Memory.IIdleReleasable
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

    /// <summary>Where drawing that ends while the page isn't shown is said; optional (tests).</summary>
    private readonly IActivityLog? _activityLog;

    /// <summary>The page is on screen (OnPageShown until OnPageLeft).</summary>
    private bool _isShown;

    private DateTime _leftUtc = DateTime.MinValue;

    /// <summary>The picked world isn't drawn on the page (let go while idle, or redrawn while the page was away): OnPageShown shows it.</summary>
    private bool _showPending;

    /// <summary>Stops the Reload or Rebuild all that is drawing; null when none is.</summary>
    private CancellationTokenSource? _reloadCancel;

    public WorldMapViewModel(WorldMapStore store, ISettingsService settings, string dataFolder, IActivityLog? activityLog = null)
    {
        _store = store;
        _settings = settings;
        _dataFolder = dataFolder;
        _activityLog = activityLog;
        Legend.CollectionChanged += OnLegendChanged;
        RestoreChoices();
        IcarusStarlink.Core.Memory.IdleMemory.Register(this);
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

    /// <summary>A Reload or Rebuild all is drawing (Stop shows next to the status line).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isDrawing;

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
        _isShown = true;
        // Let go while idle, or redrawn while the page was away: the same world comes back.
        if (_showPending)
        {
            ShowMap(SelectedMap);
        }

        if (IsBusy)
        {
            return;
        }

        _ = _loadedOnce ? CheckAsync() : LoadAsync();
    }

    /// <summary>The page was left for another: from now its images can be let go while the app is idle (ReleaseIfIdle).</summary>
    public void OnPageLeft()
    {
        _isShown = false;
        _leftUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// The picked world's picture, zone images and markers (well over 100 MB on Olympus) are let go while
    /// the app sits in the background with the Map page closed; opening the page shows them again.
    /// </summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        bool Release()
        {
            if (_isShown || IsBusy || _showPending || DateTime.UtcNow - _leftUtc < idleFor
                || (Picture is null && _layers.Count == 0 && Markers.Count == 0))
            {
                return false;
            }

            ShowMap(null); // clears the picture, layers, zone and highlight images, features, markers, drop sites
            _highlightBuffer = null;
            _highlightBitmap = null;
            _showPending = true;
            return true;
        }

        // Called on a background thread (BackgroundMemoryTrimmer); the images and lists belong to the UI thread.
        return UiDispatcher() is { } dispatcher && !dispatcher.CheckAccess() ? dispatcher.Invoke(Release) : Release();
    }

    private bool CanRun() => !IsBusy;

    /// <summary>Redraws the maps whose game files changed and says what changed.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task ReloadAsync() => RunReloadAsync(everything: false);

    /// <summary>Draws every map again, changed or not.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RebuildAllAsync() => RunReloadAsync(everything: true);

    /// <summary>Stops the drawing after the map it's on; what's drawn is kept, and the rest is drawn next time.</summary>
    [RelayCommand(CanExecute = nameof(IsDrawing))]
    private void Stop()
    {
        _reloadCancel?.Cancel();
        StatusMessage = "Stopping after the map being drawn now…";
    }

    /// <summary>
    /// Update everything's step: Reload, but only when Redraw during Update everything is ticked and
    /// the maps were all made before (making them the first time takes minutes; that waits until the
    /// Map page is opened). Says whether every map was redrawn (false: why not), and what changed.
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

        if (!_store.IsComplete())
        {
            return (false, "The maps haven't all been made yet: they're made when you open Map.");
        }

        if (IsBusy)
        {
            return (false, "The Map page is already busy drawing.");
        }

        // A reload that threw, or left a map undrawn, is Skipped with its message, not Done.
        var result = await RunReloadAsync(everything: false, forGameUpdate: true);
        return (result is { Failed.Count: 0 }, StatusMessage);
    }

    internal async Task LoadAsync()
    {
        if (Problem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }

        IsBusy = true;
        IReadOnlyList<WorldMapInfo>? maps = null;
        try
        {
            StatusMessage = "Reading the game's maps…";
            // Never made, or a build stopped half-way: finish it (the worlds already drawn are kept).
            if (!_store.IsComplete())
            {
                IsBusy = false;
                await RunReloadAsync(everything: false);
                return;
            }

            maps = await Task.Run(_store.ReadMaps);
            ShowMaps(maps);
            _loadedOnce = true;
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            // Started without anyone awaiting it (OnPageShown): anything not caught here left the page
            // half-drawn with "Reading the game's maps…" for good. Say what went wrong instead.
            StatusMessage = $"Couldn't read the maps: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        // With the worlds just read: the game's tables aren't read a second time for the check.
        await CheckWithAsync(maps);
    }

    /// <summary>The check that's running (or ran last): opening the page again while it runs doesn't start another.</summary>
    private Task? _checking;

    /// <summary>Compares the game's files with the drawn maps and puts up (or clears) the out-of-date bar.</summary>
    internal Task CheckAsync() => CheckWithAsync(null);

    /// <summary>A check still running (the page opened twice quickly) answers for both; <paramref name="maps"/> null reads them.</summary>
    private Task CheckWithAsync(IReadOnlyList<WorldMapInfo>? maps) =>
        _checking is { IsCompleted: false } running ? running : _checking = RunCheckAsync(maps);

    private async Task RunCheckAsync(IReadOnlyList<WorldMapInfo>? maps)
    {
        if (Problem() is not null)
        {
            return;
        }

        var reloadsBefore = _reloadsStarted;
        WorldMapCheckResult? check;
        try
        {
            check = await Task.Run(() => maps is null ? _store.Check() : _store.Check(maps));
        }
        catch (Exception)
        {
            // Only the out-of-date bar: if the check can't run, the bar just doesn't show.
            check = null;
        }

        // A Reload that started while this check ran redraws what it would report (and clears the bar
        // when done): its answer is out of date already, so it's dropped rather than shown after it.
        if (_reloadsStarted == reloadsBefore && !IsBusy)
        {
            OutOfDateMessage = check is { OutOfDate: true } ? check.Summary : null;
        }
    }

    /// <summary>Reloads started so far: a check that finishes after one started keeps its answer to itself.</summary>
    private int _reloadsStarted;

    /// <param name="forGameUpdate">Update everything's step: the picked world is loaded only if the page is shown, and no toast (the step reports it).</param>
    /// <returns>What the reload did (its Failed lists the maps it couldn't draw); null when it didn't run, was stopped or threw (StatusMessage says why).</returns>
    private async Task<WorldMapReloadResult?> RunReloadAsync(bool everything, bool forGameUpdate = false)
    {
        if (Problem() is { } problem)
        {
            StatusMessage = problem;
            return null;
        }

        _reloadsStarted++;
        using var stop = new CancellationTokenSource();
        _reloadCancel = stop;
        IsBusy = true;
        IsDrawing = true;
        try
        {
            StatusMessage = everything ? "Drawing every map again…" : "Checking the game's map files…";
            // A "Drawing …" line still queued when the reload ends is dropped, so it can't replace the final message.
            var drawing = true;
            var progress = new Progress<string>(text =>
            {
                if (Volatile.Read(ref drawing))
                {
                    StatusMessage = text;
                }
            });
            WorldMapReloadResult result;
            try
            {
                result = await Task.Run(() => _store.Reload(everything, progress, stop.Token));
            }
            finally
            {
                Volatile.Write(ref drawing, false);
            }

            // Never opened this session (Update everything): the maps are listed, the picked one waits for the page.
            ShowMaps(result.Maps, showPicked: !forGameUpdate || _isShown);
            _loadedOnce = true;
            OutOfDateMessage = null;
            StatusMessage = result.Summary;
            Toast(forGameUpdate, result.Summary, result.Failed.Count > 0 ? ActivityEntryKind.Warning : ActivityEntryKind.Success);
            return result;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stop: the maps drawn so far are kept (the index is saved after each). The next opening
            // of the page carries on from there (LoadAsync finishes a build that isn't complete).
            _loadedOnce = false;
            _pictureSources = null;
            if (SelectedMap is not null)
            {
                ShowPicked(_isShown); // its files may have just been redrawn
            }

            StatusMessage = everything
                ? "Stopped: the maps not redrawn yet keep their last drawing."
                : "Stopped: the maps drawn so far are kept. Reload, or opening Map again, draws the rest.";
            return null;
        }
        catch (Exception ex)
        {
            // Not awaited by anyone either (a button, the page opening): say what went wrong.
            StatusMessage = $"Couldn't make the maps: {ex.Message}";
            Toast(forGameUpdate, StatusMessage, ActivityEntryKind.Warning);
            return null;
        }
        finally
        {
            // A reload that threw may still have saved part of the index.
            _pictureSources = null;
            _reloadCancel = null;
            IsDrawing = false;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Drawing that ends while the Map page isn't shown (a first build takes minutes) is said in the
    /// activity panel: what changed and which maps couldn't be drawn. On the page the status line says
    /// it; Update everything reports its own step.
    /// </summary>
    private void Toast(bool forGameUpdate, string message, ActivityEntryKind kind)
    {
        if (!forGameUpdate && !_isShown)
        {
            _activityLog?.Log($"Map: {message}", kind);
        }
    }

    /// <summary>Why the maps can't be read at all, or null.</summary>
    private string? Problem()
    {
        if (string.IsNullOrWhiteSpace(_settings.Current.IcarusContentPath))
        {
            return "Set the game folder in Settings first: the maps are read from the game's files.";
        }

        // Without its paks every map would read as changed and draw as nothing.
        if (!Directory.Exists(Path.Combine(_settings.Current.IcarusContentPath, "Paks")))
        {
            return "The game folder in Settings has no Paks folder (was the game moved, or is its drive unplugged?): check the game folder in Settings.";
        }

        return Directory.Exists(Path.Combine(_dataFolder, "World"))
            ? null
            : "Refresh game data in Settings first: the maps' zones are read from it.";
    }

    /// <summary>The map list is being refilled: picking (and the picker's own null on Clear) doesn't show a world until it's done.</summary>
    private bool _refillingMaps;

    /// <summary>How each world's picture was made, read from index.json once per reload (not on every pick).</summary>
    private IReadOnlyDictionary<string, WorldPictureSource>? _pictureSources;

    private IReadOnlyDictionary<string, WorldPictureSource> PictureSourcesNow() => _pictureSources ??= _store.PictureSources();

    /// <param name="showPicked">False: the picked world isn't loaded now (the page isn't shown); OnPageShown shows it.</param>
    private void ShowMaps(IReadOnlyList<WorldMapInfo> maps, bool showPicked = true)
    {
        _pictureSources = null;
        var keep = SelectedMap?.Map.Id;
        _refillingMaps = true;
        try
        {
            Maps.Clear();
            // Open world maps, then mission-only maps, then outposts, then those no prospect uses yet; in development last within each.
            foreach (var map in maps.OrderBy(m => m.KindOrder).ThenBy(m => m.InDevelopment))
            {
                Maps.Add(new WorldMapItemViewModel(map));
            }

            SelectedMap = Maps.FirstOrDefault(m => m.Map.Id == keep) ?? Maps.FirstOrDefault();
        }
        finally
        {
            _refillingMaps = false;
        }

        // One ShowMap for the refilled list: the picker's own null on Clear and the new item above skip
        // it while the list refills, and the picked world's files may have been redrawn.
        ShowPicked(showPicked);
        OnPropertyChanged(nameof(MapEmptyText));
    }

    /// <summary>Shows the picked world, or (false) lets go of what's shown and leaves it for OnPageShown.</summary>
    private void ShowPicked(bool now)
    {
        if (now)
        {
            ShowMap(SelectedMap);
            return;
        }

        ShowMap(null);
        _showPending = true;
    }

    partial void OnSelectedMapChanged(WorldMapItemViewModel? value)
    {
        OnPropertyChanged(nameof(MapPickerPlaceholder));
        OnPropertyChanged(nameof(MapEmptyText));
        if (!_refillingMaps)
        {
            ShowMap(value);
        }
    }

    /// <summary>What the map picker says while it's empty; null once a map is picked.</summary>
    public string? MapPickerPlaceholder => SelectedMap is not null
        ? null
        : IsBusy ? "Reading the maps…" : Maps.Count == 0 ? "No maps yet: click Reload" : "Pick a map…";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(MapPickerPlaceholder));
        OnPropertyChanged(nameof(MapEmptyText));
    }

    partial void OnIsDrawingChanged(bool value) => OnPropertyChanged(nameof(MapEmptyText));

    partial void OnPictureChanged(ImageSource? value) => OnPropertyChanged(nameof(MapEmptyText));

    /// <summary>What the map area says while it has no picture (why, and what to do); null while it has one.</summary>
    public string? MapEmptyText =>
        Picture is not null ? null
        : IsDrawing ? "Drawing the maps: they show here when they're done."
        : IsBusy ? "Reading the maps…"
        : Problem() is { } problem ? problem
        : Maps.Count == 0 ? "No maps yet: click Reload."
        : SelectedMap is not { } item ? "Pick a map above."
        : NoPictureText(item.Map.Id);

    /// <summary>Why the picked world has no picture: not drawn yet, its file is gone, or the game has none.</summary>
    private string NoPictureText(string mapId) =>
        !PictureSourcesNow().TryGetValue(mapId, out var source) ? "This map isn't drawn yet: click Reload to draw it."
        : source == WorldPictureSource.None ? "No map picture for this world in the game files yet."
        : "This map's picture is missing or can't be read: click Reload to draw it again.";

    private void ShowMap(WorldMapItemViewModel? item)
    {
        _showPending = false;
        // The zone boxes ticked for the last world stay ticked for the next; the first world gets what
        // was ticked last time (first time ever: creatures only, as the game's fishing zones cover the
        // land too and together they hide the creature zones).
        var saved = SavedChoices();
        var shown = Layers.Count > 0
            ? Layers.Where(l => l.IsShown).Select(l => l.Kind).ToHashSet()
            : new[] { WorldLayerKind.Creatures, WorldLayerKind.Fishing, WorldLayerKind.Biomes }.Where(k => saved.Contains(k.ToString())).ToHashSet();
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
        // Features first: ShowLayers' highlight also writes the search line, from the places FindPlaces just found.
        if (item is null)
        {
            MapNote = null;
            ShowFeatures(null);
            ShowLayers();
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

        // Not in the index: never drawn, or its drawing failed. Then nothing is known about what the game has.
        var drawn = PictureSourcesNow().TryGetValue(map.Id, out var source);
        var notes = new List<string>();
        if (map.InDevelopment)
        {
            notes.Add("In development: the game has this world in its files but doesn't offer it yet, so the map may be unfinished or change.");
        }

        if (Picture is null)
        {
            notes.Add(NoPictureText(map.Id));
        }
        else if (source == WorldPictureSource.Relief)
        {
            notes.Add("The game has no map picture for it yet: this one is drawn from its height data.");
        }

        if (drawn && (Picture is not null || source == WorldPictureSource.None) && Layers.Count == 0)
        {
            notes.Add("No creature, fishing or biome zones for it in the game files yet.");
        }

        MapNote = notes.Count > 0 ? string.Join(" ", notes) : null;
        OnPropertyChanged(nameof(HasLayers));
        ShowFeatures(map);
        ShowLayers();
    }

    private void OnLayerTicked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorldLayerOption.IsShown))
        {
            Changed(ShowLayers);
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

    /// <summary>
    /// Typing waits for a short pause before it searches: each search can redraw every marker and a
    /// whole-map highlight, and doing that on every key made typing lag. Without a window (tests), or
    /// off its thread, it searches at once.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        if (UiDispatcher() is not { } dispatcher || !dispatcher.CheckAccess())
        {
            RunSearch();
            return;
        }

        _searchPending = true;
        _searchPause ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => RunPendingSearch(), dispatcher);
        _searchPause.Stop();
        _searchPause.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _searchPause;

    /// <summary>The window's dispatcher (null without one: tests). Typing waits on it before searching.</summary>
    internal Func<System.Windows.Threading.Dispatcher?> UiDispatcher { get; set; } = () => System.Windows.Application.Current?.Dispatcher;

    /// <summary>A search typed but not run yet (the pause hasn't passed).</summary>
    private bool _searchPending;

    /// <summary>Runs the search still waiting for its pause, if there is one.</summary>
    private void RunPendingSearch()
    {
        _searchPause?.Stop();
        if (_searchPending)
        {
            RunSearch();
        }
    }

    private void RunSearch()
    {
        _searchPending = false;
        FindPlaces();
        UpdateHighlight();
    }

    /// <summary>The ticked layers, bottom first: biomes under creatures under fishing.</summary>
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
        // Map only: nothing over the picture, so nothing to say on hover either.
        var hits = x is { } hx && y is { } hy && !PictureOnly ? ZonesAt(hx, hy) : [];
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
        if (PictureOnly)
        {
            return;
        }

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

    /// <summary>What the highlight image was last drawn for (layers, found zones, clicked and hovered zone); null when there's none.</summary>
    private string? _highlightDrawn;

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
        var places = _found.Count;
        if (query.Length == 0)
        {
            SearchResult = null;
        }
        else if (GridSquareAt(query) is not null)
        {
            SearchResult = $"Grid {query.ToUpperInvariant()}";
        }
        else if (foundCount == 0 && places == 0)
        {
            SearchResult = $"Nothing here has \"{query}\" (zones: only ticked layers).";
        }
        else
        {
            var what = string.Join(" and ", new[] { Count(foundCount, "zone"), Count(places, "place") }.OfType<string>());
            SearchResult = $"Found in {what}." + (places > 0 ? " Enter zooms to them." : "");
        }

        static string? Count(int n, string what) => n switch { 0 => null, 1 => $"1 {what}", _ => $"{n} {what}s" };

        if (layers.Count == 0 || (foundCount == 0 && _selectedColors.Count == 0 && _hoverColors.Count == 0))
        {
            HighlightImage = null;
            _highlightDrawn = null;
            return;
        }

        // What the highlight shows; when it's what's drawn already, the whole-map pixel pass is skipped
        // (it ran on every search key and hover move, millions of pixels each time).
        var drawing = string.Join(";", layers.Select(l =>
            $"{l.Kind}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l.Pixels)}:"
            + $"{(_selectedColors.TryGetValue(l.Kind, out var s) ? s : -1)}:{(_hoverColors.TryGetValue(l.Kind, out var h) ? h : -1)}:"
            + string.Join(",", found[l.Kind].Order())));
        if (drawing == _highlightDrawn && HighlightImage is not null)
        {
            return;
        }

        _highlightDrawn = drawing;
        var size = layers.Max(l => l.Pixels.Size);
        // One buffer and one bitmap, reused while the size stays: a new pair on every zone the mouse
        // crossed was two whole-map allocations (16 MB each at 2048²) and a new texture each time.
        if (_highlightBuffer is null || _highlightBitmap is null || _highlightBitmap.PixelWidth != size)
        {
            _highlightBuffer = new uint[size * size];
            _highlightBitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Pbgra32, null);
        }
        else
        {
            Array.Clear(_highlightBuffer);
        }

        var pixels = _highlightBuffer;
        foreach (var (kind, layer, _) in layers)
        {
            var selected = _selectedColors.TryGetValue(kind, out var s) ? s : (uint?)null;
            var hover = _hoverColors.TryGetValue(kind, out var h) ? h : (uint?)null;
            var hits = found[kind];
            if (selected is null && hover is null && hits.Count == 0)
            {
                continue;
            }

            var sameSize = layer.Size == size;
            for (var y = 0; y < size; y++)
            {
                var row = sameSize ? y * size : y * layer.Size / size * layer.Size;
                for (var x = 0; x < size; x++)
                {
                    var color = layer.Pixels[sameSize ? row + x : row + x * layer.Size / size];
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

        // Not frozen: this always runs on the UI thread (mouse, the search pause, awaited continuations).
        // The same bitmap set again raises nothing; WritePixels makes the image redraw by itself.
        _highlightBitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        HighlightImage = _highlightBitmap;
    }

    /// <summary>The highlight's pixels and bitmap, kept for the next highlight of the same size.</summary>
    private uint[]? _highlightBuffer;
    private WriteableBitmap? _highlightBitmap;

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
        // FormatException (WPF's FileFormatException) and COMException: a half-written picture.jpg (a
        // crash while it was written) shows as missing instead of stopping the map from showing.
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException
            or FormatException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
