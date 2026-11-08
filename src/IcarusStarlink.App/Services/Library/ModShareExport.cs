using System.IO;
using IcarusStarlink.Core.IO;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Export for sharing: one of the user's own mods (LibraryEntry.IsOwnMod) written as an .EXMODZ
/// a friend can import, or the user can post on Nexus. The zip has the same layout classic IMM
/// writes and reads (ExmodzArchive.Write: "Extracted Mods/&lt;fileName&gt;.EXMOD" plus the asset
/// tree), every entry path relative to the mod folder. Nothing from the Library goes in: pin,
/// notes and source live in Library_Meta, not in the mod folder, and Starlink's own notes beside
/// the mod (starlink-*.json at the folder's top, such as the player points settings) are left out.
/// </summary>
public static class ModShareExport
{
    public const string FileFilter = "Icarus mod for sharing (*.EXMODZ)|*.EXMODZ";
    public const string Extension = ".EXMODZ";

    /// <summary>
    /// "&lt;name&gt; &lt;version&gt;.EXMODZ" as a name Windows takes as it is: characters it won't put in
    /// a file name turned into '_', trailing dots and spaces dropped, and a device name such as
    /// "CON" given a leading '_'. "My mod" when nothing is left.
    /// </summary>
    public static string DefaultFileName(string name, string version)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "My mod" : name.Trim();
        if (!string.IsNullOrWhiteSpace(version))
        {
            baseName += " " + version.Trim();
        }

        return AssetPathGuard.SanitizeToSimpleFileName(baseName, emptyFallback: "My mod") + Extension;
    }

    /// <summary>
    /// The picked path ending in .EXMODZ: a typed ".EXMOD" becomes ".EXMODZ" (a zip under a mod
    /// file's extension would be read as a broken mod), and any other name gets ".EXMODZ" added.
    /// </summary>
    public static string WithShareExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, Extension, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return string.Equals(extension, ".EXMOD", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(path, Extension)
            : path + Extension;
    }

    /// <summary>True when path is folder itself or anywhere under it (case-blind, as Windows paths are).</summary>
    public static bool IsInsideFolder(string path, string folder)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(folder);
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs on a worker thread. Writes through a temp file beside outputPath that replaces it only
    /// once the whole zip is written, so a failure or a cancel leaves a file already there as it was.
    /// </summary>
    public static Task ExportAsync(string modFolderPath, string outputPath, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var contents = ForSharing(ExmodFolder.Read(modFolderPath));
                cancellationToken.ThrowIfCancellationRequested();
                WriteZip(contents, outputPath);
            },
            cancellationToken);

    /// <summary>The zip, written to a temp file that replaces outputPath only once it is whole.</summary>
    internal static void WriteZip(ExmodPackageContents contents, string outputPath) =>
        AtomicFile.Write(outputPath, temp =>
        {
            using var stream = File.Create(temp);
            ExmodzArchive.Write(stream, contents);
        });

    /// <summary>
    /// The mod without Starlink's own notes about this copy: a starlink-*.json at the folder's top,
    /// and the scale rules ("starlinkScaleRules" in the .EXMOD). The rows those rules built are
    /// already in the file, so a friend's copy works without them, and the classic mod manager
    /// never sees a key it doesn't know. Other keys the .EXMOD came with (ExtraFields) stay: they
    /// came from another tool's file and may matter to it. The mod's own package is not changed.
    /// </summary>
    internal static ExmodPackageContents ForSharing(ExmodPackageContents contents)
    {
        var package = contents.Package;
        if (package.ScaleRules.Count > 0)
        {
            package = ExmodJson.Parse(ExmodJson.Serialize(package));
            package.ScaleRules = [];
        }

        return contents with
        {
            Package = package,
            Assets = [.. contents.Assets.Where(a => !IsStarlinkOwnFile(a.RelativePath))],
        };
    }

    private static bool IsStarlinkOwnFile(string relativePath) =>
        !relativePath.Contains('/') && !relativePath.Contains('\\')
        && relativePath.StartsWith("starlink-", StringComparison.OrdinalIgnoreCase)
        && relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
