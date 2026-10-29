using System.IO;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.Services;

/// <summary>The game build on disk now, and the one the mod setup was last made ready for when they differ.</summary>
/// <param name="PreviousBuild">Null when nothing changed (or this is the first check).</param>
public sealed record GameUpdateState(string CurrentBuild, string? PreviousBuild)
{
    public bool IsUpdated => PreviousBuild is not null && !string.Equals(PreviousBuild, CurrentBuild, StringComparison.OrdinalIgnoreCase);

    /// <summary>" (build 25956278)", or nothing for a build known only by data.pak's hash.</summary>
    public string BuildLabel => GameUpdateDetector.IsHashBuild(CurrentBuild) ? "" : $" (build {CurrentBuild})";
}

/// <summary>
/// "Has Icarus updated?" from Steam's own record of the installed build: the "buildid" in
/// steamapps\appmanifest_1149460.acf of the library the game is in (found from the Icarus Content
/// folder). When that file can't be found (a copied game folder, a non-Steam install), data.pak's
/// hash stands in. The build the mod setup was last made ready for is AppSettings.LastSeenGameBuildId.
/// </summary>
public sealed class GameUpdateDetector(ISettingsService settingsService, IUnrealPakService unrealPakService)
{
    public const string IcarusAppId = "1149460";

    private const string HashPrefix = "data-";

    /// <summary>A build known only by data.pak's hash (no Steam manifest found).</summary>
    public static bool IsHashBuild(string build) => build.StartsWith(HashPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The game's Steam manifest: the Content folder sits under &lt;library&gt;\steamapps\common\Icarus\Icarus\Content,
    /// so the nearest "steamapps" folder above it holds appmanifest_1149460.acf. Null when there's none.
    /// </summary>
    public static string? FindAppManifest(string icarusContentPath)
    {
        var fileName = $"appmanifest_{IcarusAppId}.acf";
        try
        {
            for (var directory = new DirectoryInfo(icarusContentPath); directory is not null; directory = directory.Parent)
            {
                var candidate = string.Equals(directory.Name, "steamapps", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(directory.FullName, fileName)
                    : Path.Combine(directory.FullName, "steamapps", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
        }

        return null;
    }

    /// <summary>The "buildid" from an appmanifest's text ("25956278"); null when it has none.</summary>
    public static string? ReadBuildId(string acfText) =>
        SteamCmd.ReadAppManifest(acfText).BuildId is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>The build on disk now: Steam's build id, else data.pak's hash; null when neither can be read (no game folder set).</summary>
    public async Task<string?> ReadCurrentBuildAsync(CancellationToken cancellationToken = default)
    {
        if (settingsService.Current.IcarusContentPath is not { Length: > 0 } contentPath)
        {
            return null;
        }

        var fromSteam = await Task.Run(() =>
        {
            try
            {
                return FindAppManifest(contentPath) is { } manifest ? ReadBuildId(File.ReadAllText(manifest)) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }, cancellationToken);
        if (fromSteam is not null)
        {
            return fromSteam;
        }

        var hash = await unrealPakService.TryGetDataPakHashAsync(contentPath, cancellationToken);
        return hash is { Length: > 0 } ? HashPrefix + hash[..Math.Min(16, hash.Length)].ToLowerInvariant() : null;
    }

    /// <summary>
    /// The build now against the one the mod setup was made ready for. The first check ever just
    /// records the build (nothing to compare with yet), and so does a switch between a Steam
    /// build id and a data.pak hash: the two can't be compared, and saying "updated" would be a guess.
    /// </summary>
    public async Task<GameUpdateState?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (await ReadCurrentBuildAsync(cancellationToken) is not { } current)
        {
            return null;
        }

        var last = settingsService.Current.LastSeenGameBuildId;
        if (last is null || IsHashBuild(last) != IsHashBuild(current))
        {
            MarkReady(current);
            return new GameUpdateState(current, null);
        }

        return string.Equals(last, current, StringComparison.OrdinalIgnoreCase)
            ? new GameUpdateState(current, null)
            : new GameUpdateState(current, last);
    }

    /// <summary>The mod setup is ready for this build (Update everything finished, or the bar was dismissed).</summary>
    public void MarkReady(string build)
    {
        settingsService.Current.LastSeenGameBuildId = build;
        settingsService.Save();
    }
}
