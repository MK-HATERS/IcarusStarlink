using System.Globalization;
using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>What UE4SS did with a mod the last time the game ran, as UE4SS.log tells it.</summary>
public enum Ue4ssLastRun
{
    /// <summary>The log can't tell (the game stopped, or is still starting, before the Lua pass).</summary>
    Unknown,

    /// <summary>UE4SS started it.</summary>
    Started,

    /// <summary>The log ran through both start passes and UE4SS didn't start it.</summary>
    NotStarted,

    /// <summary>No log (install deletes it), or the mod was turned on or changed after the last run.</summary>
    NotRunSince,
}

/// <summary>
/// The mod start lines of UE4SS.log: "Starting Lua mod 'X'", "Mod 'X' has enabled.txt, starting
/// mod." and "Mod 'X' disabled in mods.txt.". UE4SS starts mods in two passes (C++ mods first, then
/// Lua mods), each beginning with a "Starting mods (from mods.txt …" line; a C++ mod is only named
/// in the first, a Lua mod only in the second, so the last line naming a mod is what happened to
/// it. Lua print output can glue a start line onto the end of another, so nothing is anchored.
/// Lua errors are left out: no real failing log has been captured to match against.
/// </summary>
public sealed partial class Ue4ssLogStarts
{
    /// <summary>No UE4SS.log: every mod is "not run since".</summary>
    public static readonly Ue4ssLogStarts NoLog = new(exists: false, new(StringComparer.OrdinalIgnoreCase), passes: 0, lastPassAt: null);

    private static readonly Lock CacheLock = new();
    private static (string Path, DateTime WriteUtc, long Length, Ue4ssLogStarts Result)? _cache;

    private readonly bool _exists;
    private readonly Dictionary<string, bool> _lastWord;
    private readonly int _passes;

    private Ue4ssLogStarts(bool exists, Dictionary<string, bool> lastWord, int passes, DateTime? lastPassAt)
    {
        _exists = exists;
        _lastWord = lastWord;
        _passes = passes;
        LastPassAt = lastPassAt;
    }

    /// <summary>When UE4SS last began starting mods (local time, the log's own clock); null when the log never got there.</summary>
    public DateTime? LastPassAt { get; }

    [GeneratedRegex(@"Starting (?:Lua|C\+\+) mod '([^'\r\n]+)'")]
    private static partial Regex StartingModRegex();

    [GeneratedRegex(@"Mod '([^'\r\n]+)' has enabled\.txt, starting mod\.")]
    private static partial Regex EnabledTxtRegex();

    [GeneratedRegex(@"Mod '([^'\r\n]+)' disabled in mods\.txt\.")]
    private static partial Regex DisabledRegex();

    [GeneratedRegex(@"\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d(?:\.\d+)?)\] Starting mods \(from mods\.txt")]
    private static partial Regex PassRegex();

    public static Ue4ssLogStarts Parse(IEnumerable<string> lines)
    {
        var lastWord = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var passes = 0;
        DateTime? lastPassAt = null;
        foreach (var line in lines)
        {
            if (!line.Contains("mod", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match pass in PassRegex().Matches(line))
            {
                passes++;
                if (DateTime.TryParseExact(pass.Groups[1].Value, ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss"],
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                {
                    lastPassAt = at;
                }
            }

            // In the order the line has them, so a glued line's last word wins too.
            var words = StartingModRegex().Matches(line).Select(m => (m.Index, Name: m.Groups[1].Value, Started: true))
                .Concat(EnabledTxtRegex().Matches(line).Select(m => (m.Index, Name: m.Groups[1].Value, Started: true)))
                .Concat(DisabledRegex().Matches(line).Select(m => (m.Index, Name: m.Groups[1].Value, Started: false)))
                .OrderBy(w => w.Index);
            foreach (var word in words)
            {
                lastWord[word.Name] = word.Started;
            }
        }

        return new Ue4ssLogStarts(exists: true, lastWord, passes, lastPassAt);
    }

    /// <summary>
    /// Reads UE4SS.log (shared, so it works while the game has it open), cached by its write time and
    /// size. A log that is missing or can't be read is NoLog.
    /// </summary>
    public static Ue4ssLogStarts Read(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists)
            {
                return NoLog;
            }

            var writeUtc = info.LastWriteTimeUtc;
            var length = info.Length;
            lock (CacheLock)
            {
                if (_cache is { } hit && string.Equals(hit.Path, logPath, StringComparison.OrdinalIgnoreCase) && hit.WriteUtc == writeUtc && hit.Length == length)
                {
                    return hit.Result;
                }
            }

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var result = Parse(ReadLines(reader));
            lock (CacheLock)
            {
                _cache = (logPath, writeUtc, length, result);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return NoLog;
        }
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// When the mod's on/off last changed (local time): the newest of its folder in the game (made
    /// or changed), its enabled.txt and mods.txt. DateTime.MinValue when none can be read.
    /// </summary>
    public static DateTime LastChanged(string modsFolder, string name)
    {
        var newest = DateTime.MinValue;
        try
        {
            var folder = Path.Combine(modsFolder, name);
            if (Directory.Exists(folder))
            {
                newest = Max(newest, Directory.GetCreationTime(folder), Directory.GetLastWriteTime(folder));
            }

            foreach (var file in new[] { Path.Combine(folder, Ue4ssModStart.EnabledFileName), Path.Combine(modsFolder, Ue4ssModStart.ListFileName) })
            {
                if (File.Exists(file))
                {
                    newest = Max(newest, File.GetCreationTime(file), File.GetLastWriteTime(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // What could be read stands.
        }

        return newest;

        static DateTime Max(DateTime a, DateTime b, DateTime c) => new[] { a, b, c }.Max();
    }

    /// <summary>
    /// What the last run did with this mod. A mod changed after UE4SS last started mods (or with no
    /// log at all) is NotRunSince, never NotStarted: the log is from before the change.
    /// </summary>
    /// <param name="changedAt">LastChanged for the mod (local time).</param>
    public Ue4ssLastRun StateFor(string name, DateTime changedAt)
    {
        if (!_exists || LastPassAt is { } at && changedAt > at)
        {
            return Ue4ssLastRun.NotRunSince;
        }

        if (_lastWord.TryGetValue(name, out var started))
        {
            return started ? Ue4ssLastRun.Started : Ue4ssLastRun.NotStarted;
        }

        // Both passes ran (C++ then Lua) and neither named it.
        return _passes >= 2 ? Ue4ssLastRun.NotStarted : Ue4ssLastRun.Unknown;
    }
}
