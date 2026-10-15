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
        _entries = JsonFileStore.LoadList<PendingDownloadEntry>(_filePath, _logger);
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

    public void SetActivation(int modId, int fileId, string? folderName, PendingDownloadActivationKind? kind)
    {
        var entry = _entries.FirstOrDefault(e => e.ModId == modId && e.FileId == fileId);
        if (entry is null)
        {
            return;
        }

        entry.ActivatedFolderName = folderName;
        entry.ActivatedKind = kind;
        entry.ActivatedItems = folderName is not null && kind is { } k ? [new PendingDownloadActivatedItem(folderName, k)] : [];
        Save();
    }

    public void SetActivation(int modId, int fileId, IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<string> requirements)
    {
        var entry = _entries.FirstOrDefault(e => e.ModId == modId && e.FileId == fileId);
        if (entry is null)
        {
            return;
        }

        entry.ActivatedItems = [.. items];
        entry.ActivatedFolderName = items.Count > 0 ? items[0].FolderName : null;
        entry.ActivatedKind = items.Count > 0 ? items[0].Kind : null;
        entry.Requirements = [.. requirements];
        Save();
    }

    private void Save() => JsonFileStore.Save(_filePath, _entries);
}
