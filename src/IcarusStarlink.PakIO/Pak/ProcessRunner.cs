using System.Diagnostics;
using System.Text;

namespace IcarusStarlink.PakIO.Pak;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        // Async event-based reads, not process.StandardOutput.ReadToEndAsync() after Start(): UnrealPak
        // can write enough output (one line per extracted file) to fill the pipe buffer, which deadlocks
        // a synchronous/sequential read-after-start pattern if the child blocks on a full pipe while the
        // parent is still waiting on WaitForExit first.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // WaitForExitAsync throwing on cancellation doesn't terminate the child itself — without
            // this, a cancelled build would leave the real OS process (e.g. UnrealPak.exe) running
            // indefinitely with open handles into the staging directory, which would then make the
            // caller's own cleanup of that directory fail.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
