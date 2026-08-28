using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Import;

/// <summary>
/// Registers an already-built EXMOD package as a new library entry — the counterpart to
/// ILibraryRepository.Import(string, ...) for a caller that already has an in-memory
/// ExmodPackageContents rather than a folder/zip to read one from. Declared here rather than on
/// ILibraryRepository itself (Core) because ExmodPackageContents is a PakIO type and Core can't
/// reference PakIO — PakIO already depends on Core, so the dependency runs the other way (same
/// reasoning ILibraryRepository.GetFolderPath's own doc comment already gives). The Storage-layer
/// ILibraryRepository implementation also implements this interface, registered under both in DI.
///
/// Used by the prebuilt-pak-to-EXMOD conversion path (IPrebuiltPakToExmodConverter): a
/// successfully converted pak becomes a real, editable EXMOD entry (IsOpaquePak false) instead of
/// an opaque one.
/// </summary>
public interface IExmodPackageImporter
{
    /// <summary>source/nexusModId/catalogEntryId are the same provenance tags ILibraryRepository.Import(string, ...) takes.</summary>
    LibraryEntry ImportPackage(ExmodPackageContents contents, string? source = null, int? nexusModId = null, string? catalogEntryId = null);
}
