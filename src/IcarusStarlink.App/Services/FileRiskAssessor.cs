using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Rates the game files every Library mod replaces (ReplacedFileRiskRater) and finds mods
/// replacing the same game file with different copies. A file the game doesn't have is the mod's
/// own and never counts. Reads folders and the game's mounted content: run it off the UI thread.
/// </summary>
public static class FileRiskAssessor
{
    public static Dictionary<string, ModFileRisk> Assess(ILibraryRepository repository, IReadOnlyList<(string Folder, string Name)> mods, IBaseGameContentProvider game)
    {
        // Each mod's copies of game files (its own new files left out); unknown when the game can't be read.
        var replaced = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, List<(string Folder, string Name, string File)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, name) in mods)
        {
            IReadOnlyList<string> assets;
            try
            {
                assets = repository.ListAssetPaths(folder);
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
            var hashes = owners.Select(o =>
            {
                try
                {
                    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(repository.ReadAssetContent(o.Folder, o.File)));
                }
                catch (Exception)
                {
                    return o.Folder;
                }
            }).ToList();
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

        var result = new Dictionary<string, ModFileRisk>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, paths) in replaced)
        {
            if (ReplacedFileRiskRater.Assess(paths, clashes.GetValueOrDefault(folder) ?? []) is { } risk)
            {
                result[folder] = risk;
            }
        }

        return result;
    }
}
