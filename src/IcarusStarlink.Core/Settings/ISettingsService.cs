namespace IcarusStarlink.Core.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }

    /// <returns>False if the settings file could not be written (e.g. locked or read-only path).</returns>
    bool Save();

    /// <summary>Why settings.json couldn't be used at startup, in words for the player; null when it loaded (or didn't exist yet). While set, Current holds defaults the player never chose.</summary>
    string? LoadProblem => null;
}
