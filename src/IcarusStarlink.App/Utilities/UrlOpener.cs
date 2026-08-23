using System.Diagnostics;

namespace IcarusStarlink.App.Utilities;

/// <summary>Best-effort "open in the OS's default handler" (browser for a URL, Explorer for a folder, default app for a file) — previously reimplemented independently at ten call sites across this project. Returns the exception on failure (null on success) rather than a bare bool, since some callers show it in a status message and some just swallow it silently as pure best-effort UX.</summary>
internal static class UrlOpener
{
    public static Exception? TryOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
