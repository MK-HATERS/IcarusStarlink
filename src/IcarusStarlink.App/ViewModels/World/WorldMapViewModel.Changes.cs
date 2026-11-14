using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.World;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One line of the What changed list; a spot's line also knows where it is, so a click can zoom to it.</summary>
public sealed record WorldChangeLineViewModel(string Text, WorldMapPoint? At = null)
{
    /// <summary>The line is a spot on the map (clicking it zooms there).</summary>
    public bool CanZoom => At is not null;

    public override string ToString() => Text;
}

/// <summary>A mark on the map for a spot that's new ("+", green) or gone ("−", red) since the world last changed.</summary>
public sealed record WorldChangeMarkerViewModel(WorldFeature Feature, bool IsAdded, string ToolTip)
{
    public double X => Feature.At.U * WorldMapViewModel.MapUnits;

    public double Y => Feature.At.V * WorldMapViewModel.MapUnits;

    public string Glyph => IsAdded ? "+" : "−";

    /// <summary>True for a spot that's gone (the view colours it red, a new one green).</summary>
    public bool IsRemoved => !IsAdded;
}

/// <summary>
/// What changed: for a world a Reload found changed, the switch tints the grid squares where the
/// picture or a zone's shape changed, lights up the zones whose spawns changed and marks the spots
/// that are new or gone, with the same in words in the side panel, along with the game files that
/// changed (new level files say what was worked on). A world the game added is tagged New.
/// </summary>
public sealed partial class WorldMapViewModel
{
    /// <summary>The most spot lines listed for each of new and gone (the marks on the map aren't cut).</summary>
    private const int MaxChangeLines = 60;

    /// <summary>What changed on each world, read from index.json once per reload (not on every pick).</summary>
    private IReadOnlyDictionary<string, WorldMapChangeRecord>? _changeRecords;

    private IReadOnlyDictionary<string, WorldMapChangeRecord> ChangeRecordsNow() => _changeRecords ??= _store.ChangeRecords();

    /// <summary>The shown world's record; null when nothing is recorded for it.</summary>
    private WorldMapChangeRecord? _change;

    /// <summary>The world _change is for.</summary>
    private WorldMapInfo? _changeMap;

    /// <summary>The What changed switch: on, the changed zones are lit up and new and gone spots marked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChangeList))]
    private bool _showChanges;

    /// <summary>The shown world has something recorded (the switch shows only then).</summary>
    public bool HasChangeRecord => _change is not null;

    /// <summary>The list in the side panel shows: the switch is on and this world has a record.</summary>
    public bool ShowChangeList => ShowChanges && HasChangeRecord;

    /// <summary>"What changed on 17 May 2030", or "New map, first seen on 17 May 2030"; null without a record.</summary>
    public string? ChangeTitle => _change switch
    {
        null => null,
        { NewMap: true } change => $"New map, first seen on {DateText(change.When)}",
        var change => $"What changed on {DateText(change.When)}",
    };

    /// <summary>"Found by the Reload on 17 May 2030 at 09:30. Kept until this map changes again."; null without a record.</summary>
    public string? ChangeWhenText => _change is { } change
        ? $"Found by the Reload on {DateText(change.When)} at {TimeText(change.When)}. Kept until this map changes again."
        : null;

    /// <summary>The switch's tooltip: what it does, and how much there is to see.</summary>
    public string ChangesToolTip => _change switch
    {
        null => "Nothing is recorded for this map.",
        { NewMap: true } change => $"On: says beside the map that the game added this map, first seen by the Reload on {DateText(change.When)}. Kept until this map changes.",
        var change => "On: shows what the game changed on this map: squares where the picture (pink) or a zone's shape (yellow) changed, "
            + "zones whose spawns changed (light blue), spots that are new (+) or gone (−), and the game files that changed, with a list beside the map. "
            + $"Found on {DateText(change.When)}: {ChangeCountText(change)}. Kept until this map changes again.",
    };

    /// <summary>What changed, in words: the picture, zone shapes, zones' spawns, game files, then new spots and gone ones.</summary>
    public ObservableCollection<WorldChangeLineViewModel> ChangeLines { get; } = [];

    /// <summary>The new and gone spots' marks on the map (only while the switch is on).</summary>
    public ObservableCollection<WorldChangeMarkerViewModel> ChangeMarkers { get; } = [];

    /// <summary>The changed zones, lit up over the map (only while the switch is on); null when none can be shown.</summary>
    [ObservableProperty]
    private ImageSource? _changesImage;

    /// <summary>The grid squares where the picture changed, tinted pink (one pixel per square; only while the switch is on); null when none.</summary>
    [ObservableProperty]
    private ImageSource? _pictureChangesImage;

