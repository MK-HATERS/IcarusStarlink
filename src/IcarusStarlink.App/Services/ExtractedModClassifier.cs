using System.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Import;

namespace IcarusStarlink.App.Services;

/// <summary>
/// What ClassifyAndImport produced. A single download can land as SEVERAL things (see
/// PendingDownloadActivatedItem) — Items lists every one of them, EntryName/FolderName/Kind describe
/// the primary one for simple consumers, and the four-way Deconstruct keeps the original callers'
/// `var (name, folder, kind, isOpaquePak) = …` shape working.
/// </summary>
public sealed record ClassifiedImport(
    string EntryName,
    string FolderName,
    PendingDownloadActivationKind Kind,
    bool IsOpaquePak,
    IReadOnlyList<PendingDownloadActivatedItem> Items,
    IReadOnlyList<string> Requirements)
{
    public void Deconstruct(out string entryName, out string folderName, out PendingDownloadActivationKind kind, out bool isOpaquePak)
    {
        entryName = EntryName;
        folderName = FolderName;
        kind = Kind;
        isOpaquePak = IsOpaquePak;
    }
}

/// <summary>
/// Figures out what an already-extracted archive actually is and imports it the right way: an
/// EXMOD-shaped mod (ExmodFolder.Read searches the whole tree for a .EXMOD file, wherever it's
/// nested), a prebuilt .pak with no EXMOD wrapper, a "game-folder layout" bundle (the zip mirrors
/// the Icarus install: Icarus\Binaries\Win64\ue4ss\Mods\&lt;Mod&gt;\ plus Icarus\Content\Paks\mods\*.pak
/// — the shape real Nexus UE4SS mods ship in, confirmed live against ArcFissionLibrary and
/// WirelessPower), or — the catch-all, since a UE4SS mod carries no metadata file of its own to
/// detect it by — a UE4SS mod folder. Extracted from DownloadsViewModel.ClassifyAndImportExtractedModAsync
/// (that method's own original home, still its caller for a Nexus-sourced download) so
/// LibraryViewModel's own manual "Import archive…" can reuse the identical detection instead of a
/// second copy — a mod author might ship a zip, rar, or 7z through either path, and neither the
/// file extension nor a dialog's own filter choice can tell what's actually inside ahead of time.
/// Also reads any README it finds for a REQUIREMENTS section (ModReadmeRequirements), so the
/// caller can tell the user which OTHER downloads the mod needs.
/// </summary>
public static class ExtractedModClassifier
{
    private const int MaxNestedArchiveDepth = 3;

    private static readonly string[] NestedArchiveExtensions = [".exmodz", ".zip", ".rar", ".7z"];

    public static Task<ClassifiedImport> ClassifyAndImport(
        string extractedDirectory, string originalFileName,
        ILibraryRepository libraryRepository, IUe4ssModRepository ue4ssModRepository,
        IPrebuiltPakImporter prebuiltPakImporter, string dataFolder, string? unrealPakExePath,
        string? source = null, int? nexusModId = null, IReadOnlyCollection<string>? ue4ssNamesAlreadyInstalledInGame = null) =>
        ClassifyAndImportCore(
            extractedDirectory, originalFileName, libraryRepository, ue4ssModRepository, prebuiltPakImporter,
            dataFolder, unrealPakExePath, source, nexusModId, ue4ssNamesAlreadyInstalledInGame, nestedDepth: 0);

    private static async Task<ClassifiedImport> ClassifyAndImportCore(
        string extractedDirectory, string originalFileName,
        ILibraryRepository libraryRepository, IUe4ssModRepository ue4ssModRepository,
        IPrebuiltPakImporter prebuiltPakImporter, string dataFolder, string? unrealPakExePath,
        string? source, int? nexusModId, IReadOnlyCollection<string>? ue4ssNamesAlreadyInstalledInGame, int nestedDepth)
    {
        // A manual scan, not a "*.EXMOD" glob — Directory.EnumerateFiles' pattern matching follows
        // the filesystem's own case sensitivity, same reasoning ExmodFolder.FindExmodFile's own
        // comment already gives for why it does the same thing.
        var allFiles = Directory.GetFiles(extractedDirectory, "*", SearchOption.AllDirectories);
        var requirements = ReadRequirements(allFiles);
        var hasExmod = allFiles.Any(f => f.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase));

        if (hasExmod)
        {
            // Let any real validation failure (corrupt EXMOD JSON, more than one .EXMOD, an unsafe
            // asset path, a size-budget violation) propagate up as-is — this genuinely IS
            // EXMOD-shaped content, so a failure here is a real problem, not a signal to try a
            // different format.
            var entry = libraryRepository.Import(extractedDirectory, source: source, nexusModId: nexusModId);
            return Single(entry.Name, entry.FolderName, PendingDownloadActivationKind.Library, isOpaquePak: false, requirements);
        }

