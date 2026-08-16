namespace IcarusStarlink.Core.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <returns>False if the settings file could not be written (e.g. locked or read-only path).</returns>
    bool Save();
}
