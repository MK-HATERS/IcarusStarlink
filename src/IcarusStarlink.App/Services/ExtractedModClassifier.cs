using System.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;
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
    /// source/nexusModId tag the resulting Library entry when known (a Nexus-sourced download); a
    /// plain manual import passes null for both. IsOpaquePak distinguishes the two ways Kind can be
    /// Library — a real EXMOD-shaped mod (false, already carries its own real name/author) vs. a
    /// bare .pak that stayed opaque because IPrebuiltPakImporter couldn't convert it (true) — since
    /// only the latter is worth a caller enriching with a Nexus mod-info lookup; overwriting an
    /// EXMOD's own declared name with Nexus's title would be a real, unwanted behavior change for
    /// the common case. Async because a single .pak now goes through IPrebuiltPakImporter, which
    /// genuinely extracts and diffs it before deciding.
    /// </summary>
    public static async Task<(string EntryName, string FolderName, PendingDownloadActivationKind Kind, bool IsOpaquePak)> ClassifyAndImport(
        string extractedDirectory, string originalFileName,
        ILibraryRepository libraryRepository, IUe4ssModRepository ue4ssModRepository,
        IPrebuiltPakImporter prebuiltPakImporter, string dataFolder, string? unrealPakExePath,
        string? source = null, int? nexusModId = null)
    {
        // A manual scan, not a "*.EXMOD" glob — Directory.EnumerateFiles' pattern matching follows
        // the filesystem's own case sensitivity, same reasoning ExmodFolder.FindExmodFile's own
        // comment already gives for why it does the same thing.
        var hasExmod = Directory.EnumerateFiles(extractedDirectory, "*", SearchOption.AllDirectories)
            .Any(f => f.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase));

        if (hasExmod)
        {
            // Let any real validation failure (corrupt EXMOD JSON, more than one .EXMOD, an unsafe
            // asset path, a size-budget violation) propagate up as-is — this genuinely IS
            // EXMOD-shaped content, so a failure here is a real problem, not a signal to try a
            // different format.
            var entry = libraryRepository.Import(extractedDirectory, source: source, nexusModId: nexusModId);
            return (entry.Name, entry.FolderName, PendingDownloadActivationKind.Library, IsOpaquePak: false);
        }

        var pakFiles = Directory.GetFiles(extractedDirectory, "*.pak", SearchOption.AllDirectories);
        if (pakFiles.Length == 1)
        {
            var entry = await prebuiltPakImporter.ImportAsync(pakFiles[0], dataFolder, unrealPakExePath, source: source, nexusModId: nexusModId);
            return (entry.Name, entry.FolderName, PendingDownloadActivationKind.Library, entry.IsOpaquePak);
        }

        if (pakFiles.Length > 1)
        {
            throw new FormatException($"Contains {pakFiles.Length} .pak files — ambiguous which one to import.");
        }

        // Neither EXMOD-shaped nor a single prebuilt pak — treat as a UE4SS mod, the one kind of
        // mod this app handles that carries no metadata file of its own to detect it by.
        var fallbackName = Path.GetFileNameWithoutExtension(originalFileName);
        var folderName = ue4ssModRepository.ImportFromFolder(extractedDirectory, fallbackName);
        return (folderName, folderName, PendingDownloadActivationKind.Ue4ssMod, IsOpaquePak: false);
    }
}
