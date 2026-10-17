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

    /// <summary>The -saveddirsuffix= value on a running server's command line ("" when it has none), quoted or not, any case.</summary>
    public static string ReadSavedDirSuffix(string commandLine)
    {
        const string key = "-saveddirsuffix=";
        var at = commandLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return "";
        }

        var start = at + key.Length;
        if (start < commandLine.Length && commandLine[start] == '"')
        {
            var close = commandLine.IndexOf('"', start + 1);
            return (close < 0 ? commandLine[(start + 1)..] : commandLine[(start + 1)..close]).Trim();
        }

        var end = commandLine.IndexOfAny([' ', '\t', '"'], start);
        return (end < 0 ? commandLine[start..] : commandLine[start..end]).Trim();
    }

    /// <summary>
    /// Several profiles can share one install with different Saved suffixes: a process belongs to
    /// this profile when its command line carries the profile's suffix (or none, for a profile
    /// without one). Null — the command line couldn't be read — can't tell them apart, so it counts.
    /// </summary>
    public static bool IsThisProfilesProcess(string? commandLine, DedicatedServerProfile profile) =>
        commandLine is null
        || string.Equals(ReadSavedDirSuffix(commandLine), (profile.SavedDirSuffix ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Quotes can't be escaped inside a Windows command-line argument the engine parses, and the name is capped at 64 characters by the game.</summary>
    public static string Sanitize(string? serverName)
    {
        var cleaned = (serverName ?? "").Replace("\"", "").Trim();
        return cleaned.Length > DedicatedServerDefaults.MaxServerNameLength ? cleaned[..DedicatedServerDefaults.MaxServerNameLength] : cleaned;
    }
}
