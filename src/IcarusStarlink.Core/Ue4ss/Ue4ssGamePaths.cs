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
    public static string ResolveModsFolder(string icarusContentPath) =>
        Path.Combine(ResolveLoaderFolder(icarusContentPath), "Mods");

    /// <summary>Binaries\Win64 itself — where dwmapi.dll (the loader hook) lives, a sibling of the ue4ss\ folder below.</summary>
    public static string ResolveWin64Folder(string icarusContentPath)
    {
        var trimmed = icarusContentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gameRoot = Path.GetDirectoryName(trimmed)
            ?? throw new ArgumentException($"'{icarusContentPath}' has no parent directory — not a valid Content folder path.", nameof(icarusContentPath));

        return Path.Combine(gameRoot, "Binaries", "Win64");
    }

    /// <summary>Binaries\Win64\ue4ss — holds UE4SS.dll itself, its settings, and Mods\. Confirmed against a real working Icarus+UE4SS install (Phase 6.5) — not the flat Binaries\Win64\ layout UE4SS's own generic install docs describe for other games.</summary>
    public static string ResolveLoaderFolder(string icarusContentPath) => Path.Combine(ResolveWin64Folder(icarusContentPath), "ue4ss");

    public static string ResolveDwmapiPath(string icarusContentPath) => Path.Combine(ResolveWin64Folder(icarusContentPath), "dwmapi.dll");

    public static string ResolveLoaderDllPath(string icarusContentPath) => Path.Combine(ResolveLoaderFolder(icarusContentPath), "UE4SS.dll");

    /// <summary>UE4SS writes its own version as the first couple of lines of this log on every launch (e.g. "UE4SS - v3.0.1 Beta #0 - Git SHA #...") — the only real version marker available, since UE4SS.dll itself carries no Win32 file-version resource.</summary>
    public static string ResolveLoaderLogPath(string icarusContentPath) => Path.Combine(ResolveLoaderFolder(icarusContentPath), "UE4SS.log");
}
