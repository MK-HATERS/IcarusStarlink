using System.Collections.Concurrent;
using System.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Rates the game files every Library mod replaces (ReplacedFileRiskRater) and finds mods
/// replacing the same game file with different copies. A file the game doesn't have is the mod's
/// own and never counts. A prebuilt pak that was never converted is read in place (ModPakContents).
/// Reads folders and the game's mounted content: run it off the UI thread.
///
/// Each mod copy's blueprint summary and hash is kept per (folder, file, last write time), so the
/// pass after a LibraryChangedMessage only reads what actually changed on disk.
/// </summary>
public static class FileRiskAssessor
{
    /// <summary>Past this many entries the caches start over (stale stamps from edited mods otherwise pile up).</summary>
    private const int MaxCacheEntries = 4000;

    private static readonly ConcurrentDictionary<(string Folder, string File, DateTime Stamp), BlueprintSummary?> SummaryCache = new();
    private static readonly ConcurrentDictionary<(string Folder, string File, DateTime Stamp), string> HashCache = new();
    private static readonly ConcurrentDictionary<(string Folder, DateTime Stamp), IReadOnlyList<string>> PakListingCache = new();

    /// <summary>One mod's files: its EXMOD folder's assets, or its paks' contents (opened only when something isn't cached).</summary>
    private sealed class ModFiles(string folder, string folderPath, bool isPak, ILibraryRepository repository) : IDisposable
    {
        private ModPakContents? _paks;
        private bool _paksOpened;

        /// <summary>A pak mod's paks' newest write time (every file in it changes with them).</summary>
        private readonly DateTime _pakStamp = isPak ? NewestPakWrite(folderPath) : default;

        public string Folder => folder;

        public IReadOnlyList<string> ListAssets()
        {
            if (!isPak)
            {
                return repository.ListAssetPaths(folder);
            }

            var key = (folder.ToUpperInvariant(), _pakStamp);
            if (PakListingCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var listed = Paks()?.ContentPaths ?? [];
            Remember(PakListingCache, key, listed);
            return listed;
        }

        public DateTime Stamp(string file)
        {
            if (isPak)
            {
                return _pakStamp;
            }

            try
            {
                return File.GetLastWriteTimeUtc(Path.Combine(folderPath, file));
            }
            catch (Exception)
            {
                return default;
            }
        }

        public byte[] Read(string file) =>
            isPak ? Paks()?.TryRead(file) ?? throw new IOException($"'{file}' couldn't be read from the pak.") : repository.ReadAssetContent(folder, file);

        public BlueprintSummary? Summarize(string file) =>
            isPak ? Paks()?.TrySummarizeBlueprint(file) : ModBlueprintReader.TrySummarize(folderPath, file);

        private ModPakContents? Paks()
        {
            if (!_paksOpened)
            {
                _paksOpened = true;
                _paks = ModPakContents.TryOpen(folderPath);
            }

            return _paks;
        }

        public void Dispose() => _paks?.Dispose();

        private static DateTime NewestPakWrite(string path)
        {
            try
            {
                return Directory.GetFiles(path, "*.pak", SearchOption.TopDirectoryOnly).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
            }
            catch (Exception)
            {
                return default;
            }
        }
    }

    public static Dictionary<string, ModFileRisk> Assess(ILibraryRepository repository, IReadOnlyList<(string Folder, string Name)> mods, IBaseGameContentProvider game)
    {
        var entries = repository.GetAll().ToDictionary(e => e.FolderName, StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, ModFiles>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return Assess(repository, mods, game, entries, sources);
        }
        finally
        {
            foreach (var source in sources.Values)
            {
                source.Dispose();
            }
        }
    }

