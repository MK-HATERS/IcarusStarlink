using System.IO.Compression;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Ue4ss;

/// <summary>
/// UE4SS mod zips have no metadata file of their own (unlike EXMODZ's .EXMOD) — the folder name
/// on disk is the mod's only real identity, confirmed against a real UE4SS install's own Mods
/// folder (each entry there is just a bare directory name, e.g. "NearbyCrafting"). Most real UE4SS
/// mod distributions wrap their content in one top-level folder so unzipping straight into Mods\
/// works correctly; a minority ship loose files with no wrapper. This handles both, and applies
/// the same untrusted-input discipline ExmodzArchive.Read already established — a UE4SS mod zip
/// is exactly as untrusted (an internet download, or a file a stranger shared).
/// </summary>
public static class Ue4ssModArchive
{
    /// <summary>Convenience wrapper for a caller that only needs the derived name, not extraction too — opens and scans the zip on its own. Prefer Open() when a caller needs both (the common case, Import), so the zip is only opened and scanned once.</summary>
    public static string DeriveFolderName(string zipFilePath)
    {
        using var handle = Open(zipFilePath);
        return handle.DerivedFolderName;
    }

    /// <summary>Convenience wrapper for a caller that only needs extraction, not the derived name too.</summary>
    public static void Extract(string zipFilePath, string destinationFolder)
    {
        using var handle = Open(zipFilePath);
        handle.ExtractTo(destinationFolder);
    }

    /// <summary>Opens the zip once and scans its entries once — the caller can read DerivedFolderName and/or call ExtractTo any number of times off the same open archive before disposing.</summary>
    public static Handle Open(string zipFilePath) => new(zipFilePath);

    public sealed class Handle : IDisposable
    {
        private readonly ZipArchive _archive;
        private readonly string? _wrappingFolder;

        internal Handle(string zipFilePath)
        {
            _archive = ZipFile.OpenRead(zipFilePath);
            try
            {
                // A malformed zip can throw while FindSingleWrappingFolder enumerates entries —
                // without this, the constructor would throw before ever returning an object for
                // `using` to dispose, leaking the just-opened archive's file handle.
                _wrappingFolder = FindSingleWrappingFolder(_archive);
            }
            catch
            {
                _archive.Dispose();
                throw;
            }

            DerivedFolderName = _wrappingFolder ?? Path.GetFileNameWithoutExtension(zipFilePath);
        }

        public string DerivedFolderName { get; }

        public void ExtractTo(string destinationFolder)
        {
            var sizeBudget = new ExmodSizeBudget("UE4SS mod archive");

            foreach (var entry in _archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue; // a directory entry, nothing to write
                }

                var relativePath = entry.FullName.Replace('\\', '/');
                if (_wrappingFolder is not null)
                {
                    // Strip the zip's own wrapping folder — the caller's destinationFolder name
                    // (already disambiguated against collisions) is what actually lands on disk,
                    // not whatever the zip happened to be named internally.
                    relativePath = relativePath[(_wrappingFolder.Length + 1)..];
                }

                var destPath = AssetPathGuard.ResolveWithinDirectory(destinationFolder, relativePath);

                // Charging the budget against the zip's own *declared* size (from the central
                // directory) only stops a bomb if the actual decompressed bytes are also bounded to
                // match — a crafted entry can declare a small Length but decompress to far more, and
                // entryStream.CopyTo would happily write all of it before this ever noticed.
                sizeBudget.Charge($"UE4SS mod entry '{entry.FullName}'", entry.Length);

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                using var entryStream = entry.Open();
                using var fileStream = File.Create(destPath);
                BoundedZipEntryCopy.CopyBounded(entryStream, fileStream, entry.Length, entry.FullName);
            }
        }

        public void Dispose() => _archive.Dispose();
    }

    /// <summary>Every entry sits under the same single top-level segment → that's a wrapping folder. Any loose entry directly at the root, or more than one distinct top-level segment, means there isn't one.</summary>
    private static string? FindSingleWrappingFolder(ZipArchive archive)
    {
        string? wrapping = null;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            var segments = entry.FullName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                return null; // a file sitting directly at the zip root — no single wrapping folder
            }

            wrapping ??= segments[0];
            if (!string.Equals(wrapping, segments[0], StringComparison.OrdinalIgnoreCase))
            {
                return null; // more than one top-level folder
            }
        }

        return wrapping;
    }
}
