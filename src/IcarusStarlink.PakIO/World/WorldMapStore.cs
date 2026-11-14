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
/// also keeps what changed (where its picture and zone shapes differ, which game files changed,
/// zones, and spots that came or went) until it changes again; a world the game added is kept as
/// "new". The index holds a short stamp per game file each world is drawn from, so the next change
/// can name the files; Adopt fills those in (and replaces stamps made the old way) without drawing.
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
        changes.AddRange(Dropped(maps, index).Select(name => $"{name}: no longer in the game"));
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
        // One writer of the index at a time: Adopt (in the background) steps aside while this runs.
        lock (_gate)
        {
            return ReloadLocked(everything, progress, cancel);
        }
    }

    /// <summary>Held by whoever is writing the index (Reload; Adopt only when it's free).</summary>
    private readonly object _gate = new();

    /// <summary>What Adopt did.</summary>
    public enum AdoptResult
    {
        /// <summary>Every drawn world already has today's stamp and its files' stamps (or the maps aren't all made).</summary>
        NotNeeded,

        /// <summary>A Reload is running: it does the same itself.</summary>
        Busy,

        /// <summary>The game's files or its world list can't be read now. Nothing was changed.</summary>
        Unreadable,

        /// <summary>Stamps were written.</summary>
        Adopted,
    }

    /// <summary>
    /// Whether Adopt has anything to do: the maps are all made and a drawn world has no stamps for
    /// its files yet (it was drawn before they were kept, or its stamp was made the old way). Reads
    /// only the index, never the game.
    /// </summary>
    public bool NeedsAdoption() => LoadIndex() is { Complete: true } index && index.Maps.Values.Any(e => e.Stamp.Length > 0 && e.Files is null);

    /// <summary>
    /// Without drawing anything: a drawn world whose stamp was made the old way takes today's stamp
    /// (the two can't be compared; Reload does the same), and a drawn world whose files are as they
    /// were when it was drawn gets the stamps of its files. Done ahead of the next game update, so
    /// that update is compared file by file with the game as it is now. A world whose files already
    /// changed is left for Reload. Steps aside (Busy) while a Reload runs.
    /// </summary>
    public AdoptResult Adopt()
    {
        if (!NeedsAdoption())
        {
            return AdoptResult.NotNeeded;
        }

        if (!Monitor.TryEnter(_gate))
        {
            return AdoptResult.Busy;
        }

        try
        {
            if (LoadIndex() is not { Complete: true } index)
            {
                return AdoptResult.NotNeeded;
            }

            var maps = readCatalog();
            if (maps.Count == 0 || !renderer.CanReadGame())
            {
                return AdoptResult.Unreadable;
            }

            var stamps = maps.ToDictionary(m => m.Id, renderer.Stamp, StringComparer.OrdinalIgnoreCase);
            var adopted = Adopted(maps, index, stamps);
            var changed = false;
            foreach (var map in maps)
            {
                if (index.Maps.GetValueOrDefault(map.Id) is not { Stamp.Length: > 0 } entry || !IsDrawn(map, index))
                {
                    continue;
                }

                if (adopted.Contains(map.Id) || (entry.Stamp == stamps[map.Id] && entry.Files is null))
                {
                    index.Maps[map.Id] = entry with { Stamp = stamps[map.Id], Name = map.Name, Files = FileStamps(map) };
                    changed = true;
                }
            }

            if (changed)
            {
                SaveIndex(index);
            }

            return changed ? AdoptResult.Adopted : AdoptResult.NotNeeded;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>
    /// Adopt, a while after Starlink starts and off the caller's thread; while the game's files can't
    /// be read it's tried again (<paramref name="tries"/> times in all). Never throws but for
    /// <paramref name="cancel"/>.
    /// </summary>
    public async Task<AdoptResult> AdoptWhenReadableAsync(TimeSpan delay, TimeSpan retryAfter, int tries = 3, CancellationToken cancel = default)
    {
        var result = AdoptResult.NotNeeded;
        for (var attempt = 0; attempt < tries; attempt++)
        {
            await Task.Delay(attempt == 0 ? delay : retryAfter, cancel).ConfigureAwait(false);
            try
            {
                result = await Task.Run(Adopt, cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A locked pak, a full disk: nothing was replaced (the index is written whole or not at all).
                result = AdoptResult.Unreadable;
            }

            if (result != AdoptResult.Unreadable)
            {
                break;
            }
        }

        return result;
    }

    private WorldMapReloadResult ReloadLocked(bool everything, IProgress<string>? progress, CancellationToken cancel)
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
        var index = new WorldMapIndex { Complete = false, Worlds = previous?.Worlds };
        foreach (var (id, e) in previous?.Maps ?? [])
        {
            index.Maps[id] = e;
        }

        // The worlds the game had at the last Reload: one that isn't among them is new. (An index
        // from before they were listed: the worlds it drew, once it was finished.)
        var known = previous?.Worlds ?? (previous is { Complete: true } ? [.. previous.Maps.Keys] : null);
        bool IsNew(WorldMapInfo map) => known is not null && !known.Contains(map.Id, StringComparer.OrdinalIgnoreCase);
        var newAndFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    Name = map.Name,
                    Zones = ZoneFingerprints(map),
                    // Its files are as drawn (or taken as they are): their stamps are kept for next time.
                    Files = FileStamps(map),
                    Changed = zoneChanges.Count > 0 ? Record(zoneChanges) : entry.Changed,
                };
                continue;
            }

            // The game replaced this world's files (not: Rebuild all, a first drawing, a stamp made
            // the old way): its spots, picture and zone images as drawn last time are compared with
            // the new ones, to say what changed.
            var filesChanged = before is not null && before.Stamp != stamps[map.Id] && !adopted.Contains(map.Id);
            var compare = filesChanged && IsDrawn(map, previous!);
            var oldFeatures = compare ? ReadFeatures(map.Id) : null;
            var files = FileStamps(map);

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
                    if (IsNew(map))
                    {
                        newAndFailed.Add(map.Id);
                    }
                }

                SaveIndex(index);
                continue;
            }

            // Before the old copy goes: where the new picture and zone images differ from it.
            var (picture, shapes) = compare ? CompareImages(map, before!, drawing) : (null, []);

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
                if (IsNew(map))
                {
                    newAndFailed.Add(map.Id);
                }

                SaveIndex(index);
                continue;
            }

            // What changed is kept until the world changes again (its files or its zones); drawing it
            // again with nothing changed (Rebuild all) keeps what was recorded. A world whose last
            // drawing failed has no stamp to tell whether its files changed: it gets a record only
            // when something is found to differ.
            WorldMapChangeRecord? record = null;
            if (IsNew(map))
            {
                record = new WorldMapChangeRecord { When = now?.Invoke() ?? DateTime.UtcNow, NewMap = true };
            }
            else if (filesChanged || zoneChanges.Count > 0)
            {
                record = Record(zoneChanges, filesChanged && before!.Stamp.Length > 0, oldFeatures, drawing.Features,
                    picture, shapes, filesChanged ? FileChanges(before!.Files, files) : []);
            }

            index.Maps[map.Id] = entry with
            {
                Name = map.Name,
                Zones = ZoneFingerprints(map),
                Files = files,
                Changed = record ?? before?.Changed,
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

        // The worlds the game has now, for the next Reload to tell a new one by. A new world that
        // couldn't be drawn isn't listed, so it's still new when it is drawn.
        index.Worlds = [.. maps.Select(m => m.Id).Where(id => !newAndFailed.Contains(id))];
        index.Complete = true;
        SaveIndex(index);

        var changes = previous is null ? [] : Changes(maps, previous, stamps, adopted);
        var dropped = previous is null ? [] : Dropped(maps, previous);
        var gone = dropped.Count > 0 ? $"No longer in the game: {string.Join(", ", dropped)}." : null;
        string summary;
        if (previous is null)
        {
            summary = $"Made {Count(maps.Count)}.";
        }
        else if (everything)
        {
            summary = $"Rebuilt all {Count(maps.Count)}." + (changes.Count > 0 ? " Changed since last time: " + Join(changes) + "." : gone is null ? " Nothing had changed." : "");
        }
        else if (changes.Count > 0)
        {
            summary = "Changed: " + Join(changes) + "." + (maps.Count - changes.Count > 0 ? $" {Count(maps.Count - changes.Count)} unchanged." : "");
        }
        else if (gone is not null)
        {
            summary = maps.Count == 1 ? "The map that's left is up to date." : $"The {Count(maps.Count)} that are left are up to date.";
        }
        else
        {
            summary = maps.Count == 1 ? "Nothing changed: the map is up to date." : $"Nothing changed: all {Count(maps.Count)} are up to date.";
        }

        // Said on its own, so a dropped world is always named (the list above stops at three).
        if (gone is not null)
        {
            summary = everything || changes.Count > 0 ? $"{summary} {gone}" : $"{gone} {summary}";
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
        byte[]? Jpeg, WorldPictureSource Source, IReadOnlyList<(WorldLayerKind Kind, byte[] Png, WorldLayerPixels Pixels)> Layers, WorldFeatures Features, string FeaturesJson);

    private PendingDrawing Read(WorldMapInfo map)
    {
        var (jpeg, source) = renderer.RenderPicture(map);
        var layers = new List<(WorldLayerKind, byte[], WorldLayerPixels)>();
        foreach (var layer in map.Layers)
        {
            if (layer.HeatmapAssetPath is { } path && renderer.ReadLayer(path) is { } pixels)
            {
                layers.Add((layer.Kind, pixels.ToPng(), pixels));
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

    /// <summary>
    /// What changed on a world, to keep in the index. Made whenever the game replaced the world's
    /// files (<paramref name="filesChanged"/>), even with nothing found to differ; otherwise null
    /// when there's nothing to show.
    /// </summary>
    private WorldMapChangeRecord? Record(
        List<WorldZoneChange> zones, bool filesChanged = false, WorldFeatures? before = null, WorldFeatures? after = null,
        WorldImageChange? picture = null, List<WorldImageChange>? shapes = null, List<WorldFileChange>? files = null)
    {
        var (added, removed) = before is null || after is null ? ([], []) : WorldMapChanges.Spots(before.Features, after.Features);
        var found = picture is not null || shapes is { Count: > 0 } || files is { Count: > 0 } || added.Count > 0 || removed.Count > 0;
        return !filesChanged && !found && zones.Count == 0
            ? null
            : new WorldMapChangeRecord
            {
                When = now?.Invoke() ?? DateTime.UtcNow,
                FilesChanged = filesChanged || found,
                Picture = picture,
                Shapes = shapes ?? [],
                Files = files ?? [],
                Zones = zones,
                Added = [.. added.Take(WorldMapChangeRecord.MaxSpots)],
                Removed = [.. removed.Take(WorldMapChangeRecord.MaxSpots)],
                AddedCount = added.Count,
                RemovedCount = removed.Count,
            };
    }

    /// <summary>
    /// Where a world's new picture and zone images differ from the ones drawn last time (still on
    /// disk), by the cells of its in-game grid. An image that can't be read isn't compared, except
    /// the picture: without the old one the whole picture counts as changed.
    /// </summary>
    private (WorldImageChange? Picture, List<WorldImageChange> Shapes) CompareImages(WorldMapInfo map, WorldMapIndexEntry before, PendingDrawing drawing)
    {
        var (columns, rows) = map.GridSize;
        WorldImageChange? picture;
        if (before.Picture == WorldPictureSource.Relief && drawing.Source == WorldPictureSource.Picture)
        {
            // Drawn from height data until now: the game got its own picture for this world.
            picture = new WorldImageChange { What = "Picture", Whole = true, Note = "real" };
        }
        else
        {
            var old = before.Picture == WorldPictureSource.None ? null : TryReadBytes(PicturePath(map.Id));
            picture = before.Picture != WorldPictureSource.None && old is null && drawing.Jpeg is not null
                ? new WorldImageChange { What = "Picture", Whole = true }
                : WorldImageCompare.Pictures(old, drawing.Jpeg, columns, rows);
        }

        var shapes = new List<WorldImageChange>();
        foreach (var kind in Enum.GetValues<WorldLayerKind>())
        {
            var had = before.Layers.Contains(kind);
            var old = had ? ReadLayer(map.Id, kind) : null;
            if (had && old is null)
            {
                continue;
            }

            var drawn = drawing.Layers.Where(l => l.Kind == kind).Select(l => l.Pixels).FirstOrDefault();
            if (WorldImageCompare.Layers(kind.ToString(), old, drawn, columns, rows) is { } change)
            {
                shapes.Add(change);
            }
        }

        return (picture, shapes);
    }

    private static byte[]? TryReadBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The kinds of game file a map is drawn from, in the order they're listed.</summary>
    public static readonly IReadOnlyList<string> FileKinds = ["Picture tiles", "Height tiles", "Zone images", "Level files"];

    /// <summary>
    /// Kind → file's short name → a short stamp of its contents, for every file the map is drawn from
    /// that the game has (a file the game lists but doesn't have yet is left out, so it's "new" when
    /// it comes). The short name is the file's name; its path (without the extension) only when two
    /// files of a kind share a name.
    /// </summary>
    private Dictionary<string, Dictionary<string, string>> FileStamps(WorldMapInfo map)
    {
        var files = new Dictionary<string, Dictionary<string, string>>();
        Add(FileKinds[0], map.PictureTiles.Select(t => t.AssetPath));
        Add(FileKinds[1], map.HeightTiles.Select(t => t.AssetPath));
        Add(FileKinds[2], map.Layers.Select(l => l.HeatmapAssetPath).OfType<string>());
        Add(FileKinds[3], map.Levels);
        return files;

        void Add(string kind, IEnumerable<string> paths)
        {
            var listed = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shared = listed.GroupBy(ShortName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stamped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in listed)
            {
                if (renderer.FileStamp(path) is { } stamp)
                {
                    var name = ShortName(path);
                    stamped[shared.Contains(name) ? Path.ChangeExtension(path, null).Replace('\\', '/') : name] =
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stamp)))[..12];
                }
            }

            if (stamped.Count > 0)
            {
                files[kind] = stamped;
            }
        }

        static string ShortName(string path) => Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// The files that changed, came or went between two FileStamps, by kind; empty when the files
    /// weren't known before (a world drawn before they were kept).
    /// </summary>
    internal static List<WorldFileChange> FileChanges(
        Dictionary<string, Dictionary<string, string>>? before, Dictionary<string, Dictionary<string, string>> after)
    {
        var changes = new List<WorldFileChange>();
        if (before is null)
        {
            return changes;
        }

        foreach (var kind in FileKinds.Concat(before.Keys).Concat(after.Keys).Distinct())
        {
            var old = before.GetValueOrDefault(kind) ?? [];
            var now = after.GetValueOrDefault(kind) ?? [];
            var changed = now.Where(f => old.TryGetValue(f.Key, out var stamp) && stamp != f.Value).Select(f => f.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var added = now.Keys.Where(name => !old.ContainsKey(name)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var went = old.Keys.Where(name => !now.ContainsKey(name)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (changed.Count + added.Count + went.Count > 0)
            {
                changes.Add(new WorldFileChange
                {
                    Kind = kind,
                    Changed = [.. changed.Take(WorldFileChange.MaxNames)],
                    Added = [.. added.Take(WorldFileChange.MaxNames)],
                    Gone = [.. went.Take(WorldFileChange.MaxNames)],
                    ChangedCount = changed.Count,
                    AddedCount = added.Count,
                    GoneCount = went.Count,
                });
            }
        }

        return changes;
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

        foreach (var (kind, png, _) in drawing.Layers)
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

        return changes;
    }

    /// <summary>The worlds an index has that the game no longer does, by name (by id when the index is from before names were kept).</summary>
    private static List<string> Dropped(IReadOnlyList<WorldMapInfo> maps, WorldMapIndex index) =>
        [.. index.Maps.Where(kv => !maps.Any(m => string.Equals(m.Id, kv.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Value.Name is { Length: > 0 } name ? name : kv.Key)];

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

    /// <summary>
    /// The worlds the game had when a Reload last ran to the end (a new one that couldn't be drawn
    /// left out): a world missing here is new. Null in an index from before they were listed.
    /// </summary>
    public List<string>? Worlds { get; set; }
}

public sealed record WorldMapIndexEntry
{
    /// <summary>WorldMapRenderer.Stamp when it was drawn.</summary>
    public string Stamp { get; init; } = "";

    /// <summary>The name players see, to name the world by if the game drops it; null in an older index.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Kind ("Picture tiles", "Height tiles", "Zone images", "Level files") → file's short name → a
    /// short stamp of its contents, as they were when Stamp was taken; null when they weren't kept
    /// yet (Adopt fills them in).
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? Files { get; init; }

    public WorldPictureSource Picture { get; init; }

    /// <summary>The zone images saved for it.</summary>
    public List<WorldLayerKind> Layers { get; init; } = [];

    /// <summary>Layer → zone row → contents fingerprint, when it was last reloaded.</summary>
    public Dictionary<string, Dictionary<string, string>> Zones { get; init; } = [];

    /// <summary>What changed the last time a Reload found this world changed; null when nothing is recorded.</summary>
    public WorldMapChangeRecord? Changed { get; init; }
}
