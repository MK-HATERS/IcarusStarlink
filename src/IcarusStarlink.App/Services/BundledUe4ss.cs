using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The UE4SS release that ships inside Starlink: the official UE4SS_v3.0.1.zip from
/// UE4SS-RE/RE-UE4SS, embedded unchanged in the exe (about 5.5 MB, already compressed), so
/// installing UE4SS needs no download and no extra file next to the app. It is copied out to a
/// temp file only when installing, checked against the hash recorded here, and handed to the same
/// installer a GitHub download uses. The release zip has no licence file, so UE4SS's MIT licence
/// (also embedded) is added to the temp copy and lands in the game's ue4ss folder with it.
/// </summary>
public static class BundledUe4ss
{
    public const string Version = "3.0.1";

    /// <summary>SHA-256 of the official UE4SS_v3.0.1.zip release asset.</summary>
    public const string Sha256 = "4b47d4bceddd2f561a4e395bfa00924ccfc945af576a2d0c613e6537846c57ec";

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
            if (zip.GetEntry("LICENSE") is null)
            {
                await using var license = Open(assembly, LicenseResource);
                await using var entry = zip.CreateEntry("LICENSE").Open();
                await license.CopyToAsync(entry, cancellationToken);
            }
        }

        return path;
    }

    private static Stream Open(Assembly assembly, string name) =>
        assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Starlink was built without its bundled UE4SS ({name}).");

    /// <summary>True when <paramref name="other"/> is a newer UE4SS version than the bundled one ("3.1.0" vs "3.0.1"). Unparseable versions are never newer.</summary>
    public static bool IsNewerThanBundled(string? other) => IsNewer(other, Version);

    /// <summary>True when <paramref name="candidate"/> parses as a newer version than <paramref name="current"/>; a current version that can't be read (not installed, no log yet) counts as older.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var c) && (!TryParse(current, out var cur) || c > cur);

    private static bool TryParse(string? text, out System.Version version)
    {
        var core = (text ?? "").Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return System.Version.TryParse(core, out version!);
    }
}
