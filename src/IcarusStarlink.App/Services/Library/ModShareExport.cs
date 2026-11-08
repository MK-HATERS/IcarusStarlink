using System.IO;
using IcarusStarlink.Core.IO;
using IcarusStarlink.PakIO.Container;

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

    /// <summary>"&lt;name&gt; &lt;version&gt;.EXMODZ", with characters Windows won't put in a file name turned into '_'.</summary>
    public static string DefaultFileName(string name, string version)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "My mod" : name.Trim();
        if (!string.IsNullOrWhiteSpace(version))
        {
            baseName += " " + version.Trim();
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. baseName.Select(c => invalid.Contains(c) ? '_' : c)]) + ".EXMODZ";
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
                AtomicFile.Write(outputPath, temp =>
                {
                    using var stream = File.Create(temp);
                    ExmodzArchive.Write(stream, contents);
                });
            },
            cancellationToken);

    /// <summary>The mod without Starlink's own notes about this copy (a starlink-*.json at the folder's top).</summary>
    internal static ExmodPackageContents ForSharing(ExmodPackageContents contents) =>
        contents with
        {
            Assets = [.. contents.Assets.Where(a => !IsStarlinkOwnFile(a.RelativePath))],
        };

    private static bool IsStarlinkOwnFile(string relativePath) =>
        !relativePath.Contains('/') && !relativePath.Contains('\\')
        && relativePath.StartsWith("starlink-", StringComparison.OrdinalIgnoreCase)
        && relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
