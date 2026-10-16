using System.Text;

namespace IcarusStarlink.Core.Server;

/// <summary>
/// Follows a log file the server is still writing: each Poll returns only the complete lines
/// added since the last one. Opens with FileShare.ReadWrite|Delete so the server is never blocked.
/// When the file shrinks (Unreal renames Icarus.log to a backup and starts a new one on every
/// launch) it starts over from the top. A partial last line is held back until its newline arrives.
/// </summary>
public sealed class LogTailer(string path)
{
    private long _position;
    private string _pending = "";

    public string Path { get; } = path;

    /// <summary>Skip everything already in the file — for attaching to a server that has been running a while.</summary>
    public void SeekToEnd()
    {
        _position = File.Exists(Path) ? new FileInfo(Path).Length : 0;
        _pending = "";
    }

    /// <summary>Start from the beginning on the next Poll.</summary>
    public void Reset()
    {
        _position = 0;
        _pending = "";
    }

    public IReadOnlyList<string> Poll(int maxBytes = 1 << 20)
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                Reset();
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
        "LogAudio", "LogAudioMixer", "LogSlate", "LogHttp", "LogConfig", "LogAssetRegistry", "LogTemp: Verbose",
        "LogMeshReduction", "LogAnimation", "LogNiagara", "LogFoliage", "LogLandscape", "LogUObjectArray",
    ];

    public static bool IsNoise(string line) =>
        line.Contains(": Verbose:", StringComparison.Ordinal)
        || NoiseCategories.Any(c => line.Contains(c + ":", StringComparison.Ordinal));

    public static bool IsError(string line) =>
        line.Contains(": Error:", StringComparison.Ordinal) || line.Contains("Fatal error", StringComparison.OrdinalIgnoreCase);

    public static bool IsWarning(string line) => line.Contains(": Warning:", StringComparison.Ordinal);

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
