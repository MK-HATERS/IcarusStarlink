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
/// Rebuild, throwing the result away afterward. Prefers reading the pak's own bundled .EXMOD when
/// one is present (classic IMM has baked one into every pak it builds since its own v1.1, per its
/// real changelog — confirmed directly against a real community-authored pak,
/// BF_Shengong_Invincible_P.pak, which carries a bare "BF_Shengong_Invincible.EXMOD" at its own
/// pak root) — byte-identical to what the author actually wrote, not an approximation that can
/// miss a field diffing can't detect (e.g. one deliberately set back to its own base value).
/// Falls back to PrebuiltPakFieldChangeExtractor's own diffing technique (plus
/// ExmodFieldChangeMapper's FieldChange -> ExmodFileRow grouping, built for the EXMOD editor's
/// raw-JSON Apply path) only when no usable bundled EXMOD exists.
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

        // Always under the OS temp root plus a per-call GUID, same as RebuildService's own
        // prebuilt-pak scratch extraction — unlike UnrealPakService.ExtractDataPakAsync, nothing
        // here ever moves this directory (only reads bytes out of it before deleting it), so the
        // cross-volume Directory.Move concern that made THAT extraction use a sibling folder
        // doesn't apply here.
        var scratchDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"PakConvert_{Guid.NewGuid():N}");

        try
        {
            await unrealPakService.ExtractPakAsync(unrealPakExePath, pakFilePath, scratchDirectory, cancellationToken);

            var embedded = TryReadEmbeddedExmod(scratchDirectory, pakName, report);

            List<ExmodFileRow> rows;
            string effectiveName, effectiveAuthor, effectiveVersion, effectiveDescription;
            Func<string, bool> isSupersededByPackageData;

            if (embedded is { } found)
            {
                var (embeddedPackage, embeddedRelativePath) = found;

                // This path never reads dataFolder at all — the bundled EXMOD already carries the
                // author's own exact field changes, nothing to diff against base data for.
                report.AddNote(
                    $"'{pakName}' carries its own bundled EXMOD data — read directly instead of reconstructed by comparing against current game data.");

                rows = embeddedPackage.Rows;
                effectiveName = PreferNonEmpty(embeddedPackage.Name, name);
                effectiveAuthor = PreferNonEmpty(embeddedPackage.Author is "Unknown" ? null : embeddedPackage.Author, author);
                effectiveVersion = PreferNonEmpty(embeddedPackage.Version, "1.0");
                effectiveDescription = PreferNonEmpty(
                    embeddedPackage.Description,
                    $"Converted from a prebuilt .pak import ('{pakName}.pak') — its own bundled EXMOD data was used directly.");

                // Superseded entirely by the embedded package's own Rows — carrying the pak's own
                // compiled "data/" JSON through as a bundled asset would be redundant at best (a
                // file nothing ever reads) and misleading at worst (it looks like a real bundled
                // asset but is actually just the pre-merge compiled output this conversion
                // already replaces). "data/" is the same convention PrebuiltPakFieldChangeExtractor
                // itself already reads/excludes in the diffing branch below, confirmed for real
                // against the same sample pak (data/Traits/D_Armour.json). The bundled .EXMOD file
                // itself is excluded too — it's been consumed as the package's own data, not a
                // binary asset to carry through.
                isSupersededByPackageData = relativePath =>
                    relativePath.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(relativePath, embeddedRelativePath, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Checks for at least one real *.json file somewhere under dataFolder, not just
                // "the directory has any entry at all" — an interrupted/partial "Update data
                // folder" run can leave an empty subdirectory behind, which would otherwise pass a
                // bare Any() check and then fail every single base-file lookup inside
                // PrebuiltPakFieldChangeExtractor silently (the pak would "convert" into an EXMOD
                // with zero real rows, dumping everything as raw assets, instead of this clear
                // message). Only checked here, not before extraction — a pak with its own bundled
                // EXMOD (the branch above) never needs dataFolder at all.
                if (!Directory.Exists(dataFolder) || !Directory.EnumerateFiles(dataFolder, "*.json", SearchOption.AllDirectories).Any())
                {
                    report.AddWarning(
                        $"Couldn't convert '{pakName}' into an editable mod — run \"Update data folder\" in Settings first. Imported as a prebuilt pak instead.");
                    return null;
                }

                var classifier = new DefaultSemanticClassifier();
                var (changes, diffedPaths) = PrebuiltPakFieldChangeExtractor.Extract(scratchDirectory, dataFolder, pakName, classifier, report);
                rows = ExmodFieldChangeMapper.FromFieldChanges(changes);
                effectiveName = name;
                effectiveAuthor = author;
                effectiveVersion = "1.0";
                effectiveDescription = $"Converted from a prebuilt .pak import ('{pakName}.pak') — its data changes are now real, per-field edits you can review here.";
                isSupersededByPackageData = diffedPaths.Contains;
            }

            // Everything not superseded above — binary assets, a data/*.json file with no matching
            // base table, or one that wasn't valid JSON — still has to be part of the converted
            // mod, copied through unchanged exactly like RebuildService's own raw-copy-through pass
            // does for the same reason.
            var assets = new List<ExmodAssetEntry>();
            foreach (var file in Directory.EnumerateFiles(scratchDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(scratchDirectory, file).Replace('\\', '/');
                if (isSupersededByPackageData(relativePath))
                {
                    continue;
                }

                assets.Add(new ExmodAssetEntry(relativePath, File.ReadAllBytes(file)));
            }

            var package = new ExmodPackage
            {
                Name = effectiveName,
                Author = effectiveAuthor,
                Version = effectiveVersion,
                Description = effectiveDescription,
                // Always derived from the pak's own real filename, never from `name`/the embedded
                // package's own FileName (neither guaranteed simple) — still run through the same
                // sanitizer every other externally-sourced name goes through rather than trusted
                // as-is: the pak reached this method's caller via an archive extraction or a
                // download, not necessarily a name Windows itself would have accepted verbatim.
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

    /// <summary>
    /// Classic IMM writes its own bundled .EXMOD bare at the pak's own root — confirmed for real
    /// against BF_Shengong_Invincible_P.pak, not guessed. A raw string search on the extracted
    /// tree's top level only (never recursing into "data/", which can itself contain ordinary
    /// *.json but never a *.EXMOD) so a coincidentally-named file deeper in the pak can't be
    /// mistaken for one. Returns null (never throws) for a pak with none, or one that has a
    /// same-named file that isn't actually valid EXMOD JSON — either way the caller falls back to
    /// diffing exactly as if this method had never been called.
    /// </summary>
    private static (ExmodPackage Package, string RelativePath)? TryReadEmbeddedExmod(string scratchDirectory, string pakName, MergeReport report)
    {
        var candidate = Directory.EnumerateFiles(scratchDirectory, "*.EXMOD", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (candidate is null)
        {
            return null;
        }

        try
        {
            var package = ExmodJson.Parse(File.ReadAllText(candidate));
            var relativePath = Path.GetRelativePath(scratchDirectory, candidate).Replace('\\', '/');
            return (package, relativePath);
        }
        catch (FormatException ex)
        {
            report.AddWarning(
                $"'{pakName}' has a bundled EXMOD file, but it couldn't be read ({ex.Message}) — reconstructed by comparing against current game data instead.");
            return null;
        }
    }

    /// <summary>Prefers a real, non-placeholder value over a fallback — used for every field the
    /// embedded package's own metadata might legitimately leave blank or generic.</summary>
    private static string PreferNonEmpty(string? primary, string fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}
