using System.Text.Json;
using IcarusStarlink.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Catalog;

public sealed class NexusWatchlistStore : INexusWatchlistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<NexusWatchlistStore> _logger;
    private readonly List<NexusWatchlistEntry> _entries;

    public IReadOnlyList<NexusWatchlistEntry> Entries => _entries;

    public NexusWatchlistStore(string appDataDirectory, ILogger<NexusWatchlistStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "nexus_watchlist.json");
        _entries = Load();
    }

    public void Add(NexusWatchlistEntry entry)
    {
        _entries.RemoveAll(e => e.NexusId == entry.NexusId);
        _entries.Add(entry);
        Save();
    }

    public void Remove(int nexusId)
    {
        _entries.RemoveAll(e => e.NexusId == nexusId);
        Save();
    }

    public void UpdateName(int nexusId, string name)
    {
        var entry = _entries.Find(e => e.NexusId == nexusId);
        if (entry is not null)
        {
            entry.Name = name;
            Save();
        }
    }

    private List<NexusWatchlistEntry> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<NexusWatchlistEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load Nexus watchlist from {Path}; starting empty", _filePath);
            return [];
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
