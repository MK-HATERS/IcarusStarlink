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
/// What changed: for a world a Reload found changed, the switch lights up the zones whose spawns
/// changed and marks the spots that are new or gone, with the same in words in the side panel.
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

    /// <summary>"What changed on 17 May 2030"; null without a record.</summary>
    public string? ChangeTitle => _change is { } change ? $"What changed on {DateText(change.When)}" : null;

    /// <summary>The switch's tooltip: what it does, and how much there is to see.</summary>
    public string ChangesToolTip => _change is { } change
        ? $"On: lights up the zones whose spawns changed and marks the spots that are new (+) or gone (−) since this map last changed, on {DateText(change.When)}, with a list beside the map. {ChangeCountText(change)}. Kept until this map changes again."
        : "Nothing is recorded for this map.";

    /// <summary>What changed, in words: zones first, then new spots, then gone ones.</summary>
    public ObservableCollection<WorldChangeLineViewModel> ChangeLines { get; } = [];

    /// <summary>The new and gone spots' marks on the map (only while the switch is on).</summary>
    public ObservableCollection<WorldChangeMarkerViewModel> ChangeMarkers { get; } = [];

    /// <summary>The changed zones, lit up over the map (only while the switch is on); null when none can be shown.</summary>
    [ObservableProperty]
    private ImageSource? _changesImage;

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

    /// <summary>The mark for a world in the map picker: "Changed on 17 May 2030", or null.</summary>
    private string? ChangedText(string mapId) =>
        ChangeRecordsNow().TryGetValue(mapId, out var change) ? $"Changed on {DateText(change.When)}: pick it and switch on What changed to see what." : null;

    private static string DateText(DateTime whenUtc) =>
        DateTime.SpecifyKind(whenUtc, DateTimeKind.Utc).ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>"2 zones changed, 3 new spots, 1 spot gone".</summary>
    private static string ChangeCountText(WorldMapChangeRecord change)
    {
        var parts = new List<string>();
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

        return string.Join(", ", parts);
    }

    /// <summary>The picked world's record: its list in words, and (with the switch on) its zones and marks on the map.</summary>
    private void ShowChangeRecord(WorldMapInfo? map)
    {
        _change = map is not null && ChangeRecordsNow().TryGetValue(map.Id, out var change) ? change : null;
        _changeMap = map;
        ChangeLines.Clear();
        if (_change is { } record && map is not null)
        {
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

            AddSpotLines(map, record.Added, record.AddedCount, "New");
            AddSpotLines(map, record.Removed, record.RemovedCount, "Gone");
        }

        OnPropertyChanged(nameof(HasChangeRecord));
        OnPropertyChanged(nameof(ShowChangeList));
        OnPropertyChanged(nameof(ChangeTitle));
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
            return;
        }

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
}
