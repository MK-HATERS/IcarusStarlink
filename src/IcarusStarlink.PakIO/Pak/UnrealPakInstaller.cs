using System.IO.Compression;
using System.Text.Json;

namespace IcarusStarlink.PakIO.Pak;

public sealed class UnrealPakInstaller(IProcessRunner processRunner, string appBaseDirectory) : IUnrealPakInstaller
{
    /// <summary>Relative layout inside the payload zip mirrors Epic's own tree — the exe needs its sibling DLLs and the Engine\Config/Oodle-plugin folders at their expected relative paths, so the whole tree ships as-is rather than being flattened.</summary>
    private const string ExeRelativePath = @"Engine\Binaries\Win64\UnrealPak.exe";

    private string InstallRoot => Path.Combine(appBaseDirectory, "Tools", "UnrealPak");

    private string PayloadZipPath => Path.Combine(appBaseDirectory, "Tools", "UnrealPak.zip");

    public string InstalledExePath => Path.Combine(InstallRoot, ExeRelativePath);

    public bool PayloadAvailable => File.Exists(PayloadZipPath);

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

    public Task<string> InstallAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!PayloadAvailable)
        {
            throw new FileNotFoundException(
                $"The bundled UnrealPak payload isn't part of this build ('{PayloadZipPath}' not found) — locate an existing UnrealPak.exe instead.");
        }

        // Delete-then-extract rather than extract-over: a Reinstall's whole purpose is repairing
        // a corrupt copy, and extracting over one leaves any extra/renamed stale file in place.
        if (Directory.Exists(InstallRoot))
        {
            Directory.Delete(InstallRoot, recursive: true);
        }

        ZipFile.ExtractToDirectory(PayloadZipPath, InstallRoot);

        if (!File.Exists(InstalledExePath))
        {
            throw new InvalidOperationException(
                $"The payload extracted but doesn't contain '{ExeRelativePath}' — the Tools\\UnrealPak.zip in this release is mis-packaged.");
        }

        return InstalledExePath;
    }, cancellationToken);

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
