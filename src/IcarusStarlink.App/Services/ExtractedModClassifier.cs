using System.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Figures out what an already-extracted archive actually is and imports it the right way: an
/// EXMOD-shaped mod (ExmodFolder.Read searches the whole tree for a .EXMOD file, wherever it's
/// nested), a prebuilt .pak with no EXMOD wrapper, or — the catch-all, since a UE4SS mod carries no
/// metadata file of its own to detect it by — a UE4SS mod folder. Extracted from
/// DownloadsViewModel.ClassifyAndImportExtractedModAsync (that method's own original home, still its
/// caller for a Nexus-sourced download) so LibraryViewModel's own manual "Import archive…" can reuse
/// the identical detection instead of a second copy — a mod author might ship a zip, rar, or 7z
/// through either path, and neither the file extension nor a dialog's own filter choice can tell
/// what's actually inside ahead of time.
/// </summary>
public static class ExtractedModClassifier
{
    /// <summary>
    /// A real Nexus upload confirmed live wraps a single .EXMODZ inside a .zip (the author zipped
    /// their mod file for upload) — one genuine layer of nesting. Three allows for an author who
    /// double-wraps, while still bounding the recursion against a hostile archive-in-archive chain
    /// that would otherwise extract forever (ExmodSizeBudget bounds each layer's size, not the depth).
    /// </summary>
    private const int MaxNestedArchiveDepth = 3;

    private static readonly string[] NestedArchiveExtensions = [".exmodz", ".zip", ".rar", ".7z"];

    /// <summary>
    /// source/nexusModId tag the resulting Library entry when known (a Nexus-sourced download); a
    /// plain manual import passes null for both. IsOpaquePak distinguishes the two ways Kind can be
    /// Library — a real EXMOD-shaped mod (false, already carries its own real name/author) vs. a
    /// bare .pak that stayed opaque because IPrebuiltPakImporter couldn't convert it (true) — since
    /// only the latter is worth a caller enriching with a Nexus mod-info lookup; overwriting an
    /// EXMOD's own declared name with Nexus's title would be a real, unwanted behavior change for
    /// the common case. Async because a single .pak now goes through IPrebuiltPakImporter, which
    /// genuinely extracts and diffs it before deciding.
    /// </summary>
    public static Task<(string EntryName, string FolderName, PendingDownloadActivationKind Kind, bool IsOpaquePak)> ClassifyAndImport(
        string extractedDirectory, string originalFileName,
        ILibraryRepository libraryRepository, IUe4ssModRepository ue4ssModRepository,
        IPrebuiltPakImporter prebuiltPakImporter, string dataFolder, string? unrealPakExePath,
        string? source = null, int? nexusModId = null, IReadOnlyCollection<string>? ue4ssNamesAlreadyInstalledInGame = null) =>
        ClassifyAndImportCore(
            extractedDirectory, originalFileName, libraryRepository, ue4ssModRepository, prebuiltPakImporter,
            dataFolder, unrealPakExePath, source, nexusModId, ue4ssNamesAlreadyInstalledInGame, nestedDepth: 0);

    private static async Task<(string EntryName, string FolderName, PendingDownloadActivationKind Kind, bool IsOpaquePak)> ClassifyAndImportCore(
        string extractedDirectory, string originalFileName,
        ILibraryRepository libraryRepository, IUe4ssModRepository ue4ssModRepository,
        IPrebuiltPakImporter prebuiltPakImporter, string dataFolder, string? unrealPakExePath,
        string? source, int? nexusModId, IReadOnlyCollection<string>? ue4ssNamesAlreadyInstalledInGame, int nestedDepth)
    {
        // A manual scan, not a "*.EXMOD" glob — Directory.EnumerateFiles' pattern matching follows
        // the filesystem's own case sensitivity, same reasoning ExmodFolder.FindExmodFile's own
        // comment already gives for why it does the same thing.
        var allFiles = Directory.GetFiles(extractedDirectory, "*", SearchOption.AllDirectories);
        var hasExmod = allFiles.Any(f => f.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase));

        if (hasExmod)
        {
            // Let any real validation failure (corrupt EXMOD JSON, more than one .EXMOD, an unsafe
            // asset path, a size-budget violation) propagate up as-is — this genuinely IS
            // EXMOD-shaped content, so a failure here is a real problem, not a signal to try a
            // different format.
            var entry = libraryRepository.Import(extractedDirectory, source: source, nexusModId: nexusModId);
            return (entry.Name, entry.FolderName, PendingDownloadActivationKind.Library, IsOpaquePak: false);
        }

