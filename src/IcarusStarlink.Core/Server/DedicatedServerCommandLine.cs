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
            args.Add($"-MULTIHOME={QuoteIfSpaced(profile.Multihome.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(profile.SavedDirSuffix))
        {
            args.Add($"-saveddirsuffix={QuoteIfSpaced(profile.SavedDirSuffix.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(profile.LogFileName))
        {
            args.Add($"-LOG={QuoteIfSpaced(profile.LogFileName.Trim())}");
        }

        return args;
    }

    public static string BuildArgumentString(DedicatedServerProfile profile) => string.Join(" ", BuildArguments(profile));

    /// <summary>The whole line as a person would type it (the exe quoted) — for "Copy command line" and a RunServer.bat.</summary>
    public static string BuildDisplayLine(DedicatedServerProfile profile) =>
        $"\"{DedicatedServerLayout.LauncherPath(profile.InstallFolder)}\" {BuildArgumentString(profile)}";

    /// <summary>The -saveddirsuffix= value on a running server's command line ("" when it has none), quoted or not, any case.</summary>
    public static string ReadSavedDirSuffix(string commandLine) => ReadValue(commandLine, "-saveddirsuffix=") ?? "";

    /// <summary>The -LOG= file name on a running server's command line; null when it has none (the plain -log switch only opens the console).</summary>
    public static string? ReadLogFileName(string commandLine) => ReadValue(commandLine, "-LOG=") is { Length: > 0 } name ? name : null;

    /// <summary>The -PORT= game port on a running server's command line; null when it has none or it isn't a port number.</summary>
    public static int? ReadPort(string commandLine) => ReadPortValue(commandLine, "-PORT=");

    /// <summary>The -QueryPort= on a running server's command line; null when it has none or it isn't a port number.</summary>
    public static int? ReadQueryPort(string commandLine) => ReadPortValue(commandLine, "-QueryPort=");

    /// <summary>Where a running server saves and which ports it uses, from its own command line.</summary>
    public static DedicatedServerLaunchOptions ReadLaunchOptions(string commandLine) =>
        new(ReadSavedDirSuffix(commandLine), ReadLogFileName(commandLine), ReadPort(commandLine), ReadQueryPort(commandLine));

    private static int? ReadPortValue(string commandLine, string key) =>
        int.TryParse(ReadValue(commandLine, key), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;

    /// <summary>
    /// The value of <paramref name="key"/> ("-LOG=") quoted or not, any case; null when the line
    /// doesn't have it. The key counts only at the start of an argument, not inside another
    /// argument's value.
    /// </summary>
    private static string? ReadValue(string commandLine, string key)
    {
        var at = commandLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        while (at > 0 && !char.IsWhiteSpace(commandLine[at - 1]) && commandLine[at - 1] != '"')
        {
            at = commandLine.IndexOf(key, at + 1, StringComparison.OrdinalIgnoreCase);
        }

        if (at < 0)
        {
            return null;
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

    /// <summary>
    /// A value with a space is quoted the way -SteamServerName= is: unquoted, "-saveddirsuffix=my
    /// world" reached the server as "my" plus a stray argument, so it saved to the wrong folder and
    /// ReadSavedDirSuffix no longer recognised the running server as this profile's.
    /// </summary>
    private static string QuoteIfSpaced(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "")}\"" : value;

    /// <summary>Quotes can't be escaped inside a Windows command-line argument the engine parses, and the name is capped at 64 characters by the game.</summary>
    public static string Sanitize(string? serverName)
    {
        var cleaned = (serverName ?? "").Replace("\"", "").Trim();
        return cleaned.Length > DedicatedServerDefaults.MaxServerNameLength ? cleaned[..DedicatedServerDefaults.MaxServerNameLength] : cleaned;
    }
}

/// <summary>
/// A running server's own launch options: its Saved folder suffix ("" for none), -LOG= file name
/// and ports, each null when its command line doesn't set it.
/// </summary>
public sealed record DedicatedServerLaunchOptions(string SavedDirSuffix, string? LogFileName, int? Port, int? QueryPort)
{
    /// <summary>Anything a new profile would need that its defaults don't already give.</summary>
    public bool DiffersFromDefaults =>
        SavedDirSuffix.Length > 0
        || (LogFileName is not null && !string.Equals(LogFileName, DedicatedServerDefaults.DefaultLogFileName, StringComparison.OrdinalIgnoreCase))
        || (Port is { } port && port != DedicatedServerDefaults.GamePort)
        || (QueryPort is { } query && query != DedicatedServerDefaults.QueryPort);
}
