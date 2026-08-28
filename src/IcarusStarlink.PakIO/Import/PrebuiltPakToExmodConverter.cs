using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Pak;
using IcarusStarlink.PakIO.Rebuild;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Import;

/// <summary>
/// Converts a prebuilt/opaque .pak into a real, editable EXMOD at import time — the permanent
/// counterpart to what PrebuiltPakFieldChangeExtractor already does transiently, once per
/// Rebuild, throwing the result away afterward. Reuses that same extraction + diffing technique,
/// plus ExmodFieldChangeMapper's own FieldChange -> ExmodFileRow grouping (built for the EXMOD
/// editor's raw-JSON Apply path), rather than any new diff logic of its own.
/// </summary>
public sealed class PrebuiltPakToExmodConverter(IUnrealPakService unrealPakService) : IPrebuiltPakToExmodConverter
{
    public async Task<ExmodPackageContents?> TryConvertAsync(
        string pakFilePath, string dataFolder, string unrealPakExePath, string name, string author,
        MergeReport report, CancellationToken cancellationToken = default)
    {
        var pakName = Path.GetFileNameWithoutExtension(pakFilePath);

        if (!File.Exists(unrealPakExePath))
        {
            report.AddWarning(
                $"Couldn't convert '{pakName}' into an editable mod — UnrealPak.exe isn't set up yet. Imported as a prebuilt pak instead.");
            return null;
        }

        // Checks for at least one real *.json file somewhere under dataFolder, not just "the
        // directory has any entry at all" — an interrupted/partial "Update data folder" run can
        // leave an empty subdirectory behind, which would otherwise pass a bare Any() check and
        // then fail every single base-file lookup inside PrebuiltPakFieldChangeExtractor silently
        // (the pak would "convert" into an EXMOD with zero real rows, dumping everything as raw
        // assets, instead of this clear message).
        if (!Directory.Exists(dataFolder) || !Directory.EnumerateFiles(dataFolder, "*.json", SearchOption.AllDirectories).Any())
        {
            report.AddWarning(
                $"Couldn't convert '{pakName}' into an editable mod — run \"Update data folder\" in Settings first. Imported as a prebuilt pak instead.");
            return null;
        }

        // Always under the OS temp root plus a per-call GUID, same as RebuildService's own
        // prebuilt-pak scratch extraction — unlike UnrealPakService.ExtractDataPakAsync, nothing
        // here ever moves this directory (only reads bytes out of it before deleting it), so the
        // cross-volume Directory.Move concern that made THAT extraction use a sibling folder
        // doesn't apply here.
        var scratchDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"PakConvert_{Guid.NewGuid():N}");

        try
        {
            await unrealPakService.ExtractPakAsync(unrealPakExePath, pakFilePath, scratchDirectory, cancellationToken);

            var classifier = new DefaultSemanticClassifier();
            var (changes, diffedPaths) = PrebuiltPakFieldChangeExtractor.Extract(scratchDirectory, dataFolder, pakName, classifier, report);
            var rows = ExmodFieldChangeMapper.FromFieldChanges(changes);

            // Everything the field-level diff above didn't cover — binary assets, a data/*.json
            // file with no matching base table, or one that wasn't valid JSON — still has to be
            // part of the converted mod, copied through unchanged exactly like RebuildService's
            // own raw-copy-through pass does for the same reason.
            var assets = new List<ExmodAssetEntry>();
            foreach (var file in Directory.EnumerateFiles(scratchDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(scratchDirectory, file).Replace('\\', '/');
                if (diffedPaths.Contains(relativePath))
                {
                    continue;
                }

                assets.Add(new ExmodAssetEntry(relativePath, File.ReadAllBytes(file)));
            }

            var package = new ExmodPackage
            {
                Name = name,
                Author = author,
                Version = "1.0",
                Description = $"Converted from a prebuilt .pak import ('{pakName}.pak') — its data changes are now real, per-field edits you can review here.",
                // Always derived from the pak's own real filename, never from `name` (possibly
                // Nexus-enriched, not guaranteed simple) — still run through the same sanitizer
                // every other externally-sourced name goes through rather than trusted as-is: the
                // pak reached this method's caller via an archive extraction or a download, not
                // necessarily a name Windows itself would have accepted verbatim.
                FileName = AssetPathGuard.SanitizeToSimpleFileName(pakName),
                Rows = rows,
            };

            return new ExmodPackageContents(package, assets);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.AddWarning($"Couldn't convert '{pakName}' into an editable mod ({ex.Message}). Imported as a prebuilt pak instead.");
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratchDirectory))
                {
                    Directory.Delete(scratchDirectory, recursive: true);
                }
            }
            catch (Exception)
            {
                // Best-effort scratch cleanup, same as every other scratch-extraction cleanup in
                // this codebase (e.g. LibraryViewModel.ImportOnePath) — a locked file here (an AV
                // scan, a lingering handle) must not turn an otherwise-successful (or already-
                // failed-and-reported) conversion attempt into an unhandled exception, which would
                // violate this method's own "never throws" contract.
            }
        }
    }
}
