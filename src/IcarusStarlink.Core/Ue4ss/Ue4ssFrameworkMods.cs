using System.Text.Json;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// UE4SS's own mods (the built-ins that come with the loader, and Mods\shared, its Lua library):
/// never offered as the player's mods, never moved to staging by Uninstall, never uploaded one by one.
/// </summary>
public static class Ue4ssFrameworkMods
{
    /// <summary>The largest mods.json read; the real one is about 1 KB.</summary>
    private const long MaxModsJsonBytes = 1024 * 1024;

    /// <summary>
    /// The built-ins of every build Starlink knows. 3.0.1 also ships ActorDumperMod and
    /// jsbLuaProfilerMod and has no mods.json (only mods.txt), so its built-ins are known by name.
    /// </summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "shared", "Keybinds", "BPML_GenericFunctions", "BPModLoaderMod", "CheatManagerEnablerMod",
        "ConsoleCommandsMod", "ConsoleEnablerMod", "LineTraceMod", "SplitScreenMod",
        "ActorDumperMod", "jsbLuaProfilerMod",
    };

    /// <summary>
    /// <see cref="Known"/> plus every mod_name in the installed build's own mods.json (its bundled mod
    /// manifest: a real install lists exactly its built-ins there, never the player's mods). The
    /// failure direction is deliberate: a missing, oversized or broken mods.json adds nothing, so an
    /// unknown mod counts as the player's and is kept.
    /// </summary>
    public static IReadOnlySet<string> In(string? modsFolder)
    {
        if (string.IsNullOrEmpty(modsFolder))
        {
            return Known;
        }

        try
        {
            var modsJson = new FileInfo(Path.Combine(modsFolder, "mods.json"));
            if (!modsJson.Exists || modsJson.Length > MaxModsJsonBytes)
            {
                return Known;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(modsJson.FullName));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Known;
            }

            var names = new HashSet<string>(Known, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("mod_name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } modName)
                {
                    names.Add(modName);
                }
            }

            return names;
        }
        catch (Exception)
        {
            // One bad file must not stop a listing: nothing beyond the known names counts as UE4SS's.
            return Known;
        }
    }
}
