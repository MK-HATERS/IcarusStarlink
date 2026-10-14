using IcarusStarlink.Core.Settings;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Settings;

public sealed class AppSettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private readonly ILogger<AppSettingsService> _logger;

    // True when settings.json EXISTED but couldn't be read at startup (see JsonFileStore.Load):
    // Current holds defaults the user never chose, and the first Save would overwrite their real
    // file with them (an audit finding — a OneDrive/antivirus lock at launch wiped every setting).
    // The original is preserved beside the file before that first write so nothing is lost.
    private bool _originalCouldNotBeRead;

    public AppSettings Current { get; }

    public AppSettingsService(string appDataDirectory, ILogger<AppSettingsService> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _settingsFilePath = Path.Combine(appDataDirectory, "settings.json");
        Current = JsonFileStore.Load(_settingsFilePath, () => new AppSettings(), _logger, out _originalCouldNotBeRead);
    }

    public bool Save()
    {
        try
        {
            if (_originalCouldNotBeRead && File.Exists(_settingsFilePath))
            {
                var preserved = $"{_settingsFilePath}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
                File.Copy(_settingsFilePath, preserved, overwrite: true);
                _logger.LogWarning("settings.json couldn't be read at startup; the original was preserved at {Path} before saving over it", preserved);
                _originalCouldNotBeRead = false;
            }

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
