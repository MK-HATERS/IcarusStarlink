using IcarusStarlink.Core.Patches;
using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Patches;

public interface IPatchService
{
    /// <summary>
    /// Writes a patch to outputPath — a plain indented-JSON manifest if bundledMods is empty (every
    /// queued mod is expected to be available elsewhere, e.g. the community catalog), or a zip
    /// containing manifest.json plus one real, standalone .EXMODZ entry per bundled mod otherwise.
    /// Which mods end up in bundledMods (and so which PatchModEntry.Bundled is true) is the
    /// caller's decision — this service only ever writes what it's handed.
    /// </summary>
    Task ExportAsync(
        PatchManifest manifest, IReadOnlyDictionary<string, ExmodPackageContents> bundledMods, string outputPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a patch written by <see cref="ExportAsync"/> — sniffed by content (zip magic bytes),
    /// not by file extension, since this is a file a user picked by hand and may have renamed.
    /// Throws FormatException on a corrupt or incomplete patch (missing manifest.json, a
    /// PatchModEntry.Bundled entry with no matching Bundled/&lt;FolderName&gt;.EXMODZ, oversized
    /// entries) — this file crossed a real trust boundary (shared by a friend, downloaded from a
    /// server), same class of untrusted input as an EXMODZ import.
    /// </summary>
    Task<PatchImportContents> ImportAsync(string patchFilePath, CancellationToken cancellationToken = default);
}

public sealed record PatchImportContents(PatchManifest Manifest, IReadOnlyDictionary<string, ExmodPackageContents> BundledMods);
