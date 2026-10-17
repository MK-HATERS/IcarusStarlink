using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.Services;

/// <summary>
/// "Is the extracted game data still the game's?" for Merge &amp; Install. The merge writes whole
/// tables from the extracted Data folder, so merging after a game update but before Refresh game
/// data puts the old version of every table a mod touches back into the game. A seam so the
/// rebuild's guard can be tested without hashing a real data.pak.
/// </summary>
public interface IGameDataFreshness
{
    /// <summary>True when the game's data.pak differs from the one the last Refresh game data extracted. False when it can't tell (no hash recorded yet, data.pak unreadable).</summary>
    Task<bool> IsGameDataOutdatedAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs Refresh game data; true when it finished and the extracted data now matches the game.</summary>
    Task<bool> RefreshGameDataAsync();
}

/// <summary>Compares data.pak's hash with AppSettings.LastDataPakHash, the same check Settings runs at start; the refresh is Settings' own Refresh game data.</summary>
public sealed class GameDataFreshness(IUnrealPakService unrealPakService, ISettingsService settingsService, Func<SettingsViewModel> settingsViewModel) : IGameDataFreshness
{
    public async Task<bool> IsGameDataOutdatedAsync(CancellationToken cancellationToken = default)
    {
        if (settingsService.Current.IcarusContentPath is not { Length: > 0 } contentPath
            || settingsService.Current.LastDataPakHash is not { } lastKnownHash)
        {
            return false;
        }

        var currentHash = await unrealPakService.TryGetDataPakHashAsync(contentPath, cancellationToken);
        return currentHash is not null && !string.Equals(currentHash, lastKnownHash, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> RefreshGameDataAsync()
    {
        await settingsViewModel().RefreshGameDataAsync();
        return !await IsGameDataOutdatedAsync();
    }
}
