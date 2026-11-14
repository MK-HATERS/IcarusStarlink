using System.Text.Json;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.PakIO.World;

/// <param name="Summary">One line for the page: what changed ("Styx: map redrawn; Olympus, Conifer Hard: Desert Wolf added").</param>
/// <param name="Redrawn">How many maps were drawn again.</param>
/// <param name="Failed">Worlds that couldn't be drawn, with why ("Olympus (The pak is locked.)"); Reload tries them again.</param>
public sealed record WorldMapReloadResult(string Summary, int Redrawn, IReadOnlyList<WorldMapInfo> Maps, IReadOnlyList<string> Failed);

/// <param name="OutOfDate">The game's map files or zones differ from what the maps were made from (or they were never made).</param>
public sealed record WorldMapCheckResult(bool OutOfDate, string Summary);

/// <summary>
/// The drawn maps, kept in one folder (Cache\World): &lt;world id&gt;\picture.jpg and one
/// &lt;layer&gt;.png per zone image and features.json (caves, geysers, spots, water), plus index.json
/// with what each was made from (where the game keeps its files, and every zone's contents). One
/// copy only: a map that changed is drawn again and its old copy replaced only once the new one is
/// fully read; a world that fails to draw keeps its last drawing; a world the game dropped is
/// deleted. Reload redraws only what changed; Rebuild all redraws every map. Nothing is touched when
/// the game's files or its world list can't be read. For a world a Reload found changed, the index
/// also keeps what changed (zones, and spots that came or went) until it changes again.
/// </summary>
/// <param name="now">The time a change is dated with (UTC); the clock when left out.</param>
public sealed class WorldMapStore(string root, Func<IReadOnlyList<WorldMapInfo>> readCatalog, IWorldMapRenderer renderer, Func<DateTime>? now = null)
{
    private const string IndexFile = "index.json";
    private const string PictureFile = "picture.jpg";
    private const string NoWorldList = "the game's list of worlds (Data\\World\\D_WorldData.json) can't be read: refresh game data in Settings. Nothing was changed.";
    private const string GameUnreadable = "the game's files can't be read: check the game folder in Settings. Nothing was changed.";
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>How each world's picture was made (Picture, Relief, None); a world missing here hasn't been drawn.</summary>
    public IReadOnlyDictionary<string, WorldPictureSource> PictureSources() =>
        (LoadIndex()?.Maps ?? []).ToDictionary(kv => kv.Key, kv => kv.Value.Picture, StringComparer.OrdinalIgnoreCase);