        var pakFiles = allFiles.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)).ToArray();

        // The game-folder layout (a real shape found live, see the class doc): every UE4SS mod
        // folder under a "…\ue4ss\Mods\" directory is staged under ITS OWN name (not the download's
        // file name), and every pak that sits under a "…\Paks\…" directory is imported — all of
        // them, since a bundle that ships two paks (an icon pak and a model pak) needs both, which
        // is a different situation from two loose paks of unknown relationship (still refused as
        // ambiguous below).
        var ue4ssModFolders = FindGameLayoutUe4ssModFolders(extractedDirectory);
        var layoutPaks = pakFiles.Where(IsUnderPaksFolder).ToArray();
        if (ue4ssModFolders.Count > 0 || layoutPaks.Length > 1)
        {
            var items = new List<PendingDownloadActivatedItem>();
            var names = new List<string>();
            var anyOpaque = false;
            foreach (var modFolder in ue4ssModFolders)
            {
                var folderName = ue4ssModRepository.ImportFromFolder(modFolder, Path.GetFileName(modFolder), ue4ssNamesAlreadyInstalledInGame);
                items.Add(new PendingDownloadActivatedItem(folderName, PendingDownloadActivationKind.Ue4ssMod));
                names.Add(folderName);
            }

            foreach (var pak in layoutPaks.Length > 0 ? layoutPaks : pakFiles)
            {
                var entry = await prebuiltPakImporter.ImportAsync(pak, dataFolder, unrealPakExePath, source: source, nexusModId: nexusModId);
                items.Add(new PendingDownloadActivatedItem(entry.FolderName, PendingDownloadActivationKind.Library));
                names.Add(entry.Name);
                anyOpaque |= entry.IsOpaquePak;
            }

            var primary = items[0];
            return new ClassifiedImport(names[0], primary.FolderName, primary.Kind, anyOpaque, items, requirements);
        }

        if (pakFiles.Length == 1)
        {
            var entry = await prebuiltPakImporter.ImportAsync(pakFiles[0], dataFolder, unrealPakExePath, source: source, nexusModId: nexusModId);
            return Single(entry.Name, entry.FolderName, PendingDownloadActivationKind.Library, entry.IsOpaquePak, requirements);
        }

        if (pakFiles.Length > 1)
        {
            throw new FormatException(
                $"Contains {pakFiles.Length} .pak files with no game-folder layout to say they belong together — ambiguous which one to import. "
                + "Extract it yourself and import the one you want (or both, one at a time).");
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
            var nested = await ClassifyAndImportCore(
                nestedDirectory, Path.GetFileName(nestedArchives[0]), libraryRepository, ue4ssModRepository,
                prebuiltPakImporter, dataFolder, unrealPakExePath, source, nexusModId, ue4ssNamesAlreadyInstalledInGame,
                nestedDepth + 1);
            return nested.Requirements.Count > 0 || requirements.Count == 0 ? nested : nested with { Requirements = requirements };
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
        // that carries no metadata file of its own to detect it by. Named after the upload, not
        // the full Nexus file name with its id/timestamp/hash tail (a real complaint).
        var fallbackName = NexusDownloadFileName.StripNexusSuffix(Path.GetFileNameWithoutExtension(originalFileName));
        var stagedName = ue4ssModRepository.ImportFromFolder(extractedDirectory, fallbackName, ue4ssNamesAlreadyInstalledInGame);
        return Single(stagedName, stagedName, PendingDownloadActivationKind.Ue4ssMod, isOpaquePak: false, requirements);
    }

    private static ClassifiedImport Single(string entryName, string folderName, PendingDownloadActivationKind kind, bool isOpaquePak, IReadOnlyList<string> requirements) =>
        new(entryName, folderName, kind, isOpaquePak, [new PendingDownloadActivatedItem(folderName, kind)], requirements);

    /// <summary>Every "&lt;anything&gt;\ue4ss\Mods\&lt;Mod&gt;" folder that actually contains files — the UE4SS install layout.</summary>
    private static List<string> FindGameLayoutUe4ssModFolders(string root)
    {
        var result = new List<string>();
        foreach (var modsFolder in Directory.GetDirectories(root, "Mods", SearchOption.AllDirectories))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(modsFolder) ?? "");
            if (!parent.Equals("ue4ss", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var modFolder in Directory.GetDirectories(modsFolder))
            {
                if (Directory.EnumerateFiles(modFolder, "*", SearchOption.AllDirectories).Any())
                {
                    result.Add(modFolder);
                }
            }
        }

        return result;
    }

    private static bool IsUnderPaksFolder(string pakPath) =>
        (Path.GetDirectoryName(pakPath) ?? "")
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("Paks", StringComparison.OrdinalIgnoreCase));

    /// <summary>The first README (txt/md, shallowest first) that carries a REQUIREMENTS section wins.</summary>
    private static IReadOnlyList<string> ReadRequirements(string[] allFiles)
    {
        var readmes = allFiles
            .Where(f => Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase)
                && (f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar));
        foreach (var readme in readmes)
        {
            try
            {
                var found = ModReadmeRequirements.Parse(File.ReadAllText(readme));
                if (found.Count > 0)
                {
                    return found;
                }
            }
            catch (IOException)
            {
                // Best-effort — a README that can't be read just contributes nothing.
            }
        }

        return [];
    }
}