    /// <summary>The grid squares where a zone's shape changed, tinted yellow (one pixel per square; only while the switch is on); null when none.</summary>
    [ObservableProperty]
    private ImageSource? _shapeChangesImage;

    partial void OnShowChangesChanged(bool value) => DrawChanges();

    /// <summary>A line of the list was clicked: its spot in the middle of the map, close up.</summary>
    [RelayCommand]
    private void ZoomToChange(WorldChangeLineViewModel? line)
    {
        if (line?.At is { } at)
        {
            ZoomRequested?.Invoke(at, 0.08);
        }
    }

    /// <summary>The mark for a world in the map picker: "Changed on 17 May 2030…", "New: the game added this map…", or null.</summary>
    private string? ChangedText(string mapId) =>
        !ChangeRecordsNow().TryGetValue(mapId, out var change) ? null
        : change.NewMap ? $"New: the game added this map. First seen by the Reload on {DateText(change.When)}."
        : $"Changed on {DateText(change.When)}: pick it and switch on What changed to see what.";

    /// <summary>The game added this world since the Reload before the last (the picker tags it New).</summary>
    private bool IsNewMap(string mapId) => ChangeRecordsNow().TryGetValue(mapId, out var change) && change.NewMap;

    private static string DateText(DateTime whenUtc) =>
        DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc).ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    private static string TimeText(DateTime whenUtc) =>
        DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc).ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    /// <summary>The most grid squares named in one line; more are counted instead ("in 40 of 256 grid squares").</summary>
    private const int MaxNamedSquares = 12;

    /// <summary>The most file names listed in one line; the rest are counted ("and 12 more").</summary>
    private const int MaxNamedFiles = 12;

    /// <summary>"in D4, D5, E4", or (many squares, or a world without the in-game grid) "in 40 of 256 grid squares (tinted pink on the map)".</summary>
    private static string WhereText(WorldImageChange change, WorldMapInfo map, string tint)
    {
        var onGrid = map.GridSize == (change.Columns, change.Rows);
        if (onGrid && change.Cells.Count <= MaxNamedSquares)
        {
            return "in " + string.Join(", ", change.Cells
                .Select(cell => (Column: cell % change.Columns, Row: cell / change.Columns))
                .OrderBy(c => c.Column).ThenBy(c => c.Row)
                .Select(c => $"{(char)('A' + c.Column)}{c.Row + 1}"));
        }

        var total = change.Columns * change.Rows;
        return onGrid
            ? $"in {change.Cells.Count} of {total} grid squares (tinted {tint} on the map)"
            : $"in {change.Cells.Count} of {total} parts of the map (tinted {tint})";
    }

    /// <summary>"Picture changed in D4, D5, E4.", "The whole picture changed."</summary>
    private static string PictureLine(WorldImageChange change, WorldMapInfo map) => change switch
    {
        { Note: "new" } => "The game now has a picture for this map.",
        { Note: "gone" } => "The game no longer has a picture for this map.",
        { Note: "real" } => "The game now has its own picture for this map (before, it was drawn from height data).",
        { Whole: true } => "The whole picture changed.",
        _ => $"Picture changed {WhereText(change, map, "pink")}.",
    };

    /// <summary>"Creature zones: their shapes changed in D4, D5."</summary>
    private static string ShapeLine(WorldImageChange change, WorldMapInfo map)
    {
        var title = change.What switch
        {
            nameof(WorldLayerKind.Creatures) => "Creature zones",
            nameof(WorldLayerKind.Fishing) => "Fishing zones",
            _ => "Biomes",
        };
        return change switch
        {
            { Note: "new" } => $"{title}: new on this map.",
            { Note: "gone" } => $"{title}: no longer on this map.",
            { Whole: true } => $"{title}: their shapes changed all over the map.",
            _ => $"{title}: their shapes changed {WhereText(change, map, "yellow")}.",
        };
    }

    /// <summary>"Level files changed: A, B.", "New level files: C.", "Level files gone: D."; long lists end "and 12 more".</summary>
    private static IEnumerable<string> FileLines(WorldFileChange files)
    {
        if (files.ChangedCount > 0)
        {
            yield return $"{files.Kind} changed: {Names(files.Changed, files.ChangedCount)}.";
        }

        if (files.AddedCount > 0)
        {
            yield return $"New {files.Kind.ToLowerInvariant()}: {Names(files.Added, files.AddedCount)}.";
        }

        if (files.GoneCount > 0)
        {
            yield return $"{files.Kind} gone: {Names(files.Gone, files.GoneCount)}.";
        }

        static string Names(IReadOnlyList<string> names, int total)
        {
            var shown = Math.Min(names.Count, MaxNamedFiles);
            return string.Join(", ", names.Take(shown)) + (total > shown ? $" and {total - shown} more" : "");
        }
    }

    /// <summary>"picture changed, 2 zones changed, 3 new spots, 1 spot gone, 14 game files changed".</summary>
    private static string ChangeCountText(WorldMapChangeRecord change)
    {
        var parts = new List<string>();
        if (change.Picture is not null)
        {
            parts.Add("picture changed");
        }

        if (change.Shapes.Count > 0)
        {
            parts.Add("zone shapes changed");
        }

        var files = change.Files.Sum(f => f.ChangedCount + f.AddedCount + f.GoneCount);
        if (files > 0)
        {
            parts.Add(files == 1 ? "1 game file changed" : $"{files} game files changed");
        }

        AddCounts(parts, change);
        return parts.Count > 0 ? string.Join(", ", parts) : "the game's files changed, with nothing to see on the map";
    }

    private static void AddCounts(List<string> parts, WorldMapChangeRecord change)
    {
        if (change.Zones.Count > 0)
        {
            parts.Add(change.Zones.Count == 1 ? "1 zone changed" : $"{change.Zones.Count} zones changed");
        }

        if (change.AddedCount > 0)
        {
            parts.Add(change.AddedCount == 1 ? "1 new spot" : $"{change.AddedCount} new spots");
        }

        if (change.RemovedCount > 0)
        {
            parts.Add(change.RemovedCount == 1 ? "1 spot gone" : $"{change.RemovedCount} spots gone");
        }
    }

    /// <summary>The picked world's record: its list in words, and (with the switch on) its zones and marks on the map.</summary>
    private void ShowChangeRecord(WorldMapInfo? map)
    {
        _change = map is not null && ChangeRecordsNow().TryGetValue(map.Id, out var change) ? change : null;
        _changeMap = map;
        ChangeLines.Clear();
        if (_change is { } record && map is not null)
        {
            if (record.NewMap)
            {
                ChangeLines.Add(new WorldChangeLineViewModel("New map: the game added this world. What changes on it from now on is listed here."));
            }

            if (record.Picture is { } picture)
            {
                ChangeLines.Add(new WorldChangeLineViewModel(PictureLine(picture, map)));
            }

            foreach (var shape in record.Shapes)
            {
                ChangeLines.Add(new WorldChangeLineViewModel(ShapeLine(shape, map)));
            }

            foreach (var zone in record.Zones)
            {
                var kind = zone.Layer == nameof(WorldLayerKind.Fishing) ? "Fishing zone" : "Creature zone";
                ChangeLines.Add(new WorldChangeLineViewModel(zone.What switch
                {
                    "added" => $"{kind} {zone.Name} is new.",
                    "gone" => $"{kind} {zone.Name} is gone.",
                    _ => $"{kind} {zone.Name}: {zone.What}.",
                }));
            }

            foreach (var line in record.Files.SelectMany(FileLines))
            {
                ChangeLines.Add(new WorldChangeLineViewModel(line));
            }

            AddSpotLines(map, record.Added, record.AddedCount, "New");
            AddSpotLines(map, record.Removed, record.RemovedCount, "Gone");
            if (ChangeLines.Count == 0)
            {
                ChangeLines.Add(new WorldChangeLineViewModel("The game changed this map's files, but nothing that shows here: its picture, zones and spots are as they were."));
            }
        }

        OnPropertyChanged(nameof(HasChangeRecord));
        OnPropertyChanged(nameof(ShowChangeList));
        OnPropertyChanged(nameof(ChangeTitle));
        OnPropertyChanged(nameof(ChangeWhenText));
        OnPropertyChanged(nameof(ChangesToolTip));
        DrawChanges();
    }

    private void AddSpotLines(WorldMapInfo map, IReadOnlyList<WorldFeature> spots, int total, string word)
    {
        foreach (var spot in spots.Take(MaxChangeLines))
        {
            ChangeLines.Add(new WorldChangeLineViewModel(SpotLine(map, spot, word), spot.At));
        }

        if (total > Math.Min(spots.Count, MaxChangeLines))
        {
            var more = total - Math.Min(spots.Count, MaxChangeLines);
            ChangeLines.Add(new WorldChangeLineViewModel(word == "New"
                ? $"…and {more} more new {(more == 1 ? "spot" : "spots")}."
                : $"…and {more} more {(more == 1 ? "spot" : "spots")} gone."));
        }
    }

    /// <summary>"New: Tier 2 cave · 4-6 cave worms (D10)", "Gone: Oil geyser (F4)".</summary>
    private string SpotLine(WorldMapInfo map, WorldFeature spot, string word)
    {
        var what = spot.Kind switch
        {
            WorldFeatureKind.Cave or WorldFeatureKind.BigCave => CaveLine(spot),
            WorldFeatureKind.MissionSpot => MissionText(map, spot),
            WorldFeatureKind.ExoticSpot when spot.Category is { } kind => $"{spot.Name} ({kind})",
            _ => spot.Name,
        };
        what = what.Length > 0 ? char.ToUpperInvariant(what[0]) + what[1..] : what;
        return GridSquare(spot.At) is { } square ? $"{word}: {what} ({square})" : $"{word}: {what}";
    }

    /// <summary>"POTSHOT (Forest range) · Bear", from the spot's tag and the world's mission names.</summary>
    private static string MissionText(WorldMapInfo map, WorldFeature spot)
    {
        var (category, name) = WorldMissionNames.Describe(spot.Category, spot.Name, map.MissionNames);
        return name.Length > 0 ? $"{category} · {name}" : category;
    }

    /// <summary>The changed zones lit up and the new and gone spots marked, or nothing while the switch is off.</summary>
    private void DrawChanges()
    {
        ChangeMarkers.Clear();
        if (!ShowChanges || _change is not { } change || _changeMap is not { } map)
        {
            ChangesImage = null;
            PictureChangesImage = null;
            ShapeChangesImage = null;
            return;
        }

        // A wholly changed image is said once in the list, not tinted over the whole map.
        PictureChangesImage = change.Picture is { } picture ? DrawChangedSquares([picture], PictureChangeColor) : null;
        ShapeChangesImage = DrawChangedSquares(change.Shapes, ShapeChangeColor);

        foreach (var spot in change.Added)
        {
            ChangeMarkers.Add(new WorldChangeMarkerViewModel(spot, IsAdded: true, SpotLine(map, spot, "New")));
        }

        foreach (var spot in change.Removed)
        {
            ChangeMarkers.Add(new WorldChangeMarkerViewModel(spot, IsAdded: false, SpotLine(map, spot, "Gone")));
        }

        ChangesImage = DrawChangedZones(change);
    }

    /// <summary>
    /// The zones whose spawns changed, in one colour, whether their layer is ticked or not (a zone
    /// that's gone has no place on the map any more: only the list names it).
    /// </summary>
    private ImageSource? DrawChangedZones(WorldMapChangeRecord change)
    {
        var lit = new List<(WorldLayerPixels Pixels, HashSet<uint> Colors)>();
        foreach (var group in change.Zones.Where(z => z.What != "gone").GroupBy(z => z.Layer))
        {
            if (!Enum.TryParse<WorldLayerKind>(group.Key, out var kind) || !_layers.TryGetValue(kind, out var layer))
            {
                continue;
            }

            var rows = group.Select(z => z.Row).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var colors = layer.Zones.Where(kv => rows.Contains(kv.Value.Row)).Select(kv => kv.Key).ToHashSet();
            if (colors.Count > 0)
            {
                lit.Add((layer.Pixels, colors));
            }
        }

        if (lit.Count == 0)
        {
            return null;
        }

        var size = lit.Max(l => l.Pixels.Size);
        var pixels = new uint[size * size];
        foreach (var (layer, colors) in lit)
        {
            for (var y = 0; y < size; y++)
            {
                var row = y * layer.Size / size * layer.Size;
                for (var x = 0; x < size; x++)
                {
                    if (colors.Contains(layer.Pixels[row + x * layer.Size / size]))
                    {
                        pixels[y * size + x] = ChangedZoneColor;
                    }
                }
            }
        }

        return ToBitmap(pixels, size);
    }

    /// <summary>Premultiplied BGRA: a light blue, apart from the orange of a clicked or found zone.</summary>
    internal const uint ChangedZoneColor = 0xA0288CA0;

    /// <summary>Premultiplied BGRA: a soft pink over the squares where the picture changed (the ground still shows through).</summary>
    internal const uint PictureChangeColor = 0x5A5A1446;

    /// <summary>Premultiplied BGRA: a soft yellow over the squares where a zone's shape changed.</summary>
    internal const uint ShapeChangeColor = 0x5A5A5000;

    /// <summary>
    /// The changed squares of the given images as one small picture, a pixel per square (the view
    /// stretches it over the map without smoothing); null when no square is listed. Images counted
    /// in another grid than the first are left out.
    /// </summary>
    private static ImageSource? DrawChangedSquares(IReadOnlyList<WorldImageChange> changes, uint color)
    {
        if (changes.FirstOrDefault(c => c.Cells.Count > 0) is not { } first)
        {
            return null;
        }

        var pixels = new uint[first.Columns * first.Rows];
        foreach (var cell in changes.Where(c => c.Columns == first.Columns && c.Rows == first.Rows).SelectMany(c => c.Cells))
        {
            if (cell >= 0 && cell < pixels.Length)
            {
                pixels[cell] = color;
            }
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(first.Columns, first.Rows, 96, 96, PixelFormats.Pbgra32, null, pixels, first.Columns * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
