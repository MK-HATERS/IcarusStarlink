using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Compare;

/// <summary>
/// "What did the author change in this update?" — diffs two copies of the same mod. An EXMOD mod
/// is compared straight from its own package (its Rows already ARE the author's changes, so
/// diffing two versions of them is a direct answer, no game data or UnrealPak needed); an opaque
/// .pak mod has no readable package, so it falls through to the full pak-vs-pak comparison, one
/// pair per pak when the mod is a bundle of several.
/// </summary>
public sealed class ModVersionComparer(IPakCompareService pakCompareService) : IModVersionComparer
{
    public async Task<ModVersionCompareResult> CompareAsync(
        string oldFolderPath, string newFolderPath, string? unrealPakExePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(oldFolderPath))
        {
            throw new DirectoryNotFoundException($"'{oldFolderPath}' doesn't exist — there's no previous version to compare against.");
        }

        if (!Directory.Exists(newFolderPath))
        {
            throw new DirectoryNotFoundException($"'{newFolderPath}' doesn't exist.");
        }

        var oldPaks = FindPaks(oldFolderPath);
        var newPaks = FindPaks(newFolderPath);

        // Both sides opaque paks: no package to read, so unpack and compare their real contents.
        if (oldPaks.Count > 0 && newPaks.Count > 0 && !HasExmod(oldFolderPath) && !HasExmod(newFolderPath))
        {
            if (string.IsNullOrWhiteSpace(unrealPakExePath))
            {
                throw new InvalidOperationException(
                    "This mod is a prebuilt .pak, so comparing versions needs UnrealPak.exe — set its path in Settings first.");
            }

            if (oldPaks.Count == 1 && newPaks.Count == 1)
            {
                // One pak each: compared directly, even when the author renamed it.
                var oldPak = Path.Combine(oldFolderPath, oldPaks[0]);
                var newPak = Path.Combine(newFolderPath, newPaks[0]);
                var pakResult = await pakCompareService.CompareAsync(unrealPakExePath, oldPak, newPak, cancellationToken);
                return new ModVersionCompareResult(
                    Path.GetFileName(oldPak), Path.GetFileName(newPak), pakResult.DataDifferences, pakResult.AssetDifferences);
            }

            return await CompareBundlesAsync(unrealPakExePath, oldFolderPath, oldPaks, newFolderPath, newPaks, cancellationToken);
        }

