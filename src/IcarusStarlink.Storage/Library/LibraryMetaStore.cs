using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Library;

/// <summary>
/// Reads/writes per-mod library metadata (pin/favorite/notes) keyed by folder name, in a
/// directory entirely separate from Extracted_Mods. This deliberately does NOT live inside each
/// mod's own folder: PakIO's ExmodFolder treats every file it finds there (other than the
/// .EXMOD itself) as one of the mod's own assets, so a sidecar written inside would show up in
/// the Files tab as if it were part of the mod — and would contaminate the folder if a user
/// re-shared it or re-zipped it into an EXMODZ.
/// </summary>
internal sealed class LibraryMetaStore(string metaDirectory, ILogger logger)
{
    // IOException/UnauthorizedAccessException (sidecar transiently locked or permission-denied)
    // matter here as much as JsonException — JsonFileStore.Load's own catch covers all three, so
    // if this failed and let one propagate, the *whole mod* (whose own .EXMOD package is perfectly
    // readable) would vanish from the Library instead of just falling back to default
    // pin/favorite/notes for this one entry.
    public LibraryMeta Load(string folderName) =>
        JsonFileStore.Load(GetPath(folderName), () => new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow }, logger);

    public void Save(string folderName, LibraryMeta meta)
    {
        Directory.CreateDirectory(metaDirectory);
        JsonFileStore.Save(GetPath(folderName), meta);
    }

    public void Delete(string folderName)
    {
        var path = GetPath(folderName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string folderName) => Path.Combine(metaDirectory, $"{folderName}.json");
}
