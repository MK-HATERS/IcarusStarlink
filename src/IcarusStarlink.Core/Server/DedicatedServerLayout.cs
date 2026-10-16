namespace IcarusStarlink.Core.Server;

/// <summary>
/// Where things live in a dedicated server install, per the official setup wiki: IcarusServer.exe
/// at the install root, everything the server writes under Icarus\Saved (or Saved + suffix when
/// launched with -saveddirsuffix), the settings at Saved\Config\WindowsServer\ServerSettings.ini,
/// logs under Saved\Logs, prospects under Saved\PlayerData\DedicatedServer\Prospects.
/// </summary>
public static class DedicatedServerLayout
{
    public const string LauncherExeName = "IcarusServer.exe";

    /// <summary>The real server process the launcher starts (Unreal's usual launcher + -Win64-Shipping split).</summary>
    public const string ShippingProcessName = "IcarusServer-Win64-Shipping";

    public const string LauncherProcessName = "IcarusServer";

    public static string LauncherPath(string installFolder) => Path.Combine(installFolder, LauncherExeName);

    public static string SavedFolder(string installFolder, string? savedDirSuffix) =>
        Path.Combine(installFolder, "Icarus", "Saved" + (savedDirSuffix ?? "").Trim());

    public static string SettingsIniPath(string installFolder, string? savedDirSuffix) =>
        Path.Combine(SavedFolder(installFolder, savedDirSuffix), "Config", "WindowsServer", "ServerSettings.ini");

    public static string LogsFolder(string installFolder, string? savedDirSuffix) =>
        Path.Combine(SavedFolder(installFolder, savedDirSuffix), "Logs");

    public static string LogPath(string installFolder, string? savedDirSuffix, string? logFileName) =>
        Path.Combine(LogsFolder(installFolder, savedDirSuffix), string.IsNullOrWhiteSpace(logFileName) ? DedicatedServerDefaults.DefaultLogFileName : logFileName.Trim());

    public static string ProspectsFolder(string installFolder, string? savedDirSuffix) =>
        Path.Combine(SavedFolder(installFolder, savedDirSuffix), "PlayerData", "DedicatedServer", "Prospects");

    /// <summary>The server's own pak mods folder (lowercase "mods", as the game requires).</summary>
    public static string PakModsFolder(string installFolder) => Path.Combine(installFolder, "Icarus", "Content", "Paks", "mods");

    /// <summary>Steam64 ids, one per line, of players who are admin without /AdminLogin (official wiki: Icarus/Saved/AdminWhitelist.txt). Known side effect per the wiki: it breaks the lobby's prospect Delete button.</summary>
    public static string AdminWhitelistPath(string installFolder, string? savedDirSuffix) =>
        Path.Combine(SavedFolder(installFolder, savedDirSuffix), "AdminWhitelist.txt");

    /// <summary>The server's saved ban list (BanPlayer / UnbanPlayer edit it; official wiki: Icarus/Saved/PlayerBanList.txt).</summary>
    public static string BanListPath(string installFolder, string? savedDirSuffix) =>
        Path.Combine(SavedFolder(installFolder, savedDirSuffix), "PlayerBanList.txt");

    public static string PlayerDataFolder(string installFolder, string? savedDirSuffix) =>
        Path.Combine(SavedFolder(installFolder, savedDirSuffix), "PlayerData");

    public static string Win64Folder(string installFolder) => Path.Combine(installFolder, "Icarus", "Binaries", "Win64");

    /// <summary>The exe that actually listens on the ports, which is what firewall rules must name.</summary>
    public static string ShippingExePath(string installFolder) => Path.Combine(Win64Folder(installFolder), ShippingProcessName + ".exe");

    /// <summary>The server's UE4SS mods folder — the same layout as the game client's (UE4SS 3.x: Binaries\Win64\ue4ss\Mods). Verified on a real server: UE4SS 3.0.1 loads on IcarusServer-Win64-Shipping.exe through dwmapi.dll.</summary>
    public static string Ue4ssModsFolder(string installFolder) => Path.Combine(Win64Folder(installFolder), "ue4ss", "Mods");

    public static bool IsUe4ssInstalled(string installFolder) =>
        File.Exists(Path.Combine(Win64Folder(installFolder), "dwmapi.dll")) && File.Exists(Path.Combine(Win64Folder(installFolder), "ue4ss", "UE4SS.dll"));

    /// <summary>SteamCMD writes this next to the install when it installed it there.</summary>
    public static string AppManifestPath(string installFolder) =>
        Path.Combine(installFolder, "steamapps", $"appmanifest_{DedicatedServerDefaults.SteamAppId}.acf");

    /// <summary>
    /// The install root for a folder the user picked: the folder itself, or the nearest of up to
    /// four parents, that holds IcarusServer.exe — people often pick the Icarus or Binaries\Win64
    /// subfolder. Null when none does.
    /// </summary>
    public static string? FindInstallRoot(string pickedFolder)
    {
        var current = new DirectoryInfo(pickedFolder);
        for (var depth = 0; depth <= 4 && current is not null; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, LauncherExeName)))
            {
                return current.FullName;
            }
        }

        return null;
    }

    /// <summary>Prospect save names (file names without .json) in the profile's Prospects folder, newest first; empty when there are none yet.</summary>
    public static IReadOnlyList<string> ListProspects(string installFolder, string? savedDirSuffix)
    {
        var folder = ProspectsFolder(installFolder, savedDirSuffix);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. new DirectoryInfo(folder).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))];
    }
}
