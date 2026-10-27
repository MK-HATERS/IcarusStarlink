using IcarusStarlink.Core.Patches;
using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Patches;

/// <summary>
/// What travels inside a patch besides its manifest: the EXMOD mods, pak mods and UE4SS mods the
/// manifest marks Bundled, keyed by the manifest's own FolderName / Name. Which mods are bundled
/// is the caller's decision; the service only writes what it's handed.
/// </summary>
/// <param name="Exmods">Bundled EXMOD mods, by Library folder name.</param>
/// <param name="PakFolders">Bundled pak mods, by Library folder name: the pak files to carry.</param>
/// <param name="Ue4ssFolders">Bundled UE4SS mods, by mod name: the mod folder to carry (logs, backups and enabled.txt are left out).</param>
public sealed record PatchBundle(
    IReadOnlyDictionary<string, ExmodPackageContents> Exmods,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PakFolders,
    IReadOnlyDictionary<string, string> Ue4ssFolders)
{
    public static readonly PatchBundle Empty = new(
        new Dictionary<string, ExmodPackageContents>(), new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, string>());

    public static PatchBundle Of(IReadOnlyDictionary<string, ExmodPackageContents> exmods) => Empty with { Exmods = exmods };

    public bool IsEmpty => Exmods.Count == 0 && PakFolders.Count == 0 && Ue4ssFolders.Count == 0;
}

public interface IPatchService
{
    /// <summary>
    /// Writes a patch to outputPath — a plain indented-JSON manifest if the bundle is empty (every
    /// mod is expected to be available elsewhere: Nexus, the community catalog), or a zip
    /// containing manifest.json plus one real, standalone .EXMODZ entry per bundled EXMOD
    /// (Bundled/&lt;FolderName&gt;.EXMODZ), the pak files of each bundled pak mod
    /// (BundledPaks/&lt;FolderName&gt;/&lt;file&gt;.pak) and the files of each bundled UE4SS mod
    /// (BundledUe4ss/&lt;Name&gt;/…).
    /// </summary>
    Task ExportAsync(PatchManifest manifest, PatchBundle bundle, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a patch written by <see cref="ExportAsync"/> — sniffed by content (zip magic bytes),
    /// not by file extension, since this is a file a user picked by hand and may have renamed.
    /// Bundled EXMODs come back in memory; bundled paks and UE4SS mods (which can be large) are
    /// extracted under a temp folder the result owns: dispose it when the import is done. Throws
    /// FormatException on a corrupt or incomplete patch (missing manifest.json, a bundled entry
    /// with no matching file, oversized entries, an entry path that escapes its folder) — this
    /// file crossed a real trust boundary (shared by a friend, downloaded from a server), same
    /// class of untrusted input as an EXMODZ import.
    /// </summary>
    Task<PatchImportContents> ImportAsync(string patchFilePath, CancellationToken cancellationToken = default);
}

/// <summary>What a patch held. <see cref="Dispose"/> removes the temp folder the bundled paks and UE4SS mods were extracted to.</summary>
public sealed class PatchImportContents : IDisposable
{
    public PatchImportContents(
        PatchManifest manifest, IReadOnlyDictionary<string, ExmodPackageContents> bundledMods,
        IReadOnlyDictionary<string, string>? bundledPakFolders = null, IReadOnlyDictionary<string, string>? bundledUe4ssFolders = null,
        string? extractedRoot = null)
    {
        Manifest = manifest;
        BundledMods = bundledMods;
        BundledPakFolders = bundledPakFolders ?? new Dictionary<string, string>();
        BundledUe4ssFolders = bundledUe4ssFolders ?? new Dictionary<string, string>();
        ExtractedRoot = extractedRoot;
    }

    public PatchManifest Manifest { get; }

    /// <summary>Bundled EXMOD mods, by the manifest's FolderName.</summary>
    public IReadOnlyDictionary<string, ExmodPackageContents> BundledMods { get; }

    /// <summary>Bundled pak mods, by the manifest's FolderName: a folder holding their pak files.</summary>
    public IReadOnlyDictionary<string, string> BundledPakFolders { get; }

    /// <summary>Bundled UE4SS mods, by name: the extracted mod folder (ready for IUe4ssModRepository.ImportFromFolder's parent-folder shape).</summary>
    public IReadOnlyDictionary<string, string> BundledUe4ssFolders { get; }

    /// <summary>The temp folder the bundled files were extracted to; null when nothing was.</summary>
    public string? ExtractedRoot { get; }

    public void Dispose()
    {
        if (ExtractedRoot is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(ExtractedRoot))
            {
                Directory.Delete(ExtractedRoot, recursive: true);
            }
        }
        catch (Exception)
        {
            // Scratch space in %TEMP%; a locked leftover isn't worth failing an import over.
        }
    }
}
