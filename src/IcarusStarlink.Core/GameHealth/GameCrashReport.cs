using System.Text;
using System.Text.RegularExpressions;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.GameHealth;

/// <summary>How many lines one UE4SS mod logged that say error or failed, in one game session.</summary>
public sealed record Ue4ssModErrorCount(string ModName, int Count);

/// <summary>
/// What to tell the player right after a game crash, from the crash's CrashSummary and the
/// crashed session's UE4SS.log. It only says what is there: whether the crashed stack went through
/// UE4SS, and which UE4SS mods logged errors. It never names a mod as the cause, and a graphics
/// driver crash never points at mods. Built from CrashSummary only, so the ids the CrashContext
/// also holds (MachineId, LoginId, EpicAccountId) never reach the text.
/// </summary>
public static partial class GameCrashReport
{
    /// <summary>Longest error message shown in the notice; the copied report has it whole.</summary>
    private const int NoticeMessageLength = 200;

    /// <summary>Mods named in the notice; the copied report lists them all.</summary>
    private const int NoticeModCount = 5;

    /// <summary>
    /// The GPU or its driver stopped answering (D3D12's CommandList->Close failing with
    /// DXGI_ERROR_DEVICE_REMOVED is how it shows in Icarus). Mods don't cause these.
    /// </summary>
    public static bool IsGpuDriverCrash(GameSessionHealthCheck.CrashSummary crash) =>
        GpuDriverError().IsMatch(crash.ErrorMessage);

    /// <summary>
    /// Error lines per mod, most first. A line counts for a mod when it says error, errors, failed,
    /// exception or LUA_ERR and names the mod: a Lua mod's own "[Lua] [Mod]" tag, a path inside
    /// "Mods\Mod\", or a C++ mod's "[Mod]" tag when Mod is one of <paramref name="knownMods"/> (the
    /// Mods folder's names; UE4SS's own tags like [RegisterHook] look the same).
    /// </summary>
    public static IReadOnlyList<Ue4ssModErrorCount> CountUe4ssModErrors(IEnumerable<string> logLines, IReadOnlyCollection<string>? knownMods = null)
    {
        var known = new HashSet<string>(knownMods ?? [], StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in logLines)
        {
            if (!ErrorWord().IsMatch(line))
            {
                continue;
            }

            string? mod = null;
            if (LuaModTag().Match(line) is { Success: true } lua)
            {
                mod = lua.Groups["mod"].Value.Trim();
            }
            else if (LeadingTag().Match(line) is { Success: true } tag && known.Contains(tag.Groups["mod"].Value.Trim()))
            {
                mod = tag.Groups["mod"].Value.Trim();
            }
            else if (ModPath().Match(line) is { Success: true } path)
            {
                mod = path.Groups["mod"].Value.Trim();
            }

            if (!string.IsNullOrEmpty(mod))
            {
                counts[mod] = counts.GetValueOrDefault(mod) + 1;
            }
        }

        return [.. counts
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(c => new Ue4ssModErrorCount(c.Key, c.Value))];
    }

