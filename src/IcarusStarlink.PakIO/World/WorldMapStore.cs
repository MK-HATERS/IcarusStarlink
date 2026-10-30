using System.Text.Json;

namespace IcarusStarlink.PakIO.World;

/// <param name="Summary">One line for the page: what changed ("Styx: map redrawn; Olympus, Conifer Hard: Desert Wolf added").</param>
/// <param name="Redrawn">How many maps were drawn again.</param>
public sealed record WorldMapReloadResult(string Summary, int Redrawn, IReadOnlyList<WorldMapInfo> Maps);

/// <param name="OutOfDate">The game's map files or zones differ from what the maps were made from (or they were never made).</param>
public sealed record WorldMapCheckResult(bool OutOfDate, string Summary);

/// <summary>
/// The drawn maps, kept in one folder (Cache\World): &lt;world id&gt;\picture.jpg and one
/// &lt;layer&gt;.png per zone image and features.json (caves, geysers, spots, water), plus index.json
/// with what each was made from (where the game keeps its files, and every zone's contents). One
/// copy only: a map that changed is deleted and drawn again, a world the game dropped is deleted.
/// Reload redraws only what changed; Rebuild all deletes everything and draws every map again.
/// </summary>
public sealed class WorldMapStore(string root, Func<IReadOnlyList<WorldMapInfo>> readCatalog, IWorldMapRenderer renderer)
{
    private const string IndexFile = "index.json";
    private const string PictureFile = "picture.jpg";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Root { get; } = root;

    /// <summary>The worlds and their zones as installed now (read from the tables, nothing drawn).</summary>
    public IReadOnlyList<WorldMapInfo> ReadMaps() => readCatalog();

    public string PicturePath(string mapId) => Path.Combine(Root, Safe(mapId), PictureFile);

    public string LayerPath(string mapId, WorldLayerKind kind) => Path.Combine(Root, Safe(mapId), kind + ".png");

    private const string FeaturesFile = "features.json";

