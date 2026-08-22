using IcarusStarlink.Core.Settings;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Settings;

public sealed class AppSettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettings Current { get; }

    public AppSettingsService(string appDataDirectory, ILogger<AppSettingsService> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _settingsFilePath = Path.Combine(appDataDirectory, "settings.json");
        Current = JsonFileStore.Load(_settingsFilePath, () => new AppSettings(), _logger);
    }

    public bool Save()
    {
        try
        {
            JsonFileStore.Save(_settingsFilePath, Current);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately broader than Load()'s scoped catch: Save is just serialize+write, with
            // much less surface for a masked programming bug, so catching anything here (disk
            // full, a removable drive unplugged mid-write, etc.) and reporting failure via the
            // bool return is the safer default.
            _logger.LogWarning(ex, "Failed to save settings to {Path}", _settingsFilePath);
            return false;
        }
    }
}
