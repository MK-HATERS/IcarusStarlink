using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Which UE4SS mod folder names exist right now: staged (disabled) ones, and the enabled ones in
/// the game's own Mods folder — a UE4SS mod is in exactly one of the two. Built four slightly
/// different ways on the Downloads page before (a review on 2026-10-23), and the newer-file check
/// for updates missed every enabled mod.
/// </summary>
public sealed class Ue4ssInstalledNames(IUe4ssModRepository repository, ISettingsService? settings)
{
    /// <summary>Enabled mods in the game's Mods folder; empty when the game path isn't set.</summary>
    public IReadOnlyList<string> InGame() =>
        settings?.Current.IcarusContentPath is { } contentPath && !string.IsNullOrWhiteSpace(contentPath)
            ? repository.ListInstalledInGame(Ue4ssGamePaths.ResolveModsFolder(contentPath))
            : [];

    /// <summary>Staged plus in the game, each name once.</summary>
    public IReadOnlyList<string> All() =>
        [.. repository.GetAll().Concat(InGame()).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The names a new staging must avoid (taken in the game), minus <paramref name="ownNames"/> — a
    /// reinstall must land under its own earlier name even while the game still holds that copy.
    /// Null when the game path isn't set: nothing to check against.
    /// </summary>
    public List<string>? InGameExcept(IReadOnlySet<string>? ownNames) =>
        settings?.Current.IcarusContentPath is { } contentPath && !string.IsNullOrWhiteSpace(contentPath)
            ? [.. InGame().Where(name => ownNames is null || !ownNames.Contains(name))]
            : null;
}
