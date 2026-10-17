using System.IO;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The note the updater leaves before it relaunches the app (IcarusStarlink.Updater's
/// UpdateOutcomeFile: the outcome's name on the first line, one sentence for the user on the
/// second). Read once on the next start and deleted, so a rolled-back update is reported instead
/// of the old version quietly coming back.
/// </summary>
public sealed record AppUpdateOutcome(bool Installed, string Message)
{
    public const string FileName = "update-outcome.txt";
    public const string TempFileName = "IcarusStarlink-update-outcome.txt";

    /// <summary>The newest note among <paramref name="paths"/> (the Logs folder, then TEMP), or null; every one found is deleted.</summary>
    public static AppUpdateOutcome? TakeLast(params string[] paths)
    {
        AppUpdateOutcome? newest = null;
        var newestTime = DateTime.MinValue;
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var time = File.GetLastWriteTimeUtc(path);
                var lines = File.ReadAllLines(path);
                File.Delete(path);
                if (lines.Length == 0 || time < newestTime)
                {
                    continue;
                }

                var installed = string.Equals(lines[0].Trim(), "Applied", StringComparison.OrdinalIgnoreCase);
                var message = lines.Length > 1 && lines[1].Trim().Length > 0
                    ? lines[1].Trim()
                    : installed ? "The update installed." : "The last update didn't install; your previous version is still in place.";
                newest = new AppUpdateOutcome(installed, message);
                newestTime = time;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Can't read or remove it: try again on the next start.
            }
        }

        return newest;
    }
}
