using System.Text.Json;
using IcarusStarlink.Core.Settings;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Settings;

public sealed class AppSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsFilePath;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettings Current { get; }

    public AppSettingsService(string appDataDirectory, ILogger<AppSettingsService> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _settingsFilePath = Path.Combine(appDataDirectory, "settings.json");
        Current = Load();
    }

    private AppSettings Load()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}; falling back to defaults", _settingsFilePath);
            return new AppSettings();
        }
    }

    public bool Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(_settingsFilePath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save settings to {Path}", _settingsFilePath);
            return false;
        }
    }
}