    /// <summary>
    /// The error counts from UE4SS.log, when that log belongs to the crashed session: UE4SS
    /// rewrites it at each game start, so a log last written more than 2 minutes after the crash is
    /// a later session's. Empty when there's no game folder, no UE4SS or no readable log.
    /// </summary>
    public static IReadOnlyList<Ue4ssModErrorCount> ReadUe4ssModErrors(string? icarusContentPath, DateTimeOffset crashAtUtc)
    {
        if (string.IsNullOrWhiteSpace(icarusContentPath))
        {
            return [];
        }

        try
        {
            var logPath = Ue4ssGamePaths.ResolveLoaderLogPath(icarusContentPath);
            if (!File.Exists(logPath) || File.GetLastWriteTimeUtc(logPath) > crashAtUtc.UtcDateTime + TimeSpan.FromMinutes(2))
            {
                return [];
            }

            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);
            var knownMods = Directory.Exists(modsFolder)
                ? Directory.EnumerateDirectories(modsFolder).Select(Path.GetFileName).OfType<string>().ToList()
                : [];

            // UE4SS keeps its log open for writing while the game runs; share it the same way.
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return CountUe4ssModErrors(ReadLines(reader), knownMods);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>The few lines the player reads right after the crash.</summary>
    public static string Describe(GameSessionHealthCheck.CrashSummary crash, IReadOnlyList<Ue4ssModErrorCount> modErrors)
    {
        var text = new StringBuilder();
        text.Append($"Icarus crashed at {crash.OccurredAtUtc.ToLocalTime():HH:mm}");
        text.AppendLine(crash.SecondsSinceStart is { } seconds ? $", {DescribeRunTime(seconds)} after it started." : ".");
        text.AppendLine($"Error: {Shorten(crash.ErrorMessage, NoticeMessageLength)}");
        text.AppendLine();

        if (IsGpuDriverCrash(crash))
        {
            text.Append("This looks like a graphics driver crash, not a mod: the graphics card stopped answering. Updating the graphics driver, or lower graphics settings, often helps.");
            return text.ToString();
        }

        text.AppendLine(crash.Ue4ssInStack
            ? "The crash went through UE4SS, so check your UE4SS mods (Lua or C++) first."
            : "UE4SS wasn't part of the crash.");
        if (modErrors.Count > 0)
        {
            var named = string.Join(", ", modErrors.Take(NoticeModCount).Select(m => $"{m.ModName} ({Errors(m.Count)})"));
            var more = modErrors.Count > NoticeModCount ? $" and {modErrors.Count - NoticeModCount} more" : "";
            text.AppendLine($"UE4SS mods that logged errors this session: {named}{more}.");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The text Copy crash report puts on the clipboard, for a bug report.</summary>
    public static string BuildReport(GameSessionHealthCheck.CrashSummary crash, IReadOnlyList<Ue4ssModErrorCount> modErrors)
    {
        var text = new StringBuilder();
        text.AppendLine($"Icarus crash: {crash.OccurredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({crash.CrashId})");
        text.AppendLine($"Crash type: {crash.CrashType ?? "unknown"}");
        text.AppendLine($"Error: {crash.ErrorMessage}");
        text.AppendLine($"Graphics driver crash: {YesNo(IsGpuDriverCrash(crash))}");
        text.AppendLine($"Went through UE4SS: {YesNo(crash.Ue4ssInStack)}");
        text.AppendLine($"Modules in the crash stack: {(crash.Modules.Count == 0 ? "none recorded" : string.Join(", ", crash.Modules))}");
        text.AppendLine($"Engine version: {crash.EngineVersion ?? "unknown"}");
        if (crash.SecondsSinceStart is { } seconds)
        {
            text.AppendLine($"Game had run for: {DescribeRunTime(seconds)}");
        }

        if (crash.GameStateName is { } state)
        {
            text.AppendLine($"Game state: {state}");
        }

        text.AppendLine(modErrors.Count == 0
            ? "UE4SS mods that logged errors this session: none"
            : "UE4SS mods that logged errors this session (error lines in UE4SS.log):");
        foreach (var mod in modErrors)
        {
            text.AppendLine($"  {mod.ModName}: {mod.Count}");
        }

        return text.ToString();
    }

    private static string DescribeRunTime(int seconds) => seconds switch
    {
        < 60 => seconds == 1 ? "1 second" : $"{seconds} seconds",
        < 3600 => seconds / 60 == 1 ? "1 minute" : $"{seconds / 60} minutes",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60} min",
    };

    private static string Errors(int count) => count == 1 ? "1 error" : $"{count} errors";

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Shorten(string text, int max)
    {
        var oneLine = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"CommandList->Close|DXGI_ERROR_DEVICE_|VK_ERROR_DEVICE_LOST|GPU ?crash", RegexOptions.IgnoreCase)]
    private static partial Regex GpuDriverError();

    [GeneratedRegex(@"\b(errors?|failed|exception)\b|LUA_ERR", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorWord();

    [GeneratedRegex(@"\[Lua\]\s*\[(?<mod>[^\[\]\r\n]+)\]")]
    private static partial Regex LuaModTag();

    /// <summary>The first tag after the timestamp: "[2026-11-07 12:25:59.5984159] [MkHatersBags] native part…".</summary>
    [GeneratedRegex(@"^\[[^\]]*\]\s*\[(?<mod>[^\[\]\r\n]+)\]")]
    private static partial Regex LeadingTag();

    [GeneratedRegex(@"Mods[\\/](?<mod>[^\\/:*?""<>|\r\n]+)[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex ModPath();
}
