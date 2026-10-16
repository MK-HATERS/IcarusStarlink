using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Pak;

/// <summary>
/// Where a pak's files land inside the game's content tree. The merged pak this app builds mounts
/// everything at GameContent ("../../../Icarus/Content/", the root every "/Game/…" asset path
/// resolves under); a prebuilt pak can mount deeper or shallower, so its extracted files are
/// restaged to their path under Content (RestageUnderGameContent) before anything reads them.
/// </summary>
public static class PakMountPoints
{
    public const string GameContent = "../../../Icarus/Content/";

    /// <summary>
    /// Moves an extracted pak's files to where the game actually sees them under Content. UnrealPak
    /// -Extract writes files relative to the pak's own mount point, and real paks mount at all sorts
    /// of depths — found live on Nexus: four single-table paks mount at "…/Content/data/Traits/" and
    /// the like (so D_Itemable.json came out at the root, invisible to everything that looks under
    /// data/ — the mod imported as an EXMOD with no changes and did nothing), and a day/night pak
    /// mounts at "../../../" with its files under "Icarus/Content/…". After this, a file's path in
    /// the folder is its path under Content, whatever the mount. Files that really live outside
    /// Content can't be placed; they stay where they were and are returned so callers can say so.
    /// </summary>
    public static IReadOnlyList<string> RestageUnderGameContent(string extractedDirectory, string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint) || !Directory.Exists(extractedDirectory))
        {
            return [];
        }

        var mount = mountPoint.Trim().Replace('\\', '/');
        if (!mount.EndsWith('/'))
        {
            mount += "/";
        }

        if (string.Equals(mount, GameContent, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var moves = new List<(string From, string To)>();
        var outside = new List<string>();
        foreach (var file in Directory.GetFiles(extractedDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(extractedDirectory, file).Replace('\\', '/');
            var virtualPath = mount + relative;
            // The mount point is the pak's own data: one like "…/Content/../../../Users/…" passes the
            // prefix test but would climb out of the folder, so any ".." left after it counts as outside.
            if (virtualPath.StartsWith(GameContent, StringComparison.OrdinalIgnoreCase)
                && !virtualPath[GameContent.Length..].Split('/').Any(segment => segment is ".." or "."))
            {
                moves.Add((relative, virtualPath[GameContent.Length..]));
            }
            else
            {
                outside.Add(relative);
                moves.Add((relative, relative));
            }
        }

        if (moves.All(m => string.Equals(m.From, m.To, StringComparison.OrdinalIgnoreCase)))
        {
            return outside;
        }

        var holding = extractedDirectory.TrimEnd(Path.DirectorySeparatorChar) + "_restage_" + Guid.NewGuid().ToString("N");
        Directory.Move(extractedDirectory, holding);
        Directory.CreateDirectory(extractedDirectory);
        foreach (var (from, to) in moves)
        {
            var destination = AssetPathGuard.ResolveWithinDirectory(extractedDirectory, to);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(holding, from.Replace('/', Path.DirectorySeparatorChar)), destination, overwrite: true);
        }

        Directory.Delete(holding, recursive: true);
        return outside;
    }
}
