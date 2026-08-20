namespace IcarusStarlink.PakIO.Pak;

/// <summary>Thin seam around launching an external process, so UnrealPakService is testable without actually spawning UnrealPak.exe.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// arguments is a list, not a pre-joined command-line string: paths under a real Icarus/UnrealPak
    /// install routinely contain spaces ("Icarus Software", "Program Files", ...), and building a
    /// single string means hand-quoting each one correctly. ArgumentList (what the real
    /// implementation uses under the hood) handles that per-argument instead.
    /// </summary>
    Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);
