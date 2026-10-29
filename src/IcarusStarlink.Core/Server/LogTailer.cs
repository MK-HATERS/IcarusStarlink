using System.Text;

namespace IcarusStarlink.Core.Server;

/// <summary>
/// Follows a log file the server is still writing: each Poll returns only the complete lines
/// added since the last one. Opens with FileShare.ReadWrite|Delete so the server is never blocked.
/// Unreal renames Icarus.log to a backup and starts a new one on every launch; that new run is
/// noticed when the file shrinks, OR when its first bytes change (the first line carries the time
/// the log was opened) — a new run that has already grown past the old read position used to be
/// read from the middle. A partial last line is held back until its newline arrives. Safe to poll
/// from a background thread: one Poll at a time.
/// </summary>
public sealed class LogTailer(string path)
{
    /// <summary>How much of the file's start is remembered to recognise a new file under the same name.</summary>
    private const int HeadBytes = 256;

    private readonly object _gate = new();
    private long _position;
    private string _pending = "";
    private byte[] _head = [];

    public string Path { get; } = path;

    /// <summary>Skip everything already in the file — for attaching to a server that has been running a while.</summary>
    public void SeekToEnd()
    {
        lock (_gate)
        {
            _pending = "";
            _position = 0;
            _head = [];
            try
            {
                if (File.Exists(Path))
                {
                    using var stream = Open();
                    _head = ReadHead(stream);
                    _position = stream.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read from the top next time instead.
            }
        }
    }

    private void ResetCore()
    {
        _position = 0;
        _pending = "";
        _head = [];
    }

    public IReadOnlyList<string> Poll(int maxBytes = 1 << 20)
    {
        lock (_gate)
        {
            return PollCore(maxBytes);
        }
    }

    private FileStream Open() => new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] ReadHead(FileStream stream)
    {
        var head = new byte[(int)Math.Min(HeadBytes, stream.Length)];
        stream.Seek(0, SeekOrigin.Begin);
        var read = 0;
        while (read < head.Length)
        {
            var n = stream.Read(head, read, head.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == head.Length ? head : head[..read];
    }

    /// <summary>The file under this name is not the one read so far: it is shorter, or it starts differently.</summary>
    private bool IsNewFile(FileStream stream)
    {
        if (stream.Length < _position)
        {
            return true;
        }

        if (_position == 0 || _head.Length == 0)
        {
            return false;
        }

        var current = ReadHead(stream);
        var common = Math.Min(current.Length, _head.Length);
        return !current.AsSpan(0, common).SequenceEqual(_head.AsSpan(0, common));
    }

    private IReadOnlyList<string> PollCore(int maxBytes)
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            using var stream = Open();
            if (IsNewFile(stream))
            {
                ResetCore();
            }

            if (_head.Length < HeadBytes && stream.Length > _head.Length)
            {
                _head = ReadHead(stream);
            }

            if (stream.Length == _position)
            {
                return [];
            }

            // A huge backlog (a server running for days) only shows its newest part.
            if (stream.Length - _position > maxBytes)
            {
                _position = stream.Length - maxBytes;
                _pending = "";
            }

            stream.Seek(_position, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - _position];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            _position += read;
            var text = _pending + Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n');
            _pending = lines[^1];
            return [.. lines[..^1].Select(l => l.TrimEnd('\r'))];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Which log lines count as noise — engine chatter a server owner never needs (texture streaming, shader, audio, RHI, HTTP), as opposed to joins, prospect loads, mods and errors.</summary>
public static class ServerLogFilter
{
    private static readonly string[] NoiseCategories =
    [
        "LogStreaming", "LogTexture", "LogShaders", "LogMaterial", "LogRHI", "LogD3D11RHI", "LogD3D12RHI",
        "LogAudio", "LogAudioMixer", "LogSlate", "LogHttp", "LogConfig", "LogAssetRegistry",
        "LogMeshReduction", "LogAnimation", "LogNiagara", "LogFoliage", "LogLandscape", "LogUObjectArray",
    ];

    public static bool IsNoise(string line) =>
        line.Contains(": Verbose:", StringComparison.Ordinal)
        || NoiseCategories.Any(c => line.Contains(c + ":", StringComparison.Ordinal));

    public static bool IsError(string line) =>
        line.Contains(": Error:", StringComparison.Ordinal) || line.Contains("Fatal error", StringComparison.OrdinalIgnoreCase);

    /// <summary>How the Log panel colours a line: Unreal writes "Category: Error:" / "Category: Warning:". Two Contains calls, so it is cheap enough to run as each row scrolls into view.</summary>
    public static ServerLogLevel LevelOf(string? line) =>
        string.IsNullOrEmpty(line) ? ServerLogLevel.Normal
        : IsError(line) ? ServerLogLevel.Error
        : line.Contains(": Warning:", StringComparison.Ordinal) ? ServerLogLevel.Warning
        : ServerLogLevel.Normal;

    /// <summary>
    /// A run that ended on purpose logs one of these just before the process exits: Ctrl+C or
    /// closing the console ("Engine exit requested (reason: ConsoleCtrl RequestExit)"), the ini's
    /// shutdown-when-empty timers, and the normal engine exit ("LogExit: Exiting."). A run whose log
    /// simply stops, with no exit line, crashed or was killed.
    /// The words must come right after the line's "[time][frame]Category: " start: player names
    /// and chat are in the log too, and a player called "Engine exit requested" made a later crash
    /// look like a clean stop, so crash restart never fired.
    /// </summary>
    public static bool IsCleanExit(string line)
    {
        var message = SkipLogPrefix(line);
        if (message.StartsWith("LogExit: Exiting.", StringComparison.Ordinal))
        {
            return true;
        }

        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0
            && message.StartsWith("Log", StringComparison.Ordinal)
            && !message[..colon].Contains(' ')
            && message[(colon + 2)..].StartsWith("Engine exit requested", StringComparison.Ordinal);
    }

    /// <summary>The line without its leading "[2026.08.10-00.32.24:447][176]" groups.</summary>
    private static ReadOnlySpan<char> SkipLogPrefix(string line)
    {
        var rest = line.AsSpan().TrimStart();
        while (rest.Length > 0 && rest[0] == '[' && rest.IndexOf(']') is var close and > 0)
        {
            rest = rest[(close + 1)..];
        }

        return rest;
    }

    /// <summary>The server logs every login request WITH the join password in plain text ("Login request: ?password=4543?Name=…") — masked before it is shown or copied anywhere.</summary>
    public static string Redact(string line)
    {
        var at = line.IndexOf("password=", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return line;
        }

        var start = at + "password=".Length;
        var end = line.IndexOfAny(['?', ' '], start);
        return line[..start] + "***" + (end < 0 ? "" : line[end..]);
    }
}

public enum ServerLogLevel
{
    Normal,
    Warning,
    Error,
}
