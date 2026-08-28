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

    /// <summary>
    /// List-shaped counterpart to Load&lt;T&gt; — used by FtpSiteStore/PendingDownloadStore/
    /// NexusWatchlistStore, each backed by a JSON array of independent records. Load&lt;T&gt;
    /// deserializes the whole file as one JsonSerializer.Deserialize&lt;List&lt;TItem&gt;&gt; call,
    /// so a single malformed element (a future schema change, a hand-edit, a record written by a
    /// build mid-migration) throws and silently discards every OTHER, perfectly-valid saved site/
    /// download/watchlist entry along with it. This instead parses the array structurally first,
    /// then deserializes element-by-element, skipping (and logging) only the one that fails.
    /// </summary>
    public static List<TItem> LoadList<TItem>(string filePath, ILogger logger)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            root = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to load {Path}; falling back to an empty list", filePath);
            return [];
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("Expected a JSON array in {Path} but found {Kind}; falling back to an empty list", filePath, root.ValueKind);
            return [];
        }

        var items = new List<TItem>();
        var index = 0;
        foreach (var element in root.EnumerateArray())
        {
            try
            {
                var item = element.Deserialize<TItem>(Options);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            catch (JsonException ex)
            {
                // The whole point of this method over Load<List<TItem>> — one bad record is
                // skipped and logged, not a reason to throw away every other valid one.
                logger.LogWarning(ex, "Skipped a malformed entry at index {Index} in {Path}", index, filePath);
            }

            index++;
        }

        return items;
    }

    /// <summary>Serializes value with the shared Options, then writes it via WriteAtomically.</summary>
    public static void Save<T>(string filePath, T value) => WriteAtomically(filePath, JsonSerializer.Serialize(value, Options));

    /// <summary>
    /// Writes to a temp file in the same directory, then File.Move(overwrite: true)'s it into
    /// place — not a direct File.WriteAllText, which truncates filePath in place and would leave it
    /// malformed if the process is killed mid-write (a crash, forced shutdown, or power loss). The
    /// temp file's own name is unique per call so two concurrent writes to the same path can't
    /// collide with each other's in-progress write. Public (not just used by Save&lt;T&gt;) so a
    /// caller writing pre-serialized or non-JSON content — IcarusStarlink.Storage.Saves.SaveRepository
    /// writes the game's own tab/CRLF JSON style plus one raw binary file — gets the same
    /// crash-safety guarantee without reimplementing this from scratch.
    /// </summary>
    public static void WriteAtomically(string filePath, string content)
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);
    }

    /// <summary>Byte-content counterpart to WriteAtomically, for a caller writing something that isn't text (e.g. a raw binary file).</summary>
    public static void WriteBytesAtomically(string filePath, byte[] bytes)
    {
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, filePath, overwrite: true);
    }
}
