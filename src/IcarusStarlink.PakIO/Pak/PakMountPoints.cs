namespace IcarusStarlink.PakIO.Pak;

/// <summary>
/// Where a pak's files land inside the game's content tree. The merged pak this app builds mounts
/// everything at GameContent ("../../../Icarus/Content/", the root every "/Game/…" asset path
/// resolves under). A prebuilt pak can carry a DEEPER mount point — found live: WirelessPower's
/// model pak mounts at "../../../Icarus/Content/Mods/WirelessPower/", and UnrealPak -Extract
/// writes its files flat relative to that mount, so folding them into the merged pak at the root
/// put "SM_WirelessNode_Receiver" at /Game/… instead of /Game/Mods/WirelessPower/… and the mod's
/// models silently never loaded. RelativePrefixUnderGameContent gives the subfolder those files
/// have to be restaged under so the merged pak mounts them where the original pak did.
/// </summary>
public static class PakMountPoints
{
    public const string GameContent = "../../../Icarus/Content/";

    /// <summary>
    /// "../../../Icarus/Content/Mods/WirelessPower/" → "Mods/WirelessPower/"; the content root itself,
    /// null, or anything not under the content root → "" (fold at the root, the prior behavior —
    /// a pak mounting outside Content can't be represented in the merged pak at all).
    /// </summary>
    public static string RelativePrefixUnderGameContent(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return "";
        }

        var normalized = mountPoint.Trim().Replace('\\', '/');
        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        if (!normalized.StartsWith(GameContent, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var remainder = normalized[GameContent.Length..];
        return remainder == "/" ? "" : remainder;
    }

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
            if (virtualPath.StartsWith(GameContent, StringComparison.OrdinalIgnoreCase))
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
            var destination = Path.Combine(extractedDirectory, to.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Path.Combine(holding, from.Replace('/', Path.DirectorySeparatorChar)), destination, overwrite: true);
        }

        Directory.Delete(holding, recursive: true);
        return outside;
    }

    /// <summary>True when the mount point is somewhere this app's merged pak can't reproduce (outside the game's Content tree).</summary>
    public static bool IsOutsideGameContent(string? mountPoint) =>
        !string.IsNullOrWhiteSpace(mountPoint)
        && !mountPoint.Trim().Replace('\\', '/').StartsWith(GameContent, StringComparison.OrdinalIgnoreCase);
}
