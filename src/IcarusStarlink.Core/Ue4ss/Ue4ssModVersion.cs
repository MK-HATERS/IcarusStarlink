using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// The version a UE4SS Lua mod states in its own Scripts\main.lua — <c>local VERSION = "1.9.0"</c>
/// (the user's MkHaters mods and many others do). The Library showed no version for every mod that
/// didn't come from Nexus; this fills it in. Only the first lines are read: the declaration sits at
/// the top, and a long script shouldn't be scanned on every reload.
/// </summary>
public static partial class Ue4ssModVersion
{
    private const int LinesToRead = 60;

    [GeneratedRegex(@"^\s*(?:local\s+)?(?:MOD_?)?VERSION\s*=\s*[""']v?(\d+(?:\.\d+){0,3}[\w.+-]*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex DeclarationPattern();

    /// <summary>The declared version ("1.9.0"), or null when the mod has no main.lua, states none, or can't be read.</summary>
    public static string? TryRead(string modFolder)
    {
        try
        {
            var mainLua = Path.Combine(modFolder, "Scripts", "main.lua");
            return File.Exists(mainLua) ? FromLines(File.ReadLines(mainLua).Take(LinesToRead)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? FromLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = DeclarationPattern().Match(line);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }
}
