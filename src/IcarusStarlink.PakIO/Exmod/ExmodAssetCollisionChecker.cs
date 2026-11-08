using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// The binary-asset counterpart to MergeEngine's own field-conflict detection: two queued mods
/// bundling the same game file (e.g. both ship "BP/Building/BP_Wall.uasset") resolve
/// last-mod-wins in RebuildService with no warning of their own. Unlike a field conflict, there's
/// no "pick a winner" resolution here (a binary asset has no per-file manual pick); the value is in
/// showing the collision at all.
///
/// One game file is several parts on disk (.uasset, .uexp, .ubulk …), so the parts are grouped
/// into one file, named by its path without the extension, as ExmodChangesFormatter.ReplacedGameFiles
/// shows it. Byte-identical collisions (every mod ships the same parts with the same bytes) are told
/// apart from different ones: the former is harmless duplication, the latter means one mod's own
/// file silently overwrites another's.
///
/// Only real compiled Unreal assets (GameAssetExtensions.IsRealGameAsset) are considered: a mod's
/// own Assets list can also hold a Readme.txt or an "ImageOnly.png" thumbnail the Library reads for
/// display, which RebuildService.StageFilesInQueueOrder doesn't pack into the merged pak, so a
/// "collision" on one would report a problem that can't happen (several unrelated real mods share
/// exactly these generic file names).
/// </summary>
public static class ExmodAssetCollisionChecker
{
    /// <param name="RelativePath">The game file without its extension, '/'-separated: "BP/Building/BP_Wall".</param>
    /// <param name="ModNames">The mods shipping it, in queue order.</param>
    /// <param name="AreIdentical">Every mod ships the same parts, each byte-identical to the first mod's.</param>
    public sealed record AssetCollision(string RelativePath, IReadOnlyList<string> ModNames, bool AreIdentical);

    public static IReadOnlyList<AssetCollision> Check(IReadOnlyList<(string ModName, ExmodPackageContents Package)> queuedMods)
    {
        // Game file → each mod's parts (extension → bytes), in queue order.
        var byFile = new Dictionary<string, List<(int Queued, string ModName, Dictionary<string, byte[]> Parts)>>(StringComparer.OrdinalIgnoreCase);
        for (var queued = 0; queued < queuedMods.Count; queued++)
        {
            var (modName, package) = queuedMods[queued];
            foreach (var asset in package.Assets)
            {
                if (!GameAssetExtensions.IsRealGameAsset(asset.RelativePath))
                {
                    continue;
                }

                var path = asset.RelativePath.Replace('\\', '/');
                var dot = path.LastIndexOf('.');
                var gameFile = path[..dot];
                var extension = path[dot..];

                if (!byFile.TryGetValue(gameFile, out var mods))
                {
                    mods = [];
                    byFile[gameFile] = mods;
                }

                // This mod's entry is the last one (mods are read in queue order); a part it ships twice keeps its last copy.
                if (mods.Count == 0 || mods[^1].Queued != queued)
                {
                    mods.Add((queued, modName, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)));
                }

                mods[^1].Parts[extension] = asset.Content;
            }
        }

        var collisions = new List<AssetCollision>();
        foreach (var (gameFile, mods) in byFile)
        {
            if (mods.Count < 2)
            {
                continue;
            }

            var first = mods[0].Parts;
            var areIdentical = mods.Skip(1).All(m =>
                m.Parts.Count == first.Count
                && m.Parts.All(p => first.TryGetValue(p.Key, out var bytes) && p.Value.AsSpan().SequenceEqual(bytes)));
            collisions.Add(new AssetCollision(gameFile, [.. mods.Select(m => m.ModName)], areIdentical));
        }

        return collisions;
    }
}
