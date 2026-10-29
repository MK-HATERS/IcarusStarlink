using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.ArcFission;

namespace IcarusStarlink.PakIO.Patches;

/// <summary>
/// A patch's "config file" is written into a UE4SS mod the player already runs (their game's or a
/// server's), with no question, so it must be settings and nothing else. Before 2026-10-28 any
/// path inside the mod folder was written (Scripts\main.lua included), and a config.lua with no
/// table layout was taken whole: a shared patch could put its own Lua code into a trusted mod.
/// Now only a path Ue4ssModConfigFiles would list as settings, and a Lua file only when it's data
/// (LuaData: tables, strings, numbers, booleans; no calls, no code).
/// </summary>
public static class PatchConfigSafety
{
    public static bool IsSafe(PatchConfigFile config)
    {
        if (config.Text is null || !Ue4ssModConfigFiles.IsConfigPath(config.Path))
        {
            return false;
        }

        if (Ue4ssModConfigFiles.FormatOf(config.Path) != Ue4ssModConfigFormat.Lua)
        {
            return true;
        }

        try
        {
            _ = LuaData.Parse(config.Text);
        }
        catch (FormatException)
        {
            return false;
        }

        return ValuesAreData(config.Text);
    }

    /// <summary>
    /// The import doesn't write a Lua file whole when the mod already has it: it reads the patch's
    /// settings line by line (LuaConfigDocument) and writes each value into the mod's own file
    /// (Ue4ssModConfigCarryOver.ApplyValues), where a value going into a table setting is written
    /// as Lua, not as a string. Two ways that wrote code into a trusted mod although the file as a
    /// whole is data (review 2026-10-28): a string holding Lua (Keys = "{ os.execute('x') }",
    /// written raw into the mod's Keys = { ... }), and lines inside a long comment
    /// (--[==[ ... ]==], or one opened after a value) that the line reader takes as settings. So
    /// every value that would be written as a table must itself be data.
    /// </summary>
    private static bool ValuesAreData(string text)
    {
        foreach (var setting in LuaConfigDocument.Parse(text).Settings)
        {
            var value = setting.Value.Trim();
            if (!value.StartsWith('{'))
            {
                continue;
            }

            try
            {
                _ = LuaData.Parse("return " + value);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Moves every config that isn't safe out of the entry's Configs into its RefusedConfigPaths.</summary>
    public static void Filter(PatchUe4ssEntry mod)
    {
        foreach (var refused in mod.Configs.Where(c => !IsSafe(c)).ToList())
        {
            mod.Configs.Remove(refused);
            mod.RefusedConfigPaths.Add(refused.Path);
        }
    }
}
