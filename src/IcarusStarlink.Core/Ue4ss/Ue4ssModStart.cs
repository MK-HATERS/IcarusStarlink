using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Whether UE4SS starts a mod whose folder is in a Mods folder, by its own rule (RE-UE4SS
/// UE4SSProgram::start_mods, and every UE4SS.log: "Starting mods (from mods.txt…)", then "(from
/// enabled.txt…)"): first every "Name : 1" line of mods.txt, in order, then every folder holding an
/// enabled.txt that isn't started yet — so enabled.txt starts a mod even where mods.txt says 0, and
/// a folder with neither is never started (it sits in the game doing nothing).
/// </summary>
public static class Ue4ssModStart
{
    public const string EnabledFileName = "enabled.txt";
    public const string ListFileName = "mods.txt";

    /// <summary>The Mods folder's mods.txt (empty when it has none): read it once for a whole listing.</summary>
    public static Ue4ssModsTxt ReadList(string modsFolder) => Ue4ssModsTxt.Load(Path.Combine(modsFolder, ListFileName));

    /// <summary>
    /// A folder UE4SS takes for a mod at all: a Lua mod's Scripts\main.lua or a C++ mod's
    /// dlls\main.dll. Anything else (its own shared\ library folder) is never started, nor meant to be.
    /// </summary>
    public static bool IsMod(string modsFolder, string name) =>
        File.Exists(Path.Combine(modsFolder, name, "Scripts", "main.lua")) || File.Exists(Path.Combine(modsFolder, name, "dlls", "main.dll"));

    /// <summary>
    /// UE4SS's own folders in Mods that aren't mods: shared\ holds the Lua library its mods load
    /// (UEHelpers and the type files), with no main.lua or main.dll. The bundled UE4SS zip's Mods
    /// folder has eight mods (BPML_GenericFunctions, BPModLoaderMod, CheatManagerEnablerMod,
    /// ConsoleCommandsMod, ConsoleEnablerMod, Keybinds, LineTraceMod, SplitScreenMod), mods.txt,
    /// mods.json and shared\; only shared\ is not a mod. Taking it out of the game (report
    /// 2026-10-28: unticked in the Library with "Show UE4SS built-ins" on) broke every Lua mod that
    /// uses it, and ticking it again wrote an enabled.txt into it. It is always on: never moved,
    /// never switched, never deleted.
    /// </summary>
    public static readonly IReadOnlyList<string> FrameworkFolderNames = ["shared"];

    /// <summary>One of <see cref="FrameworkFolderNames"/>: part of UE4SS, always on.</summary>
    public static bool IsFrameworkFolder(string name) => FrameworkFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public const string FrameworkFolderReason = "it's part of UE4SS (the Lua library its mods use), so it's always on";

    public static bool HasEnabledFile(string modsFolder, string name) => File.Exists(Path.Combine(modsFolder, name, EnabledFileName));

    /// <summary>UE4SS starts it: mods.txt says 1, or its folder has enabled.txt.</summary>
    public static bool Starts(string modsFolder, string name, Ue4ssModsTxt list) => list.IsEnabled(name) == true || HasEnabledFile(modsFolder, name);

    /// <summary>Why a mod in the folder isn't started, in a few words; null when it is.</summary>
    public static string? WhyNotStarted(string modsFolder, string name, Ue4ssModsTxt list) =>
        Starts(modsFolder, name, list) ? null
        : list.IsEnabled(name) == false ? "mods.txt switches it off (0) and it has no enabled.txt"
        : "it has no enabled.txt and no line in mods.txt";

    /// <summary>Makes UE4SS start the mod at the next game start: an empty enabled.txt in its folder (which wins over a 0 in mods.txt).</summary>
    public static void SwitchOn(string modsFolder, string name)
    {
        var path = Path.Combine(modsFolder, name, EnabledFileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "");
        }
    }
}
