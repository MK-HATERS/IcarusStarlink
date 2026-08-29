using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// The binary-asset counterpart to MergeEngine's own field-conflict detection: today, two queued
/// mods bundling a binary asset at the same relative path (e.g. both ship
/// "BP/Building/BP_Wall.uasset") silently resolve last-mod-wins in RebuildService, with zero
/// warning — the same silent-collision problem the Conflict Picker already solves for JSON
/// DataTable fields, just never extended to binary content. Unlike a field conflict, there's no
/// "pick a winner" resolution modeled here (a binary asset has no per-file manual-pick mechanism,
/// and building one would be real scope creep) — the value is purely in SURFACING the collision,
/// which this app couldn't tell you about at all before now.
///
/// Byte-identical collisions (two mods both bundling the same shared texture/mesh unmodified) are
/// reported separately from genuinely different ones — the former is harmless duplication, the
/// latter means one mod's own asset silently overwrites another's.
///
/// Only real compiled Unreal assets (GameAssetExtensions.IsRealGameAsset) are considered — a mod's
/// own Assets list can legitimately also contain a Readme.txt or an "ImageOnly.png" thumbnail this
/// app's own Library reads for display, which RebuildService.StageAssets no longer even packs into
/// the merged pak, so a "collision" on one would report a problem that can no longer actually
/// happen (confirmed live: several unrelated real mods share exactly these kinds of generic
/// filenames, which is what surfaced this gap in StageAssets in the first place).
/// </summary>
public static class ExmodAssetCollisionChecker
{
    public sealed record AssetCollision(string RelativePath, IReadOnlyList<string> ModNames, bool AreIdentical);

    public static IReadOnlyList<AssetCollision> Check(IReadOnlyList<(string ModName, ExmodPackageContents Package)> queuedMods)
    {
        var byPath = new Dictionary<string, List<(string ModName, byte[] Content)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (modName, package) in queuedMods)
        {
            foreach (var asset in package.Assets)
            {
                if (!GameAssetExtensions.IsRealGameAsset(asset.RelativePath))
                {
                    continue;
                }

                if (!byPath.TryGetValue(asset.RelativePath, out var entries))
                {
                    entries = [];
                    byPath[asset.RelativePath] = entries;
                }

                entries.Add((modName, asset.Content));
            }
        }

        var collisions = new List<AssetCollision>();
        foreach (var (relativePath, entries) in byPath)
        {
            if (entries.Count < 2)
            {
                continue;
            }

            var firstContent = entries[0].Content;
            var areIdentical = entries.Skip(1).All(e => e.Content.AsSpan().SequenceEqual(firstContent));
            collisions.Add(new AssetCollision(relativePath, entries.Select(e => e.ModName).ToList(), areIdentical));
        }

        return collisions;
    }
}
