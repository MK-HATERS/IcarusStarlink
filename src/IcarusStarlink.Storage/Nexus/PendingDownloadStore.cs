using System.Text.Json;
using IcarusStarlink.Core.Nexus;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Nexus;

public sealed class PendingDownloadStore : IPendingDownloadStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<PendingDownloadStore> _logger;
    private readonly List<PendingDownloadEntry> _entries;

    public IReadOnlyList<PendingDownloadEntry> Entries => _entries;

    public PendingDownloadStore(string appDataDirectory, ILogger<PendingDownloadStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "pending_downloads.json");
        _entries = Load();
    }

    public void Add(PendingDownloadEntry entry)
    {
        _entries.RemoveAll(e => e.ModId == entry.ModId && e.FileId == entry.FileId);
        _entries.Add(entry);
        Save();
    }

    public void Remove(int modId, int fileId)
    {
        _entries.RemoveAll(e => e.ModId == modId && e.FileId == fileId);
        Save();
    }

    private List<PendingDownloadEntry> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<PendingDownloadEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load pending downloads from {Path}; starting empty", _filePath);
            return [];
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
