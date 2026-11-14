using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The UE4SS build that ships inside Starlink: the official v4.0.0 release of
/// UE4SS-RE/RE-UE4SS, embedded unchanged in the exe (about 8.8 MB, already compressed), so
/// installing UE4SS needs no download and no extra file next to the app. It is copied out to a
/// temp file only when installing, checked against the hash recorded here (GitHub's own digest
/// for the asset), and handed to the same installer a GitHub download uses. UE4SS's MIT licence
/// is added if the zip has none (4.0.0 and the dev builds carry ue4ss/LICENSE; the old v3.0.1 zip
/// didn't).
///
/// To bump it: download the release's UE4SS_vX.Y.Z.zip (or a dev build's
/// UE4SS_vX.Y.Z-N-gCOMMIT.zip), put it in Assets\UE4SS (and update the csproj Include), set
/// FileName, Version, BuildNumber, Commit and Sha256, add a new official release's tag commit to
/// Ue4ssBuild, and update UE4SS's build in THIRD-PARTY-LICENSES.md.
/// </summary>
public static class BundledUe4ss
{
    public const string Version = "4.0.0";

    /// <summary>The release asset's own name.</summary>
    public const string FileName = "UE4SS_v4.0.0.zip";

    /// <summary>Commits after the version's tag, for a dev build; null for an official release.</summary>
    public static readonly int? BuildNumber = null;

    /// <summary>The v4.0.0 tag's commit.</summary>
    public const string Commit = IcarusStarlink.Core.Ue4ss.Ue4ssBuild.Official400Commit;

    /// <summary>SHA-256 of that release asset (matches GitHub's digest for it).</summary>
    public const string Sha256 = "251ad0d1381a241750e036f5918cbf2b5d7ec58ebc608cb2dd09d459d0c81e45";

    /// <summary>"4.0.0 (official release)", for buttons and messages.</summary>
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
