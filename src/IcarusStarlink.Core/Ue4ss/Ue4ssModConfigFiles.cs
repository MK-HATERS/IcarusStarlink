using System.Text.RegularExpressions;

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
public sealed record Ue4ssModConfigFile(string RelativePath, Ue4ssModConfigFormat Format)
{
    /// <summary>The values the mod ships with (config.default.lua beside config.lua): read for Reset, never edited.</summary>
    public bool IsDefaults => Ue4ssModConfigFiles.IsDefaults(RelativePath);
}

/// <summary>
/// Finds the settings files a UE4SS mod ships: config.lua / settings.lua (in the mod folder or
/// Scripts), any .ini or .cfg in the mod folder, and everything under its config folder. Scripts,
/// enabled.txt, readmes, logs and ArcFission data are never config, and neither is a Scripts
/// module that merely reads the config (mk_config.lua, mt_config.lua: code with functions in it).
/// The main file (config.lua / config.ini in the mod folder) comes first, so it's the one that
/// opens. A <c>config.default.lua</c> (or .ini) is the shipped defaults, the way the MkHaters
/// mods and MkHaters Mod Settings keep them: listed last and marked <see cref="Ue4ssModConfigFile.IsDefaults"/>,
/// so an editor can hide it and use it for "Reset to default".
/// </summary>
public static partial class Ue4ssModConfigFiles
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

        foreach (var file in Files(Path.Combine(modFolder, "Scripts"))
                     .Where(f => Path.GetExtension(f).Equals(".lua", StringComparison.OrdinalIgnoreCase) && NamedLikeSettings(f) && !IsLuaCode(f)))
        {
            Add(file);
        }

        // Stable order: the main file first (config.lua / config.ini in the mod folder), the
        // rest as found, the shipped defaults last.
        return [.. found.OrderBy(Rank).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The mod has a config file a person edits (its shipped defaults alone don't count).</summary>
    public static bool HasAny(string modFolder)
    {
        try
        {
            return Find(modFolder).Any(f => !f.IsDefaults);
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

    /// <summary>"config.default.lua", "config.defaults.ini", "settings.default.lua": the values the mod ships with.</summary>
    public static bool IsDefaults(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        return name.EndsWith(".default", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".defaults", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where this config's shipped defaults would be: config.lua → config.default.lua in the same folder.</summary>
    public static string DefaultsPathFor(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(relativePath) + ".default" + Path.GetExtension(relativePath);
        return Path.Combine(directory, name);
    }

    private static int Rank(Ue4ssModConfigFile file)
    {
        if (file.IsDefaults)
        {
            return 3;
        }

        var inModFolder = string.IsNullOrEmpty(Path.GetDirectoryName(file.RelativePath));
        var name = Path.GetFileNameWithoutExtension(file.RelativePath);
        return inModFolder && name.Equals("config", StringComparison.OrdinalIgnoreCase) ? 0
            : inModFolder && name.Equals("settings", StringComparison.OrdinalIgnoreCase) ? 1
            : 2;
    }

    private static IEnumerable<string> Files(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase) : [];

    private static bool NamedLikeSettings(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("config", StringComparison.OrdinalIgnoreCase)
            || name.Contains("settings", StringComparison.OrdinalIgnoreCase)
            || name.Equals("options", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A Scripts file named like a config that is really the mod's code: the module that reads
    /// config.lua (mk_config.lua), or the one holding the defaults in Lua (mt_config.lua). Code
    /// defines functions; a settings file is only data.
    /// </summary>
    private static bool IsLuaCode(string path)
    {
        try
        {
            return CodeMarker().IsMatch(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\bfunction\b|\brequire\s*[\(""']")]
    private static partial Regex CodeMarker();
}
