using System.Text.Json;

namespace IcarusStarlink.PakIO.Pak;

public sealed class UnrealPakInstaller(IProcessRunner processRunner, string appBaseDirectory) : IUnrealPakInstaller
{
    // Legacy location is exposed for callers, but never adopted or executed automatically.
    public string InstalledExePath => Path.Combine(appBaseDirectory, "Tools", "UnrealPak", "Engine", "Binaries", "Win64", "UnrealPak.exe");

    public bool PayloadAvailable => false;

    public async Task<UnrealPakVerifyResult> VerifyAsync(string exePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return new UnrealPakVerifyResult(UnrealPakHealth.Missing, null, "No UnrealPak.exe at that path.");
        }

        var engineVersion = TryReadEngineVersion(exePath);

        try
        {
            // A bare invocation makes UnrealPak print its usage and exit non-zero — the exit code
            // doesn't matter, producing output does: a copy whose sibling DLLs are gone fails to
            // start at all, which is exactly the corruption File.Exists can't see.
            var result = await processRunner.RunAsync(exePath, [], cancellationToken);
            var ranAtAll = result.StandardOutput.Length > 0 || result.StandardError.Length > 0;
            return ranAtAll
                ? new UnrealPakVerifyResult(UnrealPakHealth.Ok, engineVersion, null)
                : new UnrealPakVerifyResult(UnrealPakHealth.Broken, engineVersion, "UnrealPak.exe started but produced no output — the copy may be corrupt.");
        }
        catch (Exception ex)
        {
            return new UnrealPakVerifyResult(UnrealPakHealth.Broken, engineVersion, $"UnrealPak.exe wouldn't run: {ex.Message}");
        }
    }

    // No authenticated Epic payload is pinned here. Refuse even an old Tools/UnrealPak.zip.
    public Task<string> InstallAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new InvalidOperationException(
            "Automatic UnrealPak installation is disabled. Select your own Epic-sourced or locally built UE 4.27 UnrealPak.exe in Settings."));
    /// <summary>Epic's own UnrealPak.version JSON sits next to the exe ({"MajorVersion":4,"MinorVersion":27,...}) — best-effort, since a hand-assembled copy may not carry it.</summary>
    private static string? TryReadEngineVersion(string exePath)
    {
        try
        {
            var versionPath = Path.Combine(Path.GetDirectoryName(exePath)!, "UnrealPak.version");
            if (!File.Exists(versionPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(versionPath));
            var root = document.RootElement;
            return $"{root.GetProperty("MajorVersion").GetInt32()}.{root.GetProperty("MinorVersion").GetInt32()}.{root.GetProperty("PatchVersion").GetInt32()}";
        }
        catch (Exception)
        {
            return null;
        }
    }
}
