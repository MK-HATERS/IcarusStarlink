using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Services;

public static class SettingsServiceExtensions
{
    /// <summary>
    /// For a switch that's saved the moment it changes (not by Save settings): sets it and saves,
    /// or does nothing when it already has that value. True when it changed, so the caller can
    /// raise its own property change. Six places wrote this check-set-save by hand.
    /// </summary>
    public static bool SetAndSave<T>(this ISettingsService settings, Func<AppSettings, T> get, Action<AppSettings, T> set, T value)
    {
        if (EqualityComparer<T>.Default.Equals(get(settings.Current), value))
        {
            return false;
        }

        set(settings.Current, value);
        settings.Save();
        return true;
    }
}
