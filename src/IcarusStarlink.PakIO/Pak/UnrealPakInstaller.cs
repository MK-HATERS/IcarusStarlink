using System.IO.Compression;
using System.Text.Json;

namespace IcarusStarlink.PakIO.Pak;

public sealed class UnrealPakInstaller(IProcessRunner processRunner, HttpClient httpClient, string appBaseDirectory) : IUnrealPakInstaller
{
    /// <summary>Relative layout inside the payload zip mirrors Epic's own tree — the exe needs its sibling DLLs and the Engine\Config/Oodle-plugin folders at their expected relative paths, so the whole tree ships as-is rather than being flattened.</summary>
    private const string ExeRelativePath = @"Engine\Binaries\Win64\UnrealPak.exe";

    /// <summary>
    /// A fixed, dedicated release tag — deliberately NOT one of the app's own version tags —
    /// since UnrealPak.exe is pinned to UE 4.27 and doesn't change release to release; re-bundling
    /// the same ~10MB payload into every future app release zip would be pure churn. A release
    /// asset's download URL is a plain static-file redirect (unlike api.github.com), so this needs
    /// no API call, User-Agent header, or JSON parsing — just a direct GET.
    /// </summary>
    private const string RemotePayloadUrl = "https://github.com/MK-HATERS/IcarusStarlink/releases/download/unrealpak-4.27/UnrealPak.zip";

    private string InstallRoot => Path.Combine(appBaseDirectory, "Tools", "UnrealPak");

    private string PayloadZipPath => Path.Combine(appBaseDirectory, "Tools", "UnrealPak.zip");

    public string InstalledExePath => Path.Combine(InstallRoot, ExeRelativePath);

    public bool PayloadAvailable => true;

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

    public async Task<string> InstallAsync(CancellationToken cancellationToken = default)
    {
        // Delete-then-extract rather than extract-over: a Reinstall's whole purpose is repairing
        // a corrupt copy, and extracting over one leaves any extra/renamed stale file in place.
        if (Directory.Exists(InstallRoot))
        {
            await Task.Run(() => Directory.Delete(InstallRoot, recursive: true), cancellationToken);
        }

        if (File.Exists(PayloadZipPath))
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(PayloadZipPath, InstallRoot), cancellationToken);
        }
        else
        {
            await DownloadAndExtractRemotePayloadAsync(cancellationToken);
        }

        if (!File.Exists(InstalledExePath))
        {
            throw new InvalidOperationException(
                $"The payload extracted but doesn't contain '{ExeRelativePath}' — the UnrealPak payload is mis-packaged.");
        }

        return InstalledExePath;
    }

    private async Task DownloadAndExtractRemotePayloadAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(RemotePayloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Couldn't reach GitHub to download UnrealPak — check your internet connection. ({ex.Message})", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Couldn't download UnrealPak from GitHub ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            // Downloads to a temp file rather than straight into InstallRoot — ZipFile.ExtractToDirectory
            // needs a real file on disk (no stream overload), and a partial/corrupt download must not
            // leave anything behind under InstallRoot for VerifyAsync to later find and misreport as "Ok".
            var tempZipPath = Path.Combine(Path.GetTempPath(), $"UnrealPak-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var fileStream = File.Create(tempZipPath))
                {
                    await response.Content.CopyToAsync(fileStream, cancellationToken);
                }

                await Task.Run(() => ZipFile.ExtractToDirectory(tempZipPath, InstallRoot), cancellationToken);
            }
            finally
            {
                File.Delete(tempZipPath);
            }
        }
    }

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
