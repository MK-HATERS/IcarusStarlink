using IcarusStarlink.Core.Server;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Server;

/// <summary>
/// The saved FTP sites, one JSON list next to the app's other data. Every save rewrites the whole
/// file from memory, so a file that couldn't be read at startup is kept aside before the first
/// save (see JsonFileStore.KeepUnreadableOriginal) instead of being replaced by a shorter list.
/// </summary>
public sealed class FtpSiteStore : IFtpSiteStore
{
    private readonly string _filePath;
    private readonly ILogger<FtpSiteStore> _logger;
    private readonly List<FtpSiteProfile> _sites;
    private ListLoadProblem _loadProblem;

    public FtpSiteStore(string appDataDirectory, ILogger<FtpSiteStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "ftp_sites.json");
        _sites = JsonFileStore.LoadList<FtpSiteProfile>(_filePath, _logger, out _loadProblem);
    }

    public IReadOnlyList<FtpSiteProfile> GetAll() => _sites;

    public void Save(FtpSiteProfile site)
    {
        _sites.RemoveAll(s => s.Id == site.Id);
        _sites.Add(site);
        SaveToDisk();
    }

    public void Delete(Guid id)
    {
        _sites.RemoveAll(s => s.Id == id);
        SaveToDisk();
    }

    private void SaveToDisk()
    {
        JsonFileStore.KeepUnreadableOriginal(_filePath, ref _loadProblem, _logger);
        JsonFileStore.Save(_filePath, _sites);
    }
}
