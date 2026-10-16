using IcarusStarlink.Core.Server;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Server;

/// <summary>Same shape as FtpSiteStore: one JSON list next to the app's other data.</summary>
public sealed class DedicatedServerProfileStore : IDedicatedServerProfileStore
{
    private readonly string _filePath;
    private readonly List<DedicatedServerProfile> _profiles;

    public DedicatedServerProfileStore(string appDataDirectory, ILogger<DedicatedServerProfileStore> logger)
    {
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "dedicated_servers.json");
        _profiles = JsonFileStore.LoadList<DedicatedServerProfile>(_filePath, logger);
    }

    public IReadOnlyList<DedicatedServerProfile> GetAll() => _profiles;

    public void Save(DedicatedServerProfile profile)
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

        JsonFileStore.Save(_filePath, _profiles);
    }

    public void Delete(Guid id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        JsonFileStore.Save(_filePath, _profiles);
    }
}
