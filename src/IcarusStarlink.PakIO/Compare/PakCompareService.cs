using System.Text.Json;
using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Compare;

/// <summary>
/// The pak-vs-pak comparison tool (big-plan item 8b): built for "unpack the original classic-IMM
/// merged pak and verify this app's own rebuilt pak is equivalent", but works for any two paks.
/// DataTable JSONs (the shape RebuildService itself stages under data/) diff at field level via
/// TableDiffer — the same engine base-vs-modded editing and Weekly Changes already use — so an
/// equivalent-but-differently-formatted table (key order, whitespace) correctly reads as
/// identical. Everything else falls back to a raw byte comparison, including a JSON with no
/// "Rows" array: RowsToKeyedObject maps those to an empty table, which would make two genuinely
/// different non-table JSONs silently compare as equal if they went down the table path.
/// </summary>
public sealed class PakCompareService(IUnrealPakService unrealPakService) : IPakCompareService
{
    public async Task<PakCompareResult> CompareAsync(
        string unrealPakExePath, string firstPakPath, string secondPakPath, CancellationToken cancellationToken = default)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"PakCompare_{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(tempRoot, "first");
        var secondDirectory = Path.Combine(tempRoot, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);

        try
        {
            await unrealPakService.ExtractPakAsync(unrealPakExePath, firstPakPath, firstDirectory, cancellationToken);
            await unrealPakService.ExtractPakAsync(unrealPakExePath, secondPakPath, secondDirectory, cancellationToken);

            // The comparison itself is pure CPU + file reads over potentially hundreds of files —
            // kept off the caller's thread the same way RebuildService's own merge phase is.
            return await Task.Run(() => Compare(firstDirectory, secondDirectory), cancellationToken);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static PakCompareResult Compare(string firstDirectory, string secondDirectory)
    {
        var firstFiles = ListFiles(firstDirectory);
        var secondFiles = ListFiles(secondDirectory);
        var classifier = new DefaultSemanticClassifier();
        var dataDifferences = new List<ChangedDataFile>();
        var assetDifferences = new List<PakAssetDifference>();

        var allPaths = firstFiles.Keys.Union(secondFiles.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in allPaths)
        {
            var inFirst = firstFiles.TryGetValue(relativePath, out var firstPath);
            var inSecond = secondFiles.TryGetValue(relativePath, out var secondPath);

            var firstTable = inFirst ? TryReadDataTable(firstPath!) : null;
            var secondTable = inSecond ? TryReadDataTable(secondPath!) : null;

            if (inFirst && inSecond)
            {
                if (firstTable is not null && secondTable is not null)
                {
                    var fieldChanges = TableDiffer.Diff(firstTable, secondTable, relativePath, classifier);
                    var removedRowNames = firstTable.Select(kv => kv.Key).Except(secondTable.Select(kv => kv.Key)).ToList();
                    if (fieldChanges.Count > 0 || removedRowNames.Count > 0)
                    {
                        dataDifferences.Add(new ChangedDataFile(relativePath, IsNewFile: false, IsRemovedFile: false, removedRowNames, fieldChanges));
                    }
                }
                else if (!FileContentComparer.AreIdentical(firstPath!, secondPath!))
                {
                    assetDifferences.Add(new PakAssetDifference(relativePath, PakAssetDifferenceKind.DifferentContent));
                }
            }
            else if (inFirst)
            {
                if (firstTable is not null)
                {
                    dataDifferences.Add(new ChangedDataFile(
                        relativePath, IsNewFile: false, IsRemovedFile: true,
                        RemovedRowNames: [.. firstTable.Select(kv => kv.Key)], FieldChanges: []));
                }
                else
                {
                    assetDifferences.Add(new PakAssetDifference(relativePath, PakAssetDifferenceKind.OnlyInFirst));
                }
            }
            else
            {
                if (secondTable is not null)
                {
                    var newFileChanges = TableDiffer.Diff(new JsonObject(), secondTable, relativePath, classifier);
                    dataDifferences.Add(new ChangedDataFile(relativePath, IsNewFile: true, IsRemovedFile: false, RemovedRowNames: [], newFileChanges));
                }
                else
                {
                    assetDifferences.Add(new PakAssetDifference(relativePath, PakAssetDifferenceKind.OnlyInSecond));
                }
            }
        }

        return new PakCompareResult(dataDifferences, assetDifferences, firstFiles.Count, secondFiles.Count);
    }

    private static Dictionary<string, string> ListFiles(string rootDirectory) =>
        Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(rootDirectory, path).Replace('\\', '/'),
                path => path,
                StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the file isn't a parseable DataTable JSON (wrong extension, malformed, or no "Rows" array) — those compare as raw content instead.</summary>
    private static JsonObject? TryReadDataTable(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // Duplicate-tolerant: a real classic-IMM merged pak contains a row listing
            // "ResourceCostMultipliers" twice. Reading those properly beats the old behavior of
            // giving up and byte-comparing, which reported every such file as "content differs"
            // even when the two paks agreed on every value.
            var parsed = DuplicateTolerantJson.Parse(File.ReadAllText(path)) as JsonObject;
            if (parsed?["Rows"] is not JsonArray)
            {
                return null;
            }

            return DataTableJson.RowsToKeyedObject(parsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

}
