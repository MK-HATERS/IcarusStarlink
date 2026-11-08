using System.IO;

namespace IcarusStarlink.App.Services;

/// <summary>
/// "IcarusStarlink.exe --import &lt;zip or folder&gt;": a mod author's own build, installed the way
/// an update is (DownloadsViewModel.ImportOwnBuildAsync). A second launch hands it to the running
/// Starlink over the same pipe as an nxm:// link, as one line: "import|&lt;full path&gt;".
/// "--import" counts only as the first argument: the build is installed with no question, and
/// Windows starts Starlink as "exe" "%1" for every nxm:// link, so a link split by a raw quote
/// must never be read as an import.
/// </summary>
public static class OwnBuildImport
{
    public const string Argument = "--import";

    private const string Prefix = "import|";

    /// <summary>The full path after a leading "--import" on the command line, or null when there is none.</summary>
    public static string? PathIn(IReadOnlyList<string> args)
    {
        // An nxm:// launch ("exe" "%1") only ever carries a link: anything after a quote inside it is not an import.
        if (args.Any(arg => arg.TrimStart().StartsWith("nxm:", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // Only the documented form: IcarusStarlink.exe --import <zip or folder>.
        if (args.Count < 2 || !string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase) || args[1].Trim() is not { Length: > 0 } path)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The pipe message for a build at <paramref name="fullPath"/> (one line: a path can't hold a line break).</summary>
    public static string Message(string fullPath) => Prefix + fullPath;

    /// <summary>The build's path when <paramref name="message"/> is an import, else null (an nxm:// link).</summary>
    public static string? PathOf(string message) =>
        message.StartsWith(Prefix, StringComparison.Ordinal) && message.Length > Prefix.Length ? message[Prefix.Length..] : null;
}
