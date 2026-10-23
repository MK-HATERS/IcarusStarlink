namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// What of the game's UE4SS goes onto a dedicated server, the same for a server on this PC and one
/// reached over FTP (review 2026-10-23: the two picked differently, and the FTP side uploaded the
/// game's logs and every mod folder, switched off or not).
/// </summary>
public static class Ue4ssServerFiles
{
    public const string SettingsFileName = "UE4SS-settings.ini";

    /// <summary>The framework's own mods: they come with the loader, so they are never offered (or uploaded) one by one.</summary>
    public static IReadOnlySet<string> FrameworkMods { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "shared", "Keybinds", "BPML_GenericFunctions", "BPModLoaderMod", "CheatManagerEnablerMod",
        "ConsoleCommandsMod", "ConsoleEnablerMod", "LineTraceMod", "SplitScreenMod",
    };

    /// <summary>Written by UE4SS or Windows while the game runs: the server writes its own.</summary>
    private static readonly string[] RuntimeExtensions = [".log", ".dmp"];

    /// <summary>
    /// The loader's files in the game's ue4ss folder, relative to it: everything except its Mods
    /// folder and what the game wrote there while running (logs, crash dumps). The settings file is
    /// in the list; callers copy it only where the server has none, so its own settings stay.
    /// </summary>
    public static IReadOnlyList<string> LoaderFiles(string gameUe4ssFolder) =>
        !Directory.Exists(gameUe4ssFolder)
            ? []
            : [.. Directory.EnumerateFiles(gameUe4ssFolder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(gameUe4ssFolder, f))
                .Where(r => !string.Equals(r.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0], "Mods", StringComparison.OrdinalIgnoreCase))
                .Where(r => !RuntimeExtensions.Contains(Path.GetExtension(r).ToLowerInvariant()))
                .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The game's mods that belong on a server: real mods UE4SS starts in the game (Ue4ssModStart), framework mods aside.</summary>
    public static IReadOnlyList<string> ModsOnInGame(string gameModsFolder)
    {
        if (!Directory.Exists(gameModsFolder))
        {
            return [];
        }

        var list = Ue4ssModStart.ReadList(gameModsFolder);
        return [.. Directory.GetDirectories(gameModsFolder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !FrameworkMods.Contains(n) && Ue4ssModStart.IsMod(gameModsFolder, n) && Ue4ssModStart.Starts(gameModsFolder, n, list))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }
}
