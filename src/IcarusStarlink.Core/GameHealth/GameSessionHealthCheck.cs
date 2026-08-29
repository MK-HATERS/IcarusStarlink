using System.Xml.Linq;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.GameHealth;

/// <summary>
/// The closest thing this app can offer to "did that merge actually work in Icarus" — not by
/// guessing at DataTable-parse-error text (no real example of one was available to ground that in;
/// inventing patterns would be pure guessing, the one thing this project has consistently avoided
/// doing), but by reading two things that ARE real and confirmed:
///
/// 1. A real game crash. Icarus writes one folder per crash under its own fixed, real
/// %LocalAppData%\Icarus\Saved\Crashes\ location (confirmed against this machine's own real crash
/// history), each containing a CrashContext.runtime-xml whose RuntimeProperties/ErrorMessage is a
/// genuine, human-readable description (confirmed real content: a GPU-driver DXGI_ERROR_DEVICE_
/// REMOVED in the one example available here — unrelated to mods, but it proves the file shape).
/// A new crash folder appearing after an Install is a real, unambiguous "something went wrong last
/// time you played" signal, not an inference.
///
/// 2. UE4SS's own log tail, if UE4SS is installed — shown as plain text, not pattern-matched,
/// since this app has no confirmed real example of what a UE4SS mod-load failure actually looks
/// like in that log either. Honest "here's what happened" beats a guessed-at "here's what's wrong."
/// </summary>
public static class GameSessionHealthCheck
{
    public sealed record GameCrash(string CrashId, DateTimeOffset OccurredAtUtc, string ErrorMessage);

    /// <summary>Icarus's own real, fixed per-user crash location — not derived from the Content path, confirmed against this machine's own real crash folders.</summary>
    public static string ResolveCrashesFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Icarus", "Saved", "Crashes");

    /// <param name="crashesFolder">Defaults to ResolveCrashesFolder() — overridable so tests don't need the real %LocalAppData%.</param>
    public static IReadOnlyList<GameCrash> FindCrashesSince(DateTimeOffset sinceUtc, string? crashesFolder = null)
    {
        crashesFolder ??= ResolveCrashesFolder();
        if (!Directory.Exists(crashesFolder))
        {
            return [];
        }

        var crashes = new List<GameCrash>();
        foreach (var crashDirectory in Directory.EnumerateDirectories(crashesFolder))
        {
            var contextFilePath = Path.Combine(crashDirectory, "CrashContext.runtime-xml");
            if (!File.Exists(contextFilePath))
            {
                continue;
            }

            // The crash folder's own creation time IS when the crash happened — UE4's crash
            // reporter creates it fresh, at the moment of the crash, never reuses an old one.
            var createdAtUtc = Directory.GetCreationTimeUtc(crashDirectory);
            if (createdAtUtc < sinceUtc.UtcDateTime)
            {
                continue;
            }

            var errorMessage = TryReadErrorMessage(contextFilePath) ?? "(no error message recorded in this crash report)";
            crashes.Add(new GameCrash(Path.GetFileName(crashDirectory), new DateTimeOffset(createdAtUtc, TimeSpan.Zero), errorMessage));
        }

        return crashes.OrderByDescending(c => c.OccurredAtUtc).ToList();
    }

    private static string? TryReadErrorMessage(string contextFilePath)
    {
        try
        {
            var document = XDocument.Load(contextFilePath);
            return document.Descendants("ErrorMessage").FirstOrDefault()?.Value is { Length: > 0 } message ? message : null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            // A crash report mid-write, or one from an incompatible future format, shouldn't break
            // the whole check — just report it without a parsed message rather than throwing.
            return null;
        }
    }

    /// <summary>
    /// The last few lines of UE4SS's own real log, as-is — not pattern-matched, since this app has
    /// no confirmed real example of what a genuine UE4SS mod-load failure looks like in this log to
    /// match against. An empty list means UE4SS isn't installed or hasn't produced a log yet, not
    /// an error.
    /// </summary>
    public static IReadOnlyList<string> ReadUe4ssLogTail(string icarusContentPath, int maxLines = 40)
    {
        string logPath;
        try
        {
            logPath = Ue4ssGamePaths.ResolveLoaderLogPath(icarusContentPath);
        }
        catch (ArgumentException)
        {
            return [];
        }

        if (!File.Exists(logPath))
        {
            return [];
        }

        try
        {
            return File.ReadLines(logPath).TakeLast(maxLines).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
