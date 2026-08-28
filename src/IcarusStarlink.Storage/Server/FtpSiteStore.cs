using IcarusStarlink.Core.Server;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Server;

public sealed class FtpSiteStore : IFtpSiteStore
{
    private readonly string _filePath;
    private readonly ILogger<FtpSiteStore> _logger;
    private readonly List<FtpSiteProfile> _sites;

    public FtpSiteStore(string appDataDirectory, ILogger<FtpSiteStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "ftp_sites.json");
        _sites = JsonFileStore.LoadList<FtpSiteProfile>(_filePath, _logger);
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

    private void SaveToDisk() => JsonFileStore.Save(_filePath, _sites);
}
