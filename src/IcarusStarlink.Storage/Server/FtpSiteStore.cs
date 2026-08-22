using System.Text.Json;
using IcarusStarlink.Core.Server;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Server;

public sealed class FtpSiteStore : IFtpSiteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<FtpSiteStore> _logger;
    private readonly List<FtpSiteProfile> _sites;

    public FtpSiteStore(string appDataDirectory, ILogger<FtpSiteStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "ftp_sites.json");
        _sites = Load();
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

    private List<FtpSiteProfile> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<FtpSiteProfile>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load FTP sites from {Path}; starting empty", _filePath);
            return [];
        }
    }

    private void SaveToDisk()
    {
        var json = JsonSerializer.Serialize(_sites, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
