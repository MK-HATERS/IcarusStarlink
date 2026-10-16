using System.Globalization;

namespace IcarusStarlink.Core.Server;

/// <summary>
/// The launch arguments for a profile, using only parameters the official wiki documents:
/// -log (console window), -SteamServerName=, -PORT=, -QueryPort=, -MULTIHOME=, -saveddirsuffix=,
/// -LOG=. Prospect selection is deliberately NOT done on the command line: ServerSettings.ini's
/// LoadProspect/ResumeProspect/CreateProspect keys do the same thing, and keeping them in one place
/// means the ini the configurator shows is exactly what the server does.
/// </summary>
public static class DedicatedServerCommandLine
{
    public static IReadOnlyList<string> BuildArguments(DedicatedServerProfile profile)
    {
        var args = new List<string> { "-log" };

        var name = Sanitize(profile.SteamServerName);
        if (name.Length > 0)
        {
            args.Add($"-SteamServerName=\"{name}\"");
        }

        args.Add($"-PORT={profile.Port.ToString(CultureInfo.InvariantCulture)}");
        args.Add($"-QueryPort={profile.QueryPort.ToString(CultureInfo.InvariantCulture)}");

        if (!string.IsNullOrWhiteSpace(profile.Multihome))
        {
            args.Add($"-MULTIHOME={profile.Multihome.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(profile.SavedDirSuffix))
        {
            args.Add($"-saveddirsuffix={profile.SavedDirSuffix.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(profile.LogFileName))
        {
            args.Add($"-LOG={profile.LogFileName.Trim()}");
        }

        return args;
    }

    public static string BuildArgumentString(DedicatedServerProfile profile) => string.Join(" ", BuildArguments(profile));

    /// <summary>The whole line as a person would type it (the exe quoted) — for "Copy command line" and a RunServer.bat.</summary>
    public static string BuildDisplayLine(DedicatedServerProfile profile) =>
        $"\"{DedicatedServerLayout.LauncherPath(profile.InstallFolder)}\" {BuildArgumentString(profile)}";

    /// <summary>Quotes can't be escaped inside a Windows command-line argument the engine parses, and the name is capped at 64 characters by the game.</summary>
    public static string Sanitize(string? serverName)
    {
        var cleaned = (serverName ?? "").Replace("\"", "").Trim();
        return cleaned.Length > DedicatedServerDefaults.MaxServerNameLength ? cleaned[..DedicatedServerDefaults.MaxServerNameLength] : cleaned;
    }
}
