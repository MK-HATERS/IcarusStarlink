using IcarusStarlink.Core.Server;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Server;

/// <summary>
/// Same shape as FtpSiteStore: one JSON list next to the app's other data. Every save rewrites the
/// whole file from the in-memory list, so a list that loaded EMPTY because the file couldn't be
/// read must never be saved over it (that wiped every server profile). Mirrors AppSettingsService:
/// a locked/denied file is never written over this session; a corrupt one is kept aside as
/// .unreadable-*.bak before the first save; and the previous file is kept as .bak before each save.
/// </summary>
public sealed class DedicatedServerProfileStore : IDedicatedServerProfileStore
{
    private readonly string _filePath;
    private readonly ILogger<DedicatedServerProfileStore> _logger;
    private readonly List<DedicatedServerProfile> _profiles;
    private ListLoadProblem _problem;

    public DedicatedServerProfileStore(string appDataDirectory, ILogger<DedicatedServerProfileStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "dedicated_servers.json");
        _profiles = JsonFileStore.LoadList<DedicatedServerProfile>(_filePath, logger, out _problem);
        LoadProblem = _problem switch
        {
            ListLoadProblem.Unreadable =>
                $"Your saved server list ({_filePath}) couldn't be read (another program may have it open). Changes to servers won't be saved until Starlink is restarted, so the list isn't overwritten.",
            ListLoadProblem.Corrupt =>
                $"Your saved server list ({_filePath}) is damaged and couldn't be read. A copy of it is kept beside it (.bak) the first time a change is saved.",
            _ => null,
        };
    }

    public string? LoadProblem { get; }

    public IReadOnlyList<DedicatedServerProfile> GetAll() => _profiles;

    public bool Save(DedicatedServerProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            _profiles[index] = profile;
        }
        else
        {
            _profiles.Add(profile);
        }

        return WriteToDisk();
    }

    public bool Delete(Guid id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        return WriteToDisk();
    }

    private bool WriteToDisk()
    {
        if (_problem == ListLoadProblem.Unreadable)
        {
            _logger.LogWarning("Not saving {Path}: it couldn't be read at startup, and saving now would replace it with an incomplete list", _filePath);
            return false;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                if (_problem == ListLoadProblem.Corrupt)
                {
                    var preserved = $"{_filePath}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
                    File.Copy(_filePath, preserved, overwrite: true);
                    _logger.LogWarning("{Path} couldn't be parsed at startup; the original was kept at {Preserved} before saving over it", _filePath, preserved);
                    _problem = ListLoadProblem.None;
                }
                else
                {
                    File.Copy(_filePath, _filePath + ".bak", overwrite: true);
                }
            }

            JsonFileStore.Save(_filePath, _profiles);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save {Path}", _filePath);
            return false;
        }
    }
}
