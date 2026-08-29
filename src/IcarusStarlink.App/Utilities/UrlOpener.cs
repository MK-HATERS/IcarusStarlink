using System.Diagnostics;
using System.IO;

namespace IcarusStarlink.App.Utilities;

/// <summary>Best-effort "open in the OS's default handler" (browser for a URL, Explorer for a folder, default app for a file) — previously reimplemented independently at ten call sites across this project. Returns the exception on failure (null on success) rather than a bare bool, since some callers show it in a status message and some just swallow it silently as pure best-effort UX.</summary>
internal static class UrlOpener
{
    public static Exception? TryOpen(string target)
    {
        try
        {
            // A folder goes through explorer.exe explicitly rather than a bare ShellExecute("open")
            // — confirmed live (bypassing this app entirely, via a raw ProcessStartInfo call) that
            // Windows resolves the "open" verb on a real Steam game's own install folder (e.g.
            // Icarus's own game root) to LAUNCHING THE GAME instead of browsing it, presumably a
            // Steam-registered shell association on that specific folder. explorer.exe's own
            // command-line form (a bare path argument) always opens that folder in a window, with
            // no verb resolution to hijack.
            if (Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\""));
            }
            else
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
