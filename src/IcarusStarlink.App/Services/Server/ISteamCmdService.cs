namespace IcarusStarlink.App.Services;

public sealed record SteamCmdResult(bool Success, string Message);

/// <summary>Runs SteamCMD for the dedicated server. An interface so the view model's tests never download or run anything.</summary>
public interface ISteamCmdService
{
    /// <summary>The steamcmd.exe in use: the one chosen in settings when it exists, else Starlink's own copy; null when neither is there yet.</summary>
    string? CurrentExePath { get; }

    /// <summary>Where "Install SteamCMD" puts Starlink's own copy (a SteamCMD folder in Starlink's own folder).</summary>
    string OwnInstallFolder { get; }

    /// <summary>Downloads SteamCMD from Valve into OwnInstallFolder and lets it update itself. Throws on a download failure.</summary>
    Task InstallOwnCopyAsync(Action<string> onLine, CancellationToken cancellationToken = default);

    /// <summary>Installs (or updates, or switches branch of) the server into <paramref name="installFolder"/>. Every output line goes to <paramref name="onLine"/>. Throws when no SteamCMD is set up.</summary>
    Task<SteamCmdResult> InstallOrUpdateAsync(string installFolder, string branch, Action<string> onLine, CancellationToken cancellationToken = default);

    /// <summary>The newest build id Steam lists for the branch, or null when SteamCMD couldn't tell. Throws when no SteamCMD is set up.</summary>
    Task<int?> GetLatestBuildIdAsync(string branch, CancellationToken cancellationToken = default);
}
