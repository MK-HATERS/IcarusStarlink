using System.IO;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Starlink keeps everything (mods, settings, logs) beside its exe, so it needs a folder the user
/// can write to. Program Files (for a standard user) or a folder under Controlled folder access isn't.
/// </summary>
public static class InstallFolderCheck
{
    /// <summary>Whether Starlink can write in <paramref name="folder"/> (creates it, writes and deletes a probe file).</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".write-check-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
