namespace IcarusStarlink.Updater;

/// <summary>
/// What the updater leaves for the app it relaunches: a two-line text file (the outcome's name,
/// then one line for the user) next to updater.log — Logs\update-outcome.txt in the install, or
/// %TEMP%\IcarusStarlink-update-outcome.txt when the Logs folder can't be written. The app reads
/// and deletes it on its next start (IcarusStarlink.App's AppUpdateOutcome). Plain text, not JSON:
/// the updater is a trimmed single-file exe, and nothing here should depend on reflection.
/// </summary>
public static class UpdateOutcomeFile
{
    public const string FileName = "update-outcome.txt";
    public const string TempFileName = "IcarusStarlink-update-outcome.txt";

    public static void Write(string path, UpdateRunner.Outcome outcome, string message)
    {
        var oneLine = message.Replace("\r", " ").Replace("\n", " ").Trim();
        File.WriteAllText(path, $"{outcome}\n{oneLine}\n");
    }
}
