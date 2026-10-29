using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The UE4SS build that ships inside Starlink: a dev build from UE4SS-RE/RE-UE4SS's rolling
/// "experimental-latest" release, embedded unchanged in the exe (about 8.7 MB, already
/// compressed), so installing UE4SS needs no download and no extra file next to the app. Dev
/// builds, not the old tagged v3.0.1, because that is what Icarus mods are made for (both C++ mods
/// a real user ran shipped one, and their Lua API is newer). It is copied out to a temp file only
/// when installing, checked against the hash recorded here (GitHub's own digest for the asset),
/// and handed to the same installer a GitHub download uses. UE4SS's MIT licence is added if the
/// zip has none (dev builds carry ue4ss/LICENSE; the old v3.0.1 zip didn't).
///
/// To bump it: download the newest UE4SS_v3.0.1-NNNN-gCOMMIT.zip from experimental-latest, put it
/// in Assets\UE4SS (and update the csproj Include), and set FileName, BuildNumber, Commit and Sha256.
/// </summary>
public static class BundledUe4ss
{
    public const string Version = "3.0.1";

    /// <summary>The release asset's own name: version, build number (commits after the v3.0.1 tag) and commit.</summary>
    public const string FileName = "UE4SS_v3.0.1-1181-g83330635.zip";

    public const int BuildNumber = 1181;

    public const string Commit = "83330635";

    /// <summary>SHA-256 of that release asset (matches GitHub's digest for it).</summary>
    public const string Sha256 = "b9a166a756605b21058a64cde93bcaab1a855dba3553e5c7e6ce0fa8fdfad7b9";

    /// <summary>"3.0.1 dev build 1181", for buttons and messages.</summary>
    public static string Label => IcarusStarlink.Core.Ue4ss.Ue4ssBuild.Describe(Version, Commit, BuildNumber);

    private const string ZipResource = "UE4SS.zip";
    private const string LicenseResource = "UE4SS-LICENSE.txt";

    /// <summary>Writes the bundled release to a new temp .zip (with LICENSE added) and returns its path. The caller deletes it.</summary>
    public static async Task<string> ExtractToTempAsync(CancellationToken cancellationToken = default)
    {
        var assembly = typeof(BundledUe4ss).Assembly;
        var path = Path.Combine(Path.GetTempPath(), $"UE4SS_bundled_{Guid.NewGuid():N}.zip");
        await using (var resource = Open(assembly, ZipResource))
        await using (var file = File.Create(path))
        {
            await resource.CopyToAsync(file, cancellationToken);
        }

        await using (var check = File.OpenRead(path))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken));
            if (!hash.Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("The UE4SS copy built into Starlink is damaged. Reinstall Starlink, or install UE4SS from GitHub or a zip instead.");
            }
        }

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            // Next to UE4SS.dll, wherever the zip keeps it (dev builds: ue4ss\, old v3.0.1: root).
            var licensePath = zip.GetEntry("ue4ss/UE4SS.dll") is null ? "LICENSE" : "ue4ss/LICENSE";
            if (zip.GetEntry(licensePath) is null)
            {
                await using var license = Open(assembly, LicenseResource);
                await using var entry = zip.CreateEntry(licensePath).Open();
                await license.CopyToAsync(entry, cancellationToken);
            }
        }

        return path;
    }

    private static Stream Open(Assembly assembly, string name) =>
        assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Starlink was built without its bundled UE4SS ({name}).");
}