    /// <summary>What's placed on the world (caves, geysers, spots, water) as read when it was drawn; None when it hasn't been.</summary>
    public WorldFeatures ReadFeatures(string mapId)
    {
        try
        {
            var path = Path.Combine(Root, Safe(mapId), FeaturesFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<WorldFeatures>(File.ReadAllText(path)) ?? WorldFeatures.None : WorldFeatures.None;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return WorldFeatures.None;
        }
    }

    /// <summary>The world's zone image as drawn, or null when it hasn't been (or can't be read).</summary>
    public WorldLayerPixels? ReadLayer(string mapId, WorldLayerKind kind)
    {
        try
        {
            var path = LayerPath(mapId, kind);
            return File.Exists(path) ? WorldLayerPixels.FromPng(File.ReadAllBytes(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>How each world's picture was made (Picture, Relief, None); a world missing here hasn't been drawn.</summary>
    public IReadOnlyDictionary<string, WorldPictureSource> PictureSources() =>
        (LoadIndex()?.Maps ?? []).ToDictionary(kv => kv.Key, kv => kv.Value.Picture, StringComparer.OrdinalIgnoreCase);

    /// <summary>Compares the game's files and zones with what the maps were made from, without drawing anything.</summary>
    public WorldMapCheckResult Check()
    {
        var index = LoadIndex();
        var maps = readCatalog();
        if (index is null)
        {
            return new WorldMapCheckResult(maps.Count > 0, maps.Count > 0 ? "The maps haven't been made yet." : "");
        }

        var changes = Changes(maps, index, maps.ToDictionary(m => m.Id, renderer.Stamp, StringComparer.OrdinalIgnoreCase));
        var missing = maps.Where(m => !IsDrawn(m, index)).ToList();
        return changes.Count == 0 && missing.Count == 0
            ? new WorldMapCheckResult(false, "")
            : new WorldMapCheckResult(true, "The game's files changed since these maps were made: " + Join(changes.Count > 0 ? changes : [.. missing.Select(m => $"{m.Name}: not made yet")]) + ".");
    }

    /// <summary>
    /// Draws the maps whose files changed or aren't drawn (all of them when <paramref name="everything"/>),
    /// deletes worlds the game dropped, and says what changed. A world that fails to draw is left out
    /// of the index (named in the summary), so the next Reload tries it again.
    /// </summary>
    public WorldMapReloadResult Reload(bool everything, IProgress<string>? progress = null)
    {
        var maps = readCatalog();
        var previous = LoadIndex();
        var old = everything ? null : previous;
        if (everything)
        {
            TryDelete(Root);
        }

        Directory.CreateDirectory(Root);
        var stamps = maps.ToDictionary(m => m.Id, renderer.Stamp, StringComparer.OrdinalIgnoreCase);
        var index = new WorldMapIndex();
        var redrawn = 0;
        var failed = new List<string>();
        foreach (var map in maps)
        {
            var entry = old?.Maps.GetValueOrDefault(map.Id);
            if (entry is null || entry.Stamp != stamps[map.Id] || !IsDrawn(map, old!))
            {
                progress?.Report($"Drawing {map.Name} and reading its caves and spots ({redrawn + 1} of up to {maps.Count}; a big map takes about a minute)…");
                try
                {
                    entry = Draw(map, stamps[map.Id]);
                }
                catch (Exception ex)
                {
                    // One world that can't be drawn (a mod's odd tiles, a locked pak) mustn't stop the
                    // others or leave the index unsaved.
                    TryDelete(Path.Combine(Root, Safe(map.Id)));
                    failed.Add($"{map.Name} ({ex.Message})");
                    continue;
                }

                redrawn++;
            }

            index.Maps[map.Id] = entry with { Zones = ZoneFingerprints(map) };
        }

        // Worlds the game no longer has: their folders go.
        foreach (var folder in SafeDirectories())
        {
            if (!maps.Any(m => string.Equals(Safe(m.Id), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)))
            {
                TryDelete(folder);
            }
        }

        SaveIndex(index);

        var changes = previous is null ? [] : Changes(maps, previous, stamps);
        string summary;
        if (previous is null)
        {
            summary = $"Made {Count(maps.Count)}.";
        }
        else if (everything)
        {
            summary = $"Rebuilt all {Count(maps.Count)}." + (changes.Count > 0 ? " Changed since last time: " + Join(changes) + "." : " Nothing had changed.");
        }
        else
        {
            summary = changes.Count > 0
                ? "Changed: " + Join(changes) + "." + (maps.Count - changes.Count > 0 ? $" {Count(maps.Count - changes.Count)} unchanged." : "")
                : maps.Count == 1 ? "Nothing changed: the map is up to date." : $"Nothing changed: all {Count(maps.Count)} are up to date.";
        }

        if (failed.Count > 0)
        {
            summary += $" Couldn't draw {Join(failed)}: Reload tries again.";
        }

        return new WorldMapReloadResult(summary, redrawn, maps);
    }

    private WorldMapIndexEntry Draw(WorldMapInfo map, string stamp)
    {
        var folder = Path.Combine(Root, Safe(map.Id));
        TryDelete(folder);
        Directory.CreateDirectory(folder);
        var (jpeg, source) = renderer.RenderPicture(map);
        if (jpeg is not null)
        {
            File.WriteAllBytes(Path.Combine(folder, PictureFile), jpeg);
        }

        var layers = new List<WorldLayerKind>();
        foreach (var layer in map.Layers)
        {
            if (layer.HeatmapAssetPath is { } path && renderer.ReadLayer(path) is { } pixels)
            {
                File.WriteAllBytes(LayerPath(map.Id, layer.Kind), pixels.ToPng());
                layers.Add(layer.Kind);
            }
        }

        var features = renderer.ReadFeatures(map);
        File.WriteAllText(Path.Combine(folder, FeaturesFile), JsonSerializer.Serialize(features));

        return new WorldMapIndexEntry { Stamp = stamp, Picture = source, Layers = layers };
    }

    private bool IsDrawn(WorldMapInfo map, WorldMapIndex index) =>
        index.Maps.GetValueOrDefault(map.Id) is { } entry
        && (entry.Picture == WorldPictureSource.None || File.Exists(PicturePath(map.Id)))
        && File.Exists(Path.Combine(Root, Safe(map.Id), FeaturesFile))
        && entry.Layers.All(kind => File.Exists(LayerPath(map.Id, kind)));

    /// <summary>
    /// What differs between the maps now and what an index was made from, one item per world:
    /// "Styx: map redrawn", "Olympus, Conifer Hard: Desert Wolf added, Bear gone", "ARK: new map".
    /// </summary>
    private static List<string> Changes(IReadOnlyList<WorldMapInfo> maps, WorldMapIndex index, IReadOnlyDictionary<string, string> stamps)
    {
        var changes = new List<string>();
        foreach (var map in maps)
        {
            if (index.Maps.GetValueOrDefault(map.Id) is not { } before)
            {
                changes.Add($"{map.Name}: new map");
                continue;
            }

            var parts = new List<string>();
            if (before.Stamp != stamps.GetValueOrDefault(map.Id))
            {
                parts.Add("map redrawn");
            }

            var now = ZoneFingerprints(map);
            foreach (var (layer, zones) in now)
            {
                var old = before.Zones.GetValueOrDefault(layer) ?? [];
                foreach (var (row, fingerprint) in zones)
                {
                    if (!old.TryGetValue(row, out var oldFingerprint))
                    {
                        parts.Add($"{WorldMapCatalog.ZoneName(row)} added");
                    }
                    else if (oldFingerprint != fingerprint)
                    {
                        parts.Add($"{WorldMapCatalog.ZoneName(row)}: {DescribeZoneChange(map, layer, row, oldFingerprint)}");
                    }
                }

                parts.AddRange(old.Keys.Where(row => !zones.ContainsKey(row)).Select(row => $"{WorldMapCatalog.ZoneName(row)} gone"));
            }

            if (parts.Count > 0)
            {
                changes.Add($"{map.Name}: {string.Join(", ", parts.Distinct().Take(4))}{(parts.Distinct().Count() > 4 ? " and more" : "")}");
            }
        }

        foreach (var gone in index.Maps.Keys.Where(id => !maps.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))))
        {
            changes.Add($"{gone}: no longer in the game");
        }

        return changes;
    }

    /// <summary>"Desert Wolf added, Bear gone" or "weights changed", from the zone's old fingerprint and its contents now.</summary>
    private static string DescribeZoneChange(WorldMapInfo map, string layer, string row, string oldFingerprint)
    {
        var zone = map.Layers.Where(l => l.Kind.ToString() == layer).SelectMany(l => l.ZonesByColor.Values).FirstOrDefault(z => z.Row == row);
        if (zone is null)
        {
            return "changed";
        }

        var oldRows = oldFingerprint.Split('|').Where(p => !p.StartsWith("~q", StringComparison.Ordinal) && p.Contains(':')).Select(p => p[..Math.Max(0, p.LastIndexOf(':'))]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = zone.Entries.Where(e => !oldRows.Contains(e.Row)).Select(e => e.Name).Distinct().ToList();
        var removed = oldRows.Where(r => !zone.Entries.Any(e => string.Equals(e.Row, r, StringComparison.OrdinalIgnoreCase))).Select(WorldMapCatalog.Humanize).ToList();
        var words = added.Select(n => $"{n} added").Concat(removed.Select(n => $"{n} gone")).ToList();
        return words.Count > 0 ? string.Join(", ", words) : "weights changed";
    }

    /// <summary>Layer name → zone row → its contents' fingerprint (creatures and fish; biomes have no contents).</summary>
    private static Dictionary<string, Dictionary<string, string>> ZoneFingerprints(WorldMapInfo map) =>
        map.Layers.Where(l => l.Kind != WorldLayerKind.Biomes).ToDictionary(
            l => l.Kind.ToString(),
            l => l.ZonesByColor.Values.GroupBy(z => z.Row, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Fingerprint(), StringComparer.OrdinalIgnoreCase));

    private static string Count(int maps) => maps == 1 ? "1 map" : $"{maps} maps";

    /// <summary>The first three items, then "and N more".</summary>
    private static string Join(IReadOnlyList<string> items) =>
        items.Count <= 3 ? string.Join("; ", items) : string.Join("; ", items.Take(3)) + $"; and {items.Count - 3} more";

    /// <summary>
    /// A world id as a folder name. Plain row names stay as they are; anything else has its odd
    /// characters replaced and a short hash of the id added, so "A.B" and "A_B" (a mod's rows) never
    /// share a folder and delete each other's pictures.
    /// </summary>
    private static string Safe(string id)
    {
        var plain = string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        return plain == id && plain.Length > 0
            ? plain
            : plain + "_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToUpperInvariant())))[..8];
    }

    private IEnumerable<string> SafeDirectories()
    {
        try
        {
            return Directory.Exists(Root) ? Directory.GetDirectories(Root) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private WorldMapIndex? LoadIndex()
    {
        try
        {
            var path = Path.Combine(Root, IndexFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<WorldMapIndex>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Written whole or not at all: a half-written index would read as "never made" and redraw every map.</summary>
    private void SaveIndex(WorldMapIndex index) =>
        IcarusStarlink.Core.IO.AtomicFile.WriteAllText(Path.Combine(Root, IndexFile), JsonSerializer.Serialize(index, JsonOptions));

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>index.json: what each drawn world was made from.</summary>
public sealed class WorldMapIndex
{
    public Dictionary<string, WorldMapIndexEntry> Maps { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record WorldMapIndexEntry
{
    /// <summary>WorldMapRenderer.Stamp when it was drawn.</summary>
    public string Stamp { get; init; } = "";

    public WorldPictureSource Picture { get; init; }

    /// <summary>The zone images saved for it.</summary>
    public List<WorldLayerKind> Layers { get; init; } = [];

    /// <summary>Layer → zone row → contents fingerprint, when it was last reloaded.</summary>
    public Dictionary<string, Dictionary<string, string>> Zones { get; init; } = [];
}