    private static Dictionary<string, ModFileRisk> Assess(
        ILibraryRepository repository, IReadOnlyList<(string Folder, string Name)> mods, IBaseGameContentProvider game,
        Dictionary<string, LibraryEntry> entries, Dictionary<string, ModFiles> sources)
    {
        // Each mod's copies of game files (its own new files left out); unknown when the game can't be read.
        var replaced = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, List<(string Folder, string Name, string File)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, name) in mods)
        {
            IReadOnlyList<string> assets;
            try
            {
                var source = new ModFiles(folder, repository.GetFolderPath(folder), entries.GetValueOrDefault(folder)?.IsOpaquePak == true, repository);
                sources[folder] = source;
                assets = source.ListAssets();
            }
            catch (Exception)
            {
                continue;
            }

            var mine = new List<string>();
            foreach (var path in ExmodChangesFormatter.ReplacedGameFiles(assets))
            {
                // The .uasset is the part to compare (its .uexp/.ubulk follow it).
                var file = assets.FirstOrDefault(a => string.Equals(a.Replace('\\', '/'), path + ".uasset", StringComparison.OrdinalIgnoreCase))
                    ?? assets.First(a => a.Replace('\\', '/').StartsWith(path + ".", StringComparison.OrdinalIgnoreCase));
                if (game.HasAsset(path + ".uasset") is not true && game.HasAsset(path + ".umap") is not true)
                {
                    continue;
                }

                mine.Add(path);
                if (!byPath.TryGetValue(path, out var owners))
                {
                    byPath[path] = owners = [];
                }

                owners.Add((folder, name, file));
            }

            replaced[folder] = mine;
        }

        // The same game file from two mods: a clash only when the copies differ (two mods shipping
        // the same unchanged texture is just duplication).
        var clashes = new Dictionary<string, List<FileClash>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, owners) in byPath.Where(kv => kv.Value.Count > 1))
        {
            var hashes = owners.Select(o => Hash(sources[o.Folder], o.File)).ToList();
            if (hashes.Distinct(StringComparer.Ordinal).Count() == 1)
            {
                continue;
            }

            for (var i = 0; i < owners.Count; i++)
            {
                var others = owners.Where((_, j) => j != i && hashes[j] != hashes[i]).Select(o => o.Name).Distinct().ToList();
                if (others.Count == 0)
                {
                    continue;
                }

                if (!clashes.TryGetValue(owners[i].Folder, out var list))
                {
                    clashes[owners[i].Folder] = list = [];
                }

                list.Add(new FileClash(path, others));
            }
        }

        // A replaced blueprint or screen is compared with the game's own copy: what the mod changes,
        // and whether the game's copy has things the mod's lacks (proof it's out of date). The
        // game's side is read once per pass, however many mods replace it.
        var gameSummaries = new Dictionary<string, BlueprintSummary?>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ModFileRisk>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, paths) in replaced)
        {
            var withDifferences = paths.Select(path =>
            {
                if (ReplacedFileRiskRater.Rate(path).Risk < FileRisk.Medium)
                {
                    return (path, (BlueprintDifference?)null);
                }

                if (!gameSummaries.TryGetValue(path, out var gameCopy))
                {
                    gameSummaries[path] = gameCopy = game.TrySummarizeBlueprint(path + ".uasset");
                }

                var modCopy = gameCopy is null ? null : Summarize(sources[folder], path + ".uasset");
                return (path, gameCopy is not null && modCopy is not null ? BlueprintComparer.Compare(gameCopy, modCopy) : null);
            }).ToList();

            if (ReplacedFileRiskRater.AssessCompared(withDifferences, clashes.GetValueOrDefault(folder) ?? []) is { } risk)
            {
                result[folder] = risk;
            }
        }

        return result;
    }

    private static BlueprintSummary? Summarize(ModFiles source, string file)
    {
        var key = (source.Folder.ToUpperInvariant(), file.Replace('\\', '/').ToUpperInvariant(), source.Stamp(file));
        if (SummaryCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var summary = source.Summarize(file);
        Remember(SummaryCache, key, summary);
        return summary;
    }

    private static string Hash(ModFiles source, string file)
    {
        var key = (source.Folder.ToUpperInvariant(), file.Replace('\\', '/').ToUpperInvariant(), source.Stamp(file));
        if (HashCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source.Read(file)));
            Remember(HashCache, key, hash);
            return hash;
        }
        catch (Exception)
        {
            // Unreadable: counts as its own copy (not cached, so it's read again next time).
            return source.Folder;
        }
    }

    private static void Remember<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, TKey key, TValue value) where TKey : notnull
    {
        if (cache.Count >= MaxCacheEntries)
        {
            cache.Clear();
        }

        cache[key] = value;
    }
}
