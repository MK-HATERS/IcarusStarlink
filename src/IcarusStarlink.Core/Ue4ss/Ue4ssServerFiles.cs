namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// What of the game's UE4SS goes onto a dedicated server, the same for a server on this PC and one
/// reached over FTP (the two used to pick differently, and the FTP side uploaded the
/// game's logs and every mod folder, switched off or not).
/// </summary>
public static class Ue4ssServerFiles
{
    public const string SettingsFileName = "UE4SS-settings.ini";

    /// <summary>
    /// The release's own UE4SS-settings.ini, which the installer writes beside the player's (replaced
    /// on every install; UE4SS itself never reads it): the UE4SS config tab reads it for the settings
    /// the player's file doesn't have yet.
    /// </summary>
    public const string DefaultSettingsFileName = "UE4SS-settings.default.ini";

    /// <summary>
    /// The framework's own mods: they come with the loader, so they are never offered (or uploaded)
    /// one by one. These are the names every known build ships; Ue4ssFrameworkMods.In adds the
    /// installed build's own.
    /// </summary>
    public static IReadOnlySet<string> FrameworkMods => Ue4ssFrameworkMods.Known;

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
        var frameworkMods = Ue4ssFrameworkMods.In(gameModsFolder);
        return [.. Directory.GetDirectories(gameModsFolder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !frameworkMods.Contains(n) && Ue4ssModStart.IsMod(gameModsFolder, n) && Ue4ssModStart.Starts(gameModsFolder, n, list))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }
}
