namespace IcarusStarlink.PakIO.Safety;

/// <summary>
/// Checks a mod's files for the ways a download can be broken before the game ever sees it —
/// the idea borrowed from DRG's mint, trimmed to what actually happens with Icarus mods. Each
/// returns plain user-facing text; callers decide whether a finding refuses the import or just
/// warns. Icarus is Unreal 4.27 and pak-only: pak index version 11, no IoStore containers.
/// </summary>
public static class ModLints
{
    /// <summary>The pak index version UE 4.27 (and so Icarus) reads; a newer engine's packer writes a higher number the game can't open.</summary>
    public const int IcarusPakVersion = 11;

    private static readonly byte[] PakMagicLittleEndian = [0xE1, 0x12, 0x6F, 0x5A];

    /// <summary>
    /// The pak index version from the file's footer (the 4 bytes after the 0x5A6F12E1 magic that
    /// sits near the end of every Unreal pak), or null when no footer is found — a truncated file
    /// or not a pak at all. Never throws for an unreadable file.
    /// </summary>
    public static int? TryReadPakVersion(string pakFilePath)
    {
        try
        {
            using var stream = new FileStream(pakFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var tailLength = (int)Math.Min(stream.Length, 4096);
            if (tailLength < 8)
            {
                return null;
            }

            stream.Seek(-tailLength, SeekOrigin.End);
            var tail = new byte[tailLength];
            stream.ReadExactly(tail);

            // The LAST occurrence: the magic can in principle also appear inside the index data.
            for (var i = tail.Length - 8; i >= 0; i--)
            {
                if (tail.AsSpan(i, 4).SequenceEqual(PakMagicLittleEndian))
                {
                    return BitConverter.ToInt32(tail, i + 4);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A ".pak" with no pak footer at all: a web page or error text saved under that name (a
    /// catalog link that points at a page, not the file), or a cut-off copy. Refused with the
    /// reason — it used to land in the Library as an opaque pak mod and go into the game with the
    /// next Merge &amp; Install (review 2026-10-28).
    /// </summary>
    public static string? CheckIsPak(string pakFilePath) =>
        File.Exists(pakFilePath) && TryReadPakVersion(pakFilePath) is null
            ? $"'{Path.GetFileName(pakFilePath)}' isn't a working .pak file — it may be a broken or unfinished download, or a web page saved under that name. Download it again, or ask the mod author."
            : null;

    /// <summary>A pak packed for a newer engine than Icarus runs — refuse it with the reason rather than import something the game will reject.</summary>
    public static string? CheckPakVersion(string pakFilePath)
    {
        var version = TryReadPakVersion(pakFilePath);
        return version is { } v && v > IcarusPakVersion
            ? $"'{Path.GetFileName(pakFilePath)}' was packed for a newer Unreal Engine (pak version {v}; Icarus reads version {IcarusPakVersion}) — the game can't load it. Ask the mod author for a UE 4.27 build."
            : null;
    }

    /// <summary>IoStore containers (.ucas/.utoc) are a UE5-era format Icarus doesn't use; a download built from them can't be installed by any means this app has.</summary>
    public static string? CheckForIoStoreContainers(IEnumerable<string> filePaths)
    {
        var container = filePaths.FirstOrDefault(f =>
            f.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
        return container is null
            ? null
            : $"Contains an IoStore container ('{Path.GetFileName(container)}') — that's a UE5 packaging format Icarus (UE 4.27) can't load. This download isn't usable as-is.";
    }

    /// <summary>
    /// A cooked asset is a .uasset/.uexp PAIR; one half without the other (a packaging mistake, a
    /// partial copy) loads as corrupt in-game. Reported, not refused — the rest of the mod may be fine.
    /// </summary>
    public static IReadOnlyList<string> CheckSplitAssetPairs(IEnumerable<string> filePaths)
    {
        var set = new HashSet<string>(filePaths.Select(f => f.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        var findings = new List<string>();
        foreach (var path in set)
        {
            if (path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !set.Contains(Path.ChangeExtension(path, ".uexp")))
            {
                findings.Add($"'{Path.GetFileName(path)}' has no matching .uexp — this asset will be broken in-game.");
            }
            else if (path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase) && !set.Contains(Path.ChangeExtension(path, ".uasset")))
            {
                findings.Add($"'{Path.GetFileName(path)}' has no matching .uasset — this asset will be broken in-game.");
            }
        }

        return findings;
    }
}
