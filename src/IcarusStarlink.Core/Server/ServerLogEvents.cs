using System.Globalization;
using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Server;

public enum ServerLogEventKind
{
    PlayerJoined,
    PlayerLeft,
    ProspectLoaded,
    ReturnedToLobby,
    ShutdownRequested,
    ModPakMounted,
    Error,
}

public sealed record ServerLogEvent(ServerLogEventKind Kind, DateTime? Time, string Text);

public sealed record OnlinePlayer(string Name, string PlayerId, DateTime? Since)
{
    /// <summary>The 17-digit Steam64 id for the admin and ban lists; null when the log gave no Steam id.</summary>
    public string? Steam64Id => ServerAccessLists.Steam64FromPlayerId(PlayerId);

    public bool HasSteam64Id => Steam64Id is not null;
}

/// <summary>
/// Turns the dedicated server's own log into players and events. Line shapes taken from a real
/// Icarus dedicated server log (player name and id replaced):
///   LogNet: Login request: ?password=…?Name=TestPlayer userId: Steam:UNKNOWN [0x1100001025E4C01] platform: Steam
///   LogNet: Join succeeded: TestPlayer
///   LogNet: UNetConnection::Close: … PC: BP_IcarusPlayerController…, UniqueId: Steam:UNKNOWN [0x1100001025E4C01], …
///   LogLocalSessionSubsystem: Display: SetActiveProspectInfo - ProspectID: Hard Elysium JULY | ProspectDTKey: OpenWorld_Elysium
///   LogLocalSessionSubsystem: Display: ResetActiveProspect - ProspectID: … (back to the lobby)
///   LogCore: Engine exit requested (reason: ConsoleCtrl RequestExit)
/// A player moving from the lobby into a prospect closes one connection and opens another (same
/// id), so a "left" immediately followed by a rejoin of the same id is a map change, not a leave.
/// </summary>
public sealed partial class ServerLogEvents
{
    private static readonly TimeSpan TravelGrace = TimeSpan.FromSeconds(60);

    /// <summary>How a ModPakMounted event's text starts (one per mod pak at every start: the Overview's Recent events leave them out).</summary>
    public const string ModPakMountedText = "Mod pak loaded: ";

    private readonly Dictionary<string, string> _namesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OnlinePlayer> _online = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Players whose connection closed at the given time; they stay online until TravelGrace passes without a rejoin (a lobby→prospect move closes and reopens within seconds).</summary>
    private readonly Dictionary<string, DateTime?> _closing = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingLoginId;

    /// <summary>Login lines since the last join: a join whose name matches no login is paired with the pending login only when there was exactly one.</summary>
    private int _loginsSinceJoin;

    public IReadOnlyCollection<OnlinePlayer> Online => _online.Values;

    public string? CurrentProspect { get; private set; }

    public void Reset()
    {
        _namesById.Clear();
        _online.Clear();
        _closing.Clear();
        _pendingLoginId = null;
        _loginsSinceJoin = 0;
        CurrentProspect = null;
    }

    /// <summary>Turns closed connections that weren't followed by a rejoin within the grace period into "disconnected" events. Called with each line's time, and by the page's timer with the current UTC time (a quiet server writes no lines).</summary>
    public IReadOnlyList<ServerLogEvent> ExpireStale(DateTime nowUtc)
    {
        List<ServerLogEvent>? left = null;
        foreach (var (id, closedAt) in _closing.ToList())
        {
            if (closedAt is not null && nowUtc - closedAt.Value < TravelGrace)
            {
                continue;
            }

            _closing.Remove(id);
            if (_online.Remove(id, out var player))
            {
                (left ??= []).Add(new ServerLogEvent(ServerLogEventKind.PlayerLeft, closedAt, $"{player.Name} disconnected"));
            }
        }

        return left ?? (IReadOnlyList<ServerLogEvent>)[];
    }

    /// <summary>Feeds one log line; returns the events it produced (usually none or one; a line can also expire earlier disconnects).</summary>
    public IReadOnlyList<ServerLogEvent> Feed(string line)
    {
        var time = ParseTime(line);
        var expired = time is { } t ? ExpireStale(t) : [];
        var ev = FeedOne(line, time);
        return ev is null ? expired : [.. expired, ev];
    }

