namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// The short label that tells two parts of ONE Nexus download apart. A real bundle (WirelessPower)
/// ships "ZZ_WirelessPower_Icon_P.pak" and "ZZ_WirelessPower_Model_P.pak" next to its UE4SS folder;
/// both paks used to be renamed to the Nexus title, so the Library showed three rows all called
/// "Wireless Power" and the user read the extras as a dependency that had been downloaded behind
/// their back. The part label is the pak's own name with the loader-ordering prefix ("ZZ_"), the
/// "_P" patch suffix and every token that merely repeats the mod's own name stripped away:
/// "Icon", "Model". The caller then names the entry "Wireless Power — Model".
/// </summary>
public static class ModPartLabel
{
    public static string FromPakName(string pakFolderOrFileName, string? modDisplayName)
    {
        var name = Path.GetFileNameWithoutExtension(pakFolderOrFileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        // Loader-ordering prefixes mod authors use so their pak sorts after the game's own.
        while (name.Length > 2 && (name.StartsWith("ZZ_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Z_", StringComparison.OrdinalIgnoreCase)))
        {
            name = name[(name.IndexOf('_') + 1)..];
        }

        if (name.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^2];
        }

        var modTokens = Normalize(modDisplayName ?? "");
        var kept = name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => modTokens.Length == 0 || !modTokens.Contains(Normalize(token), StringComparison.Ordinal))
            .ToList();

        return kept.Count > 0 ? string.Join(" ", kept) : name;
    }

    private static string Normalize(string text) =>
        new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
