namespace IcarusStarlink.Core.Server;

/// <summary>
/// One Icarus dedicated server on this PC: where it is installed and how it is launched. The
/// in-game settings (passwords, max players, prospect, shutdown timers…) are NOT stored here —
/// they live in the server's own ServerSettings.ini, which is the single source of truth the
/// configurator reads and writes (see ServerSettingsIni), so editing the file by hand and editing it
/// here can never disagree. Several profiles can point at the same install with a different
/// SavedDirSuffix and ports (two worlds on one install, as the official docs describe).
/// </summary>
public sealed class DedicatedServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What this app calls the profile ("Friends server").</summary>
    public string Name { get; set; } = "";

    /// <summary>The folder holding IcarusServer.exe (the SteamCMD install folder).</summary>
    public string InstallFolder { get; set; } = "";

    /// <summary>The server-browser name, sent as -SteamServerName (64 characters at most). The ini's SessionName key does nothing, per the official docs.</summary>
    public string SteamServerName { get; set; } = "";

    /// <summary>Game port, UDP. Official default 17777.</summary>
    public int Port { get; set; } = DedicatedServerDefaults.GamePort;

    /// <summary>Steam query port, UDP. Official default 27015.</summary>
    public int QueryPort { get; set; } = DedicatedServerDefaults.QueryPort;

    /// <summary>-MULTIHOME= — bind to one local IP; empty for all.</summary>
    public string Multihome { get; set; } = "";

    /// <summary>-saveddirsuffix= — appended to the "Saved" folder name so several servers can share one install. Empty for the default Saved folder.</summary>
    public string SavedDirSuffix { get; set; } = "";

    /// <summary>-LOG= — the log file name under Saved\Logs. Empty means the engine default (Icarus.log).</summary>
    public string LogFileName { get; set; } = "";

    /// <summary>Steam branch the install tracks: "public" or "experimental" (players on Experimental can only join an experimental server).</summary>
    public string Branch { get; set; } = "public";
}

public static class DedicatedServerDefaults
{
    public const int GamePort = 17777;
    public const int QueryPort = 27015;
    public const int SteamAppId = 2089300;
    public const string DefaultLogFileName = "Icarus.log";
    public const int MaxServerNameLength = 64;
    public const int MinPlayers = 1;
    public const int MaxPlayers = 8;
}