    private ServerLogEvent? FeedOne(string line, DateTime? time)
    {
        // Player events must start right after the line's "[time][frame]" groups, like
        // IsCleanExit: player names are in the log too, and text that only quoted a join added a
        // ghost player (the daily restart then always waited) or a quoted exit emptied the list.
        var message = ServerLogFilter.SkipLogPrefix(line).ToString();
        if (LoginPattern().Match(message) is { Success: true } login)
        {
            var id = login.Groups["id"].Value;
            _namesById[id] = login.Groups["name"].Value;
            _pendingLoginId = id;
            _loginsSinceJoin++;
            return null;
        }

        if (JoinPattern().Match(message) is { Success: true } join)
        {
            var name = join.Groups["name"].Value.Trim();
            var id = _pendingLoginId is { } pending && _namesById.GetValueOrDefault(pending) == name
                ? pending
                : _namesById.FirstOrDefault(p => p.Value == name).Key
                    // The login's name can differ from the join's ("Who?" is logged as "Who" there):
                    // with exactly one login since the last join it can only be that one's.
                    ?? (_loginsSinceJoin == 1 ? _pendingLoginId : null)
                    ?? name;
            _pendingLoginId = null;
            _loginsSinceJoin = 0;
            var alreadyHere = _closing.Remove(id) | _online.ContainsKey(id);
            if (!_online.ContainsKey(id))
            {
                _online[id] = new OnlinePlayer(name, id, time);
            }

            return alreadyHere ? null : new ServerLogEvent(ServerLogEventKind.PlayerJoined, time, $"{name} joined");
        }

        if (message.StartsWith("LogNet: UNetConnection::Close:", StringComparison.Ordinal)
            && line.Contains("PC: BP_IcarusPlayerController", StringComparison.Ordinal)
            && ClosePattern().Match(line) is { Success: true } close)
        {
            var id = close.Groups["id"].Value;
            if (_online.ContainsKey(id))
            {
                _closing[id] = time;
            }

            return null;
        }

        if (ProspectPattern().Match(line) is { Success: true } prospect)
        {
            var name = prospect.Groups["prospect"].Value.Trim();
            var map = prospect.Groups["map"].Value.Trim().Replace("OpenWorld_", "", StringComparison.Ordinal);
            if (string.Equals(name, CurrentProspect, StringComparison.Ordinal))
            {
                return null;
            }

            CurrentProspect = name;
            return new ServerLogEvent(ServerLogEventKind.ProspectLoaded, time, $"Prospect loaded: {name} ({map})");
        }

        if (line.Contains("ResetActiveProspect - ProspectID:", StringComparison.Ordinal) && !line.Contains("ProspectID: NULL", StringComparison.Ordinal))
        {
            CurrentProspect = null;
            return new ServerLogEvent(ServerLogEventKind.ReturnedToLobby, time, "Returned to the lobby");
        }

        if (ServerLogFilter.IsCleanExit(line) && line.Contains("ConsoleCtrl", StringComparison.Ordinal))
        {
            _online.Clear();
            _closing.Clear();
            return new ServerLogEvent(ServerLogEventKind.ShutdownRequested, time, "Shutdown requested");
        }

        if (MountPattern().Match(line) is { Success: true } mount)
        {
            return new ServerLogEvent(ServerLogEventKind.ModPakMounted, time, $"{ModPakMountedText}{mount.Groups["pak"].Value}");
        }

        if (ServerLogFilter.IsError(line))
        {
            return new ServerLogEvent(ServerLogEventKind.Error, time, line.Length > 200 ? line[..200] + "…" : line);
        }

        return null;
    }

    /// <summary>"[2026.08.12-17.31.51:477][117]LogNet: …" → the UTC timestamp Unreal logs in; null for lines without one.</summary>
    public static DateTime? ParseTime(string line)
    {
        var match = TimePattern().Match(line);
        return match.Success && DateTime.TryParseExact(match.Groups["t"].Value, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
    }

    [GeneratedRegex(@"^\[(?<t>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2})")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^LogNet: Login request: .*?\?Name=(?<name>[^?]+?)(?=\?| userId:).*?userId: \S+ \[(?<id>0x[0-9A-Fa-f]+)\]")]
    private static partial Regex LoginPattern();

    [GeneratedRegex(@"^LogNet: Join succeeded: (?<name>.+)$")]
    private static partial Regex JoinPattern();

    [GeneratedRegex(@"UniqueId: \S+ \[(?<id>0x[0-9A-Fa-f]+)\]")]
    private static partial Regex ClosePattern();

    [GeneratedRegex(@"SetActiveProspectInfo - ProspectID: (?<prospect>.+?) \| ProspectDTKey: (?<map>\S+)")]
    private static partial Regex ProspectPattern();

    [GeneratedRegex(@"LogPakFile: Display: Mounting pak file \.\./\.\./\.\./Icarus/Content/Paks/mods/(?<pak>[^\s]+?)\.?$")]
    private static partial Regex MountPattern();
}
