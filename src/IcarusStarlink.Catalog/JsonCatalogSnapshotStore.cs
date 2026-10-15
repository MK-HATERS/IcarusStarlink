using System.Text.Json;

namespace IcarusStarlink.Catalog;

/// <summary>One JSON file (Cache\catalog_snapshot.json), written via temp file + rename so a crash mid-write can't leave a half-written snapshot to misread next launch.</summary>
public sealed class JsonCatalogSnapshotStore(string filePath) : ICatalogSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public CatalogSnapshot? TryLoad()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CatalogSnapshot>(File.ReadAllText(filePath), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(CatalogSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, Options));
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cache — see the interface.
        }
    }
}
