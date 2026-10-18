namespace IcarusStarlink.Core.Ue4ss;

/// <summary>How a mod's config file is edited.</summary>
public enum Ue4ssModConfigFormat
{
    /// <summary>Key = value lines in [sections] (config.ini, Mod.cfg).</summary>
    Ini,

    /// <summary>A Lua table of settings (config.lua).</summary>
    Lua,

    /// <summary>Anything else (config.json, settings.toml): edited as plain text.</summary>
    Text,
}

/// <summary>A config file inside a UE4SS mod folder, as a path relative to that folder.</summary>
public sealed record Ue4ssModConfigFile(string RelativePath, Ue4ssModConfigFormat Format);

/// <summary>
/// Finds the settings files a UE4SS mod ships: config.lua / settings.lua (in the mod folder or
/// Scripts), any .ini or .cfg in the mod folder, and everything under its config folder. Scripts,
/// enabled.txt, readmes, logs and ArcFission data are never config.
/// </summary>
public static class Ue4ssModConfigFiles
{
    private static readonly string[] SettingsExtensions = [".ini", ".cfg", ".lua", ".json", ".toml", ".txt"];

    public static IReadOnlyList<Ue4ssModConfigFile> Find(string modFolder)
    {
        if (!Directory.Exists(modFolder))
        {
            return [];
        }

        var found = new List<Ue4ssModConfigFile>();
        void Add(string path)
        {
            var relative = Path.GetRelativePath(modFolder, path);
            if (!found.Any(f => f.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new Ue4ssModConfigFile(relative, FormatOf(path)));
            }
        }

        foreach (var file in Files(modFolder))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".ini" or ".cfg" || (SettingsExtensions.Contains(ext) && NamedLikeSettings(file)))
            {
                Add(file);
            }
        }

        foreach (var folder in new[] { "config", "configs", "settings" })
        {
            var dir = Path.Combine(modFolder, folder);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                             .Where(f => SettingsExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    Add(file);
                }
            }
        }

        foreach (var file in Files(Path.Combine(modFolder, "Scripts")).Where(f => Path.GetExtension(f).Equals(".lua", StringComparison.OrdinalIgnoreCase) && NamedLikeSettings(f)))
        {
            Add(file);
        }

        return found;
    }

    public static bool HasAny(string modFolder)
    {
        try
        {
            return Find(modFolder).Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static Ue4ssModConfigFormat FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ini" or ".cfg" => Ue4ssModConfigFormat.Ini,
        ".lua" => Ue4ssModConfigFormat.Lua,
        _ => Ue4ssModConfigFormat.Text,
    };

    private static IEnumerable<string> Files(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase) : [];

    private static bool NamedLikeSettings(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("config", StringComparison.OrdinalIgnoreCase)
            || name.Contains("settings", StringComparison.OrdinalIgnoreCase)
            || name.Equals("options", StringComparison.OrdinalIgnoreCase);
    }
}
