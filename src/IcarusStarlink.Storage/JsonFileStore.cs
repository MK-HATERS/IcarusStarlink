using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage;

/// <summary>
/// Shared "single JSON file, tolerate corruption by falling back to a default" read/write helper —
/// previously hand-copied (a private JsonSerializerOptions field, Load's try/catch-and-default,
/// Save's serialize-and-write) across ProfileStore, PendingDownloadStore, FtpSiteStore,
/// AppSettingsService, NexusWatchlistStore, and LibraryMetaStore. Each store still owns its own
/// class, constructor, and domain-specific API (Add/Remove/UpdateName/etc.) — this only collapses
/// the file I/O mechanics underneath, not the store's own shape.
/// </summary>
internal static class JsonFileStore
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Reads and deserializes filePath. Returns defaultValue() if the file doesn't exist, comes
    /// back null after deserializing, or fails to read/parse — a missing, empty, corrupt, or
    /// transiently-locked (IOException/UnauthorizedAccessException) file degrades to defaults with
    /// a logged warning rather than throwing and blocking whatever owns this store.
    /// </summary>
    public static T Load<T>(string filePath, Func<T> defaultValue, ILogger logger)
    {
        if (!File.Exists(filePath))
        {
            return defaultValue();
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<T>(json, Options) ?? defaultValue();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to load {Path}; falling back to defaults", filePath);
            return defaultValue();
        }
    }

    public static void Save<T>(string filePath, T value) => File.WriteAllText(filePath, JsonSerializer.Serialize(value, Options));
}
