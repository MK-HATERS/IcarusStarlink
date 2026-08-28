using IcarusStarlink.Core.Library;

namespace IcarusStarlink.PakIO.Import;

/// <summary>
/// The one place every "import a prebuilt .pak into the Library" call site should go through —
/// tries converting it into a real, editable EXMOD first (IPrebuiltPakToExmodConverter), falling
/// back to today's opaque ILibraryRepository.ImportPak whenever conversion isn't possible right
/// now (UnrealPak.exe not set up, the extracted game data missing, or the pak itself can't be
/// read). Centralizing this here means every future import path gets the same behavior for free,
/// instead of each call site needing its own copy of the try-convert-else-fallback logic.
/// </summary>
public interface IPrebuiltPakImporter
{
    /// <summary>
    /// source/nexusModId/catalogEntryId are the same provenance tags ILibraryRepository.Import
    /// already takes. name/author seed the converted package's own declared Name/Author when
    /// conversion succeeds — pass real values when the caller already knows them (a Nexus/catalog
    /// download's own title/uploader); omitted, they default to the pak's own filename and
    /// "Unknown", matching today's opaque-import placeholder values exactly.
    /// </summary>
    Task<LibraryEntry> ImportAsync(
        string pakFilePath, string dataFolder, string? unrealPakExePath,
        string? source = null, int? nexusModId = null, string? catalogEntryId = null,
        string? name = null, string? author = null, CancellationToken cancellationToken = default);
}
