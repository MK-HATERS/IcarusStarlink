namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Real UE4SS install layout, confirmed against a real Icarus install with UE4SS already set up:
/// the loader (dwmapi.dll + a ue4ss\ folder) lives in Binaries\Win64 — a sibling of Content, not
/// inside it — and mods live one bare folder per mod under Binaries\Win64\ue4ss\Mods\ (no metadata
/// file of their own, unlike EXMOD). Per the spec ("Workshop only stages/installs mod folders; it
/// does not replace UE4SS"), this app never installs or manages the loader itself — only what's
/// under Mods\.
/// </summary>
public static class Ue4ssGamePaths
{
    public static string ResolveModsFolder(string icarusContentPath)
    {
        var trimmed = icarusContentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gameRoot = Path.GetDirectoryName(trimmed)
            ?? throw new ArgumentException($"'{icarusContentPath}' has no parent directory — not a valid Content folder path.", nameof(icarusContentPath));

        return Path.Combine(gameRoot, "Binaries", "Win64", "ue4ss", "Mods");
    }
}