    /// <summary>What changed on each world the last time a Reload found it changed; a world missing here has nothing to show.</summary>
    public IReadOnlyDictionary<string, WorldMapChangeRecord> ChangeRecords() =>
        (LoadIndex()?.Maps ?? []).Where(kv => kv.Value.Changed is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Changed!, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every world was drawn by a Reload that ran to the end (false: never made, or stopped half-way).</summary>
    public bool IsComplete() => LoadIndex() is { Complete: true, Maps.Count: > 0 };

    /// <summary>Compares the game's files and zones with what the maps were made from, without drawing anything.</summary>
    public WorldMapCheckResult Check() => Check(readCatalog());

    /// <summary>Check against worlds already read (ReadMaps just now), so the game's tables aren't read twice.</summary>
    public WorldMapCheckResult Check(IReadOnlyList<WorldMapInfo> maps)
    {
        ThrowIfUnreadable(maps);
        var index = LoadIndex();
        if (index is null)
        {
            return new WorldMapCheckResult(maps.Count > 0, maps.Count > 0 ? "The maps haven't been made yet." : "");
        }

        var stamps = maps.ToDictionary(m => m.Id, renderer.Stamp, StringComparer.OrdinalIgnoreCase);
        var changes = Changes(maps, index, stamps, Adopted(maps, index, stamps));
        var missing = maps.Where(m => !IsDrawn(m, index)).ToList();
        return changes.Count == 0 && missing.Count == 0
            ? new WorldMapCheckResult(false, "")
            : new WorldMapCheckResult(true, "The game's files changed since these maps were made: " + Join(changes.Count > 0 ? changes : [.. missing.Select(m => $"{m.Name}: not made yet")]) + ".");
    }

    /// <summary>
    /// Draws the maps whose files changed or aren't drawn (all of them when <paramref name="everything"/>),
    /// deletes worlds the game dropped, and says what changed. Each map's old copy is replaced only once
    /// its new one is fully read; a world that fails to draw keeps its last drawing (or is left out of
    /// the index when it had none) and is named in the summary, so the next Reload tries it again. The
    /// index is saved after each world, so a Reload stopped half-way (<paramref name="cancel"/>, checked
    /// between worlds, throws OperationCanceledException) is picked up where it left off.
    /// Throws, changing nothing, when the game's files or its world list can't be read.
    /// </summary>
    public WorldMapReloadResult Reload(bool everything, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var maps = readCatalog();
        ThrowIfUnreadable(maps);
        var previous = LoadIndex();
        var old = everything ? null : previous;
        Directory.CreateDirectory(Root);
        if (everything)
        {
            DeleteStrayFiles();
        }

        var stamps = maps.ToDictionary(m => m.Id, renderer.Stamp, StringComparer.OrdinalIgnoreCase);
        var adopted = previous is null ? [] : Adopted(maps, previous, stamps);
        // Worlds not reached yet keep their entries while this runs: if it's stopped half-way (Starlink
        // closed, the PC restarted), the next Reload picks up where it left off.
        var index = new WorldMapIndex { Complete = false };
        foreach (var (id, e) in previous?.Maps ?? [])
        {
            index.Maps[id] = e;
        }

        var redrawn = 0;
        var failed = new List<string>();
        foreach (var map in maps)
        {
            // Stop (Map page): between worlds only, so the one being drawn is finished and kept. The
            // index stays not Complete, and the next Reload draws the rest.
            cancel.ThrowIfCancellationRequested();
            var before = previous?.Maps.GetValueOrDefault(map.Id);
            var zoneChanges = before is null ? [] : ZoneChanges(map, before);
            var entry = old?.Maps.GetValueOrDefault(map.Id);
            // A stamp made the old way can't be compared with one made now: the world keeps its
            // drawing and takes the new stamp, instead of every map being drawn again once.
            if (entry is not null && (entry.Stamp == stamps[map.Id] || adopted.Contains(map.Id)) && IsDrawn(map, old!))
            {
                index.Maps[map.Id] = entry with
                {
                    Stamp = stamps[map.Id],
                    Zones = ZoneFingerprints(map),
                    Changed = zoneChanges.Count > 0 ? Record(zoneChanges, null, null) : entry.Changed,
                };
                continue;
            }

            // The game replaced this world's files (not: Rebuild all, a first drawing, a stamp made
            // the old way): its spots as drawn last time are kept to say which came and went.
            var filesChanged = before is not null && before.Stamp != stamps[map.Id] && !adopted.Contains(map.Id);
            var oldFeatures = filesChanged && IsDrawn(map, previous!) ? ReadFeatures(map.Id) : null;

            progress?.Report($"Drawing {map.Name} and reading its caves and spots ({redrawn + 1} of up to {maps.Count}; a big map takes about a minute)…");
            PendingDrawing drawing;
            try
            {
                drawing = Read(map);
            }
            catch (Exception ex)
            {
                // One world that can't be drawn (a mod's odd tiles, a locked pak) mustn't stop the others.
                // Nothing on disk was touched: a world drawn before keeps its last drawing. Its stamp is
                // cleared so the next Reload tries it again, even when the game's files didn't change
                // (Rebuild all); its old zone fingerprints stay, so a zone change is still said then.
                failed.Add($"{map.Name} ({ex.Message})");
                if (previous?.Maps.GetValueOrDefault(map.Id) is { } kept && IsDrawn(map, previous))
                {
                    index.Maps[map.Id] = kept with { Stamp = "" };
                }
                else
                {
                    index.Maps.Remove(map.Id);
                }

                SaveIndex(index);
                continue;
            }

            // The index never lists a world whose folder is being rewritten.
            index.Maps.Remove(map.Id);
            SaveIndex(index);
            try
            {
                entry = Write(map, drawing, stamps[map.Id]);
            }
            catch (Exception ex)
            {
                // Half-written: its folder goes and it stays out of the index, so the next Reload draws it again.
                Directories.TryDelete(Path.Combine(Root, Safe(map.Id)));
                failed.Add($"{map.Name} ({ex.Message})");
                SaveIndex(index);
                continue;
            }

            // What changed is kept until the world changes again (its files or its zones); drawing it
            // again with nothing changed (Rebuild all) keeps what was recorded.
            index.Maps[map.Id] = entry with
            {
                Zones = ZoneFingerprints(map),
                Changed = filesChanged || zoneChanges.Count > 0 ? Record(zoneChanges, oldFeatures, drawing.Features) : before?.Changed,
            };
            SaveIndex(index);
            redrawn++;
        }

        // Worlds the game no longer has: their folders and entries go.
        foreach (var folder in SafeDirectories())
        {
            if (!maps.Any(m => string.Equals(Safe(m.Id), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)))
            {
                Directories.TryDelete(folder);
            }
        }

        foreach (var id in index.Maps.Keys.Where(id => !maps.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            index.Maps.Remove(id);
        }

        index.Complete = true;
        SaveIndex(index);

        var changes = previous is null ? [] : Changes(maps, previous, stamps, adopted);
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

        return new WorldMapReloadResult(summary, redrawn, maps, failed);
    }

    /// <summary>
    /// The game always has worlds: none means its world list couldn't be read. And with its files
    /// unreadable every map would compare as changed and draw as nothing. Nothing is changed then.
    /// </summary>
    private void ThrowIfUnreadable(IReadOnlyList<WorldMapInfo> maps)
    {
        if (maps.Count == 0)
        {
            throw new IOException(NoWorldList);
        }

        if (!renderer.CanReadGame())
        {
            throw new IOException(GameUnreadable);
        }
    }

    /// <summary>Rebuild all: files left directly in the folder (a half-written index's temp file) go; the index stays until it's replaced.</summary>
    private void DeleteStrayFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Root))
            {
                if (!string.Equals(Path.GetFileName(file), IndexFile, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A world's new drawing, read from the game but not written yet.</summary>
    private sealed record PendingDrawing(
        byte[]? Jpeg, WorldPictureSource Source, IReadOnlyList<(WorldLayerKind Kind, byte[] Png)> Layers, WorldFeatures Features, string FeaturesJson);

    private PendingDrawing Read(WorldMapInfo map)
    {
        var (jpeg, source) = renderer.RenderPicture(map);
        var layers = new List<(WorldLayerKind, byte[])>();
        foreach (var layer in map.Layers)
        {
            if (layer.HeatmapAssetPath is { } path && renderer.ReadLayer(path) is { } pixels)
            {
                layers.Add((layer.Kind, pixels.ToPng()));
            }
        }

        var features = renderer.ReadFeatures(map);
        return new PendingDrawing(jpeg, source, layers, features, JsonSerializer.Serialize(features));
    }

    /// <summary>
    /// The scheme a stamp was made with: the text before its first colon ("c2:AB12…"); "" for the
    /// first scheme's bare hash, which also changed whenever the game repacked its paks.
    /// </summary>
    private static string Scheme(string stamp) => stamp.IndexOf(':') is var colon and > 0 ? stamp[..colon] : "";

    /// <summary>
    /// The drawn worlds whose stamp was made with another scheme than today's: the two can't be
    /// compared, so such a world isn't reported as changed, and Reload gives it today's stamp without
    /// drawing it. A world whose last drawing failed (its stamp was cleared) is never one of them.
    /// </summary>
    private HashSet<string> Adopted(IReadOnlyList<WorldMapInfo> maps, WorldMapIndex index, IReadOnlyDictionary<string, string> stamps) =>
        maps.Where(m => index.Maps.GetValueOrDefault(m.Id) is { Stamp.Length: > 0 } entry
                && Scheme(entry.Stamp) != Scheme(stamps[m.Id]) && IsDrawn(m, index))
            .Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>What changed on a world, to keep in the index; null when there's nothing to show.</summary>
    private WorldMapChangeRecord? Record(List<WorldZoneChange> zones, WorldFeatures? before, WorldFeatures? after)
    {
        var (added, removed) = before is null || after is null ? ([], []) : WorldMapChanges.Spots(before.Features, after.Features);
        return zones.Count == 0 && added.Count == 0 && removed.Count == 0
            ? null
            : new WorldMapChangeRecord
            {
                When = now?.Invoke() ?? DateTime.UtcNow,
                Zones = zones,
                Added = [.. added.Take(WorldMapChangeRecord.MaxSpots)],
                Removed = [.. removed.Take(WorldMapChangeRecord.MaxSpots)],
                AddedCount = added.Count,
                RemovedCount = removed.Count,
            };
    }

    private WorldMapIndexEntry Write(WorldMapInfo map, PendingDrawing drawing, string stamp)
    {
        var folder = Path.Combine(Root, Safe(map.Id));
        // The old copy goes only now, with the new one fully read (this also clears stray files in it).
        Directories.TryDelete(folder);
        Directory.CreateDirectory(folder);
        if (drawing.Jpeg is not null)
        {
            File.WriteAllBytes(Path.Combine(folder, PictureFile), drawing.Jpeg);
        }

        foreach (var (kind, png) in drawing.Layers)
        {
            File.WriteAllBytes(LayerPath(map.Id, kind), png);
        }

        File.WriteAllText(Path.Combine(folder, FeaturesFile), drawing.FeaturesJson);
        return new WorldMapIndexEntry { Stamp = stamp, Picture = drawing.Source, Layers = [.. drawing.Layers.Select(l => l.Kind)] };
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
    /// <param name="adopted">Worlds whose stamp was made the old way (Adopted): not said to be redrawn.</param>
    private static List<string> Changes(IReadOnlyList<WorldMapInfo> maps, WorldMapIndex index, IReadOnlyDictionary<string, string> stamps, HashSet<string> adopted)
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
            if (before.Stamp != stamps.GetValueOrDefault(map.Id) && !adopted.Contains(map.Id))
            {
                parts.Add("map redrawn");
            }

            parts.AddRange(ZoneChanges(map, before).Select(z => z.Summary));
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

    /// <summary>The creature and fishing zones that differ from what an index entry was made from: added, gone, or what changed in them.</summary>
    private static List<WorldZoneChange> ZoneChanges(WorldMapInfo map, WorldMapIndexEntry before)
    {
        var changes = new List<WorldZoneChange>();
        foreach (var (layer, zones) in ZoneFingerprints(map))
        {
            var old = before.Zones.GetValueOrDefault(layer) ?? [];
            foreach (var (row, fingerprint) in zones)
            {
                if (!old.TryGetValue(row, out var oldFingerprint))
                {
                    changes.Add(new WorldZoneChange(layer, row, WorldMapCatalog.ZoneName(row), "added"));
                }
                else if (oldFingerprint != fingerprint)
                {
                    changes.Add(new WorldZoneChange(layer, row, WorldMapCatalog.ZoneName(row), DescribeZoneChange(map, layer, row, oldFingerprint)));
                }
            }

            changes.AddRange(old.Keys.Where(row => !zones.ContainsKey(row)).Select(row => new WorldZoneChange(layer, row, WorldMapCatalog.ZoneName(row), "gone")));
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
        AtomicFile.WriteAllText(Path.Combine(Root, IndexFile), JsonSerializer.Serialize(index, JsonOptions));
}

/// <summary>index.json: what each drawn world was made from.</summary>
public sealed class WorldMapIndex
{
    public Dictionary<string, WorldMapIndexEntry> Maps { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False while a Reload is still drawing (the index is saved after each world); an index left that way was stopped half-way.</summary>
    public bool Complete { get; set; } = true;
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

    /// <summary>What changed the last time a Reload found this world changed; null when nothing is recorded.</summary>
    public WorldMapChangeRecord? Changed { get; init; }
}
