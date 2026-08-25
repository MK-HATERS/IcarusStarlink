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
/// .pak mod has no readable package, so it falls through to the full pak-vs-pak comparison.
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

        var oldPak = FindSinglePak(oldFolderPath);
        var newPak = FindSinglePak(newFolderPath);

        // Both sides opaque paks: no package to read, so unpack and compare their real contents.
        if (oldPak is not null && newPak is not null && !HasExmod(oldFolderPath) && !HasExmod(newFolderPath))
        {
            if (string.IsNullOrWhiteSpace(unrealPakExePath))
            {
                throw new InvalidOperationException(
                    "This mod is a prebuilt .pak, so comparing versions needs UnrealPak.exe — set its path in Settings first.");
            }

            var pakResult = await pakCompareService.CompareAsync(unrealPakExePath, oldPak, newPak, cancellationToken);
            return new ModVersionCompareResult(
                Path.GetFileName(oldPak), Path.GetFileName(newPak), pakResult.DataDifferences, pakResult.AssetDifferences);
        }

        return await Task.Run(() => CompareExmodFolders(oldFolderPath, newFolderPath), cancellationToken);
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
            var inOld = oldTables.TryGetValue(currentFile, out var oldTable);
            var inNew = newTables.TryGetValue(currentFile, out var newTable);

            if (inOld && !inNew)
            {
                differences.Add(new ChangedDataFile(
                    currentFile, IsNewFile: false, IsRemovedFile: true,
                    RemovedRowNames: [.. oldTable!.Select(kv => kv.Key)], FieldChanges: []));
                continue;
            }

            if (!inOld && inNew)
            {
                differences.Add(new ChangedDataFile(
                    currentFile, IsNewFile: true, IsRemovedFile: false, RemovedRowNames: [],
                    TableDiffer.Diff(new JsonObject(), newTable!, currentFile, classifier)));
                continue;
            }

            var fieldChanges = TableDiffer.Diff(oldTable!, newTable!, currentFile, classifier);
            var removedRowNames = oldTable!.Select(kv => kv.Key).Except(newTable!.Select(kv => kv.Key)).ToList();
            if (fieldChanges.Count > 0 || removedRowNames.Count > 0)
            {
                differences.Add(new ChangedDataFile(currentFile, IsNewFile: false, IsRemovedFile: false, removedRowNames, fieldChanges));
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

    private static string? FindSinglePak(string folderPath) =>
        Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));
}
