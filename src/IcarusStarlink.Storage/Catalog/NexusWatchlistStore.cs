using IcarusStarlink.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Catalog;

/// <summary>The Nexus watchlist, one JSON list next to the app's other data — same unreadable-file guard as FtpSiteStore.</summary>
public sealed class NexusWatchlistStore : INexusWatchlistStore
{
    private readonly string _filePath;
    private readonly ILogger<NexusWatchlistStore> _logger;
    private readonly List<NexusWatchlistEntry> _entries;
    private ListLoadProblem _loadProblem;

    public IReadOnlyList<NexusWatchlistEntry> Entries => _entries;

    public NexusWatchlistStore(string appDataDirectory, ILogger<NexusWatchlistStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "nexus_watchlist.json");
        _entries = JsonFileStore.LoadList<NexusWatchlistEntry>(_filePath, _logger, out _loadProblem);
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

    private void Save()
    {
        JsonFileStore.KeepUnreadableOriginal(_filePath, ref _loadProblem, _logger);
        JsonFileStore.Save(_filePath, _entries);
    }
}