        var pakFiles = allFiles.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pakFiles.Length == 1)
        {
            var entry = await prebuiltPakImporter.ImportAsync(pakFiles[0], dataFolder, unrealPakExePath, source: source, nexusModId: nexusModId);
            return (entry.Name, entry.FolderName, PendingDownloadActivationKind.Library, entry.IsOpaquePak);
        }

        if (pakFiles.Length > 1)
        {
            throw new FormatException($"Contains {pakFiles.Length} .pak files — ambiguous which one to import.");
        }

        // A real bug found live (a user's own Nexus bug report, confirmed by inspecting the actual
        // downloaded file): the Nexus upload was a .zip wrapping a single .EXMODZ — the author had
        // zipped their mod file for upload. After extracting the outer zip, this folder held one
        // .EXMODZ and nothing else: no .EXMOD, no .pak, nothing Unreal-shaped — so it fell straight
        // through to the UE4SS catch-all below and was "staged" as a UE4SS mod, uselessly. The
        // inner archive is the real mod; extract it and classify THAT, exactly as if it had been
        // the download itself (the manual "Import archive…" path handles a bare .EXMODZ fine, which
        // is why that route never showed this bug). One nested archive is unambiguous; several
        // (e.g. an author bundling multiple variants in one upload) is the same genuine ambiguity
        // the multi-.pak rule above already refuses to guess at.
        var nestedArchives = allFiles
            .Where(f => NestedArchiveExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (nestedArchives.Length == 1)
        {
            if (nestedDepth >= MaxNestedArchiveDepth)
            {
                throw new FormatException($"Archives nested more than {MaxNestedArchiveDepth} levels deep — refusing to keep unpacking.");
            }

            // Under extractedDirectory on purpose: every caller deletes that whole tree afterward,
            // so this layer is cleaned up with it. Classifying the nested folder (not the parent)
            // keeps the now-redundant outer .EXMODZ/.zip file out of what Import copies.
            var nestedDirectory = Path.Combine(extractedDirectory, $"__nested{nestedDepth}_{Guid.NewGuid():N}");
            AnyArchiveExtractor.ExtractToDirectory(nestedArchives[0], nestedDirectory);
            return await ClassifyAndImportCore(
                nestedDirectory, Path.GetFileName(nestedArchives[0]), libraryRepository, ue4ssModRepository,
                prebuiltPakImporter, dataFolder, unrealPakExePath, source, nexusModId, ue4ssNamesAlreadyInstalledInGame,
                nestedDepth + 1);
        }

        if (nestedArchives.Length > 1)
        {
            throw new FormatException(
                $"Contains {nestedArchives.Length} archives inside it — ambiguous which one is the mod. Extract it yourself and import the one you want.");
        }

        // A real bug found live: a user reported a downloaded EXMODZ silently landing as a UE4SS
        // mod instead. Neither EXMOD-shaped nor a single prebuilt pak genuinely means "treat as
        // UE4SS" ONLY when the content is actually Lua-script-shaped — but raw Unreal asset files
        // (.uasset/.uexp — exactly what a real EXMOD's own BP/ subfolder is full of) with no .EXMOD
        // manifest alongside them is a different, much more likely real-world case: a download
        // that's missing its manifest (an incomplete/corrupt download, or a mod author's own
        // packaging mistake), not a genuine UE4SS mod at all. UE4SS mods are Lua scripts; they have
        // no legitimate reason to carry raw .uasset/.uexp content, which UE4SS itself can't do
        // anything with sitting loose in a mod folder — silently "staging" this as a UE4SS mod
        // previously reported success while actually importing something completely inert.
        var hasOrphanedUnrealAssets = allFiles
            .Any(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase));
        if (hasOrphanedUnrealAssets)
        {
            throw new FormatException(
                "Contains Unreal asset files (.uasset/.uexp) but no .EXMOD manifest — this looks like an incomplete "
                + "or corrupted download (or a packaging mistake by the mod author) rather than a real UE4SS mod. "
                + "Try re-downloading it.");
        }

        // Neither EXMOD-shaped nor a single prebuilt pak, and nothing here looks like orphaned
        // Unreal asset content either — treat as a UE4SS mod, the one kind of mod this app handles
        // that carries no metadata file of its own to detect it by.
        var fallbackName = Path.GetFileNameWithoutExtension(originalFileName);
        var folderName = ue4ssModRepository.ImportFromFolder(extractedDirectory, fallbackName, ue4ssNamesAlreadyInstalledInGame);
        return (folderName, folderName, PendingDownloadActivationKind.Ue4ssMod, IsOpaquePak: false);
    }
}
