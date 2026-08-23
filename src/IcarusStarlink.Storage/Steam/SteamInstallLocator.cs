using System.Runtime.Versioning;
using IcarusStarlink.Core.Steam;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace IcarusStarlink.Storage.Steam;

/// <summary>
/// Real registry + filesystem I/O side of ISteamInstallLocator — SteamLibraryVdf (Core) holds the
/// pure, unit-testable parsing logic; this just wires that up to the real Steam install. Confirmed
/// against the user's own real machine during Phase 7.5 planning: SteamPath is stored under
/// HKEY_CURRENT_USER\SOFTWARE\Valve\Steam with forward slashes and a lowercase drive letter (e.g.
/// "c:/program files (x86)/steam"), not the backslash form Explorer would show.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamInstallLocator(ILogger<SteamInstallLocator> logger) : ISteamInstallLocator
{
    /// <summary>Icarus's real Steam App ID — confirmed against the user's own appmanifest_1149460.acf during Phase 5 planning.</summary>
    private const string IcarusAppId = "1149460";

    public string? FindIcarusContentPath()
    {
        var steamPath = GetSteamPath();
        if (steamPath is null)
        {
            return null;
        }

        foreach (var library in EnumerateLibraryRoots(steamPath))
        {
            try
            {
                var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{IcarusAppId}.acf");
                if (File.Exists(manifestPath))
                {
                    return Path.Combine(library, "steamapps", "common", "Icarus", "Icarus", "Content");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A library root that no longer exists (removed drive, offline NAS share) — same
                // "best effort, never throws" contract as the rest of this class; try the next one.
                logger.LogDebug(ex, "Couldn't check Steam library '{Library}' for Icarus.", library);
            }
        }

        return null;
    }

    public string? TryGetPersonaName(string steamId64)
    {
        try
        {
            var steamPath = GetSteamPath();
            if (steamPath is null)
            {
                return null;
            }

            var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(vdfPath))
            {
                return null;
            }

            // loginusers.vdf keys each account block by SteamID64 with a "PersonaName" line inside
            // (confirmed against the real file). A targeted scan beats a full VDF parser here: find
            // the ID's block, then the first PersonaName after it but before the next account block.
            var lines = File.ReadAllLines(vdfPath);
            var inBlock = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed == $"\"{steamId64}\"")
                {
                    inBlock = true;
                    continue;
                }

                if (inBlock)
                {
                    // A new quoted-number key at this level means the next account's block started.
                    if (trimmed.Length > 2 && trimmed[0] == '"' && trimmed[^1] == '"' && trimmed[1..^1].All(char.IsDigit))
                    {
                        return null;
                    }

                    var match = System.Text.RegularExpressions.Regex.Match(trimmed, "^\"PersonaName\"\\s+\"(.*)\"$");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't resolve a persona name for {SteamId}.", steamId64);
            return null;
        }
    }

    private string? GetSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var value = key?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Replace('/', Path.DirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't read Steam's own install path from the registry.");
            return null;
        }
    }

    /// <summary>The main Steam install is always itself a library too, even though it's not listed inside its own libraryfolders.vdf.</summary>
    private IEnumerable<string> EnumerateLibraryRoots(string steamPath)
    {
        yield return steamPath;

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            yield break;
        }

        string vdfContent;
        try
        {
            vdfContent = File.ReadAllText(vdfPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Couldn't read '{VdfPath}'.", vdfPath);
            yield break;
        }

        foreach (var library in SteamLibraryVdf.ParseLibraryPaths(vdfContent))
        {
            yield return library;
        }
    }
}