        return await Task.Run(() => CompareExmodFolders(oldFolderPath, newFolderPath), cancellationToken);
    }

    /// <summary>
    /// A bundle of paks on either side: each pak is compared with the pak of the same place and
    /// name in the other version, and every difference is prefixed with its pak ("Mod_Model_P.pak:
    /// …"). A pak only one version has is reported as added or removed, so a renamed pak shows as
    /// one of each.
    /// </summary>
    private async Task<ModVersionCompareResult> CompareBundlesAsync(
        string unrealPakExePath, string oldFolderPath, IReadOnlyList<string> oldPaks, string newFolderPath, IReadOnlyList<string> newPaks,
        CancellationToken cancellationToken)
    {
        var data = new List<ChangedDataFile>();
        var assets = new List<PakAssetDifference>();
        // Keyed without case, each keeping its own spelling for the path on disk.
        var oldByName = oldPaks.ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);
        var newByName = newPaks.ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var pak in oldPaks.Union(newPaks, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!newByName.TryGetValue(pak, out var newPak))
            {
                assets.Add(new PakAssetDifference(pak, PakAssetDifferenceKind.OnlyInFirst));
                continue;
            }

            if (!oldByName.TryGetValue(pak, out var oldPak))
            {
                assets.Add(new PakAssetDifference(pak, PakAssetDifferenceKind.OnlyInSecond));
                continue;
            }

            var result = await pakCompareService.CompareAsync(
                unrealPakExePath, Path.Combine(oldFolderPath, oldPak), Path.Combine(newFolderPath, newPak), cancellationToken);
            data.AddRange(result.DataDifferences.Select(d => d with { RelativePath = $"{pak}: {d.RelativePath}" }));
            assets.AddRange(result.AssetDifferences.Select(d => d with { RelativePath = $"{pak}: {d.RelativePath}" }));
        }

        return new ModVersionCompareResult($"previous version ({Count(oldPaks)})", $"this version ({Count(newPaks)})", data, assets);

        static string Count(IReadOnlyList<string> paks) => paks.Count == 1 ? "1 pak" : $"{paks.Count} paks";
    }

    private static ModVersionCompareResult CompareExmodFolders(string oldFolderPath, string newFolderPath)
    {
        var oldPackage = ExmodFolder.ReadPackageOnly(oldFolderPath);
        var newPackage = ExmodFolder.ReadPackageOnly(newFolderPath);

        var classifier = new DefaultSemanticClassifier();
        var oldTables = ToKeyedTablesByFile(oldPackage);
        var newTables = ToKeyedTablesByFile(newPackage);

        var differences = new List<ChangedDataFile>();
        foreach (var currentFile in oldTables.Keys.Union(newTables.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            oldTables.TryGetValue(currentFile, out var oldTable);
            newTables.TryGetValue(currentFile, out var newTable);

            // reportEmptyNewFile: true — "a new file exists" is itself meaningful to someone
            // checking what an author changed, even if that file happens to be empty right now.
            // See DataTableFileDiffer's own doc comment.
            var changed = DataTableFileDiffer.Diff(currentFile, oldTable, newTable, classifier, reportEmptyNewFile: true);
            if (changed is not null)
            {
                differences.Add(changed);
            }
        }

        return new ModVersionCompareResult(
            $"v{oldPackage.Version}",
            $"v{newPackage.Version}",
            differences,
            CompareAssets(oldFolderPath, newFolderPath));
    }

    /// <summary>
    /// Groups a package's own rows by file, reusing ExmodBaseDiffer.ToKeyedObject for the actual
    /// per-row "sparse EXMOD row → TableDiffer-shaped JsonObject" transform rather than re-deriving
    /// it — the two are the identical rule. A package can legitimately have more than one row
    /// sharing the same CurrentFile, so a second row's own keyed items are merged in (deep-cloned
    /// again, since a JsonNode can only ever belong to one parent) rather than the first row's table
    /// being dropped.
    /// </summary>
    private static Dictionary<string, JsonObject> ToKeyedTablesByFile(ExmodPackage package)
    {
        var tables = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in package.Rows)
        {
            var rowKeyed = ExmodBaseDiffer.ToKeyedObject(row);
            if (!tables.TryGetValue(row.CurrentFile, out var table))
            {
                tables[row.CurrentFile] = rowKeyed;
                continue;
            }

            foreach (var (itemName, fields) in rowKeyed)
            {
                table[itemName] = fields?.DeepClone();
            }
        }

        return tables;
    }

    /// <summary>A mod's binary assets (meshes, textures, icons) are part of what an author ships — a changed .uasset with no field change at all is a real, meaningful update.</summary>
    private static IReadOnlyList<PakAssetDifference> CompareAssets(string oldFolderPath, string newFolderPath)
    {
        var oldAssets = ExmodFolder.ListAssetPaths(oldFolderPath).ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);
        var newAssets = ExmodFolder.ListAssetPaths(newFolderPath).ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);

        var differences = new List<PakAssetDifference>();
        foreach (var relativePath in oldAssets.Keys.Union(newAssets.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var inOld = oldAssets.ContainsKey(relativePath);
            var inNew = newAssets.ContainsKey(relativePath);

            if (inOld && !inNew)
            {
                differences.Add(new PakAssetDifference(relativePath, PakAssetDifferenceKind.OnlyInFirst));
            }
            else if (!inOld && inNew)
            {
                differences.Add(new PakAssetDifference(relativePath, PakAssetDifferenceKind.OnlyInSecond));
            }
            else if (!FileContentComparer.AreIdentical(
                         Path.Combine(oldFolderPath, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                         Path.Combine(newFolderPath, relativePath.Replace('/', Path.DirectorySeparatorChar))))
            {
                differences.Add(new PakAssetDifference(relativePath, PakAssetDifferenceKind.DifferentContent));
            }
        }

        return differences;
    }

    private static bool HasExmod(string folderPath) =>
        Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
            .Any(f => f.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every .pak under the folder, as a '/'-separated path relative to it, in name order.</summary>
    private static IReadOnlyList<string> FindPaks(string folderPath) =>
        [.. Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(folderPath, f).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)];
}
