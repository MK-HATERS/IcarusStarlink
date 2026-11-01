using System.IO;

namespace IcarusStarlink.App.Services;

/// <summary>
/// "IcarusStarlink.exe --import &lt;zip or folder&gt;": a mod author's own build, installed the way
/// an update is (DownloadsViewModel.ImportOwnBuildAsync). A second launch hands it to the running
/// Starlink over the same pipe as an nxm:// link, as one line: "import|&lt;full path&gt;".
/// </summary>
public static class OwnBuildImport
{
    public const string Argument = "--import";

    private const string Prefix = "import|";

    /// <summary>The full path after "--import" on the command line, or null when there is none.</summary>
    public static string? PathIn(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], Argument, StringComparison.OrdinalIgnoreCase) && args[i + 1].Trim() is { Length: > 0 } path)
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>The pipe message for a build at <paramref name="fullPath"/> (one line: a path can't hold a line break).</summary>
    public static string Message(string fullPath) => Prefix + fullPath;

    /// <summary>The build's path when <paramref name="message"/> is an import, else null (an nxm:// link).</summary>
    public static string? PathOf(string message) =>
        message.StartsWith(Prefix, StringComparison.Ordinal) && message.Length > Prefix.Length ? message[Prefix.Length..] : null;
}
