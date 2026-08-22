using IcarusStarlink.Core.Nexus;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Nexus;

public sealed class PendingDownloadStore : IPendingDownloadStore
{
    private readonly string _filePath;
    private readonly ILogger<PendingDownloadStore> _logger;
    private readonly List<PendingDownloadEntry> _entries;

    public IReadOnlyList<PendingDownloadEntry> Entries => _entries;

    public PendingDownloadStore(string appDataDirectory, ILogger<PendingDownloadStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "pending_downloads.json");
        _entries = JsonFileStore.Load(_filePath, () => new List<PendingDownloadEntry>(), _logger);
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

    private void Save() => JsonFileStore.Save(_filePath, _entries);
}
