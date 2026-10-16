namespace IcarusStarlink.App.Services;

public sealed record SteamCmdResult(bool Success, string Message);

/// <summary>Runs SteamCMD for the dedicated server. An interface so the view model's tests never download or run anything.</summary>
public interface ISteamCmdService
{
    /// <summary>Downloads SteamCMD into this app's Tools folder on first use and lets it update itself. Throws on a download failure.</summary>
    Task EnsureInstalledAsync(Action<string> onLine, CancellationToken cancellationToken = default);

    /// <summary>Installs (or updates, or switches branch of) the server into <paramref name="installFolder"/>. Every output line goes to <paramref name="onLine"/>.</summary>
    Task<SteamCmdResult> InstallOrUpdateAsync(string installFolder, string branch, Action<string> onLine, CancellationToken cancellationToken = default);

    /// <summary>The newest build id Steam lists for the branch, or null when SteamCMD couldn't tell.</summary>
    Task<int?> GetLatestBuildIdAsync(string branch, CancellationToken cancellationToken = default);
}
