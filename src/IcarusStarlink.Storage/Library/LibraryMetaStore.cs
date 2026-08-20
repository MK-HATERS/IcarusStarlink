using System.Text.Json;
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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LibraryMeta Load(string folderName)
    {
        var path = GetPath(folderName);
        if (!File.Exists(path))
        {
            return new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow };
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LibraryMeta>(json, JsonOptions) ?? new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // IOException/UnauthorizedAccessException (sidecar transiently locked or
            // permission-denied) matter here as much as JsonException: RescanAll's own catch
            // covers both, but if this method let either propagate, the *whole mod* — whose own
            // .EXMOD package is perfectly readable — would vanish from the Library instead of
            // just falling back to default pin/favorite/notes for this one entry.
            logger.LogWarning(ex, "Failed to load library metadata from {Path}; falling back to defaults", path);
            return new LibraryMeta { ImportedAtUtc = DateTimeOffset.UtcNow };
        }
    }

    public void Save(string folderName, LibraryMeta meta)
    {
        Directory.CreateDirectory(metaDirectory);
        File.WriteAllText(GetPath(folderName), JsonSerializer.Serialize(meta, JsonOptions));
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
