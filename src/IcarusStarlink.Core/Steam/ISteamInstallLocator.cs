namespace IcarusStarlink.Core.Steam;

/// <summary>Auto-detects the real Icarus install path (Phase 7.5) — Settings' "Auto-detect" button, an alternative to manually Browse-ing to it.</summary>
public interface ISteamInstallLocator
{
    /// <summary>
    /// Null if Steam itself, or Icarus specifically, couldn't be found — never throws, since this
    /// is a best-effort convenience the user can always fall back to Browse… for.
    /// </summary>
    string? FindIcarusContentPath();

    /// <summary>
    /// The Steam display name for a SteamID64, from Steam's own LOCAL config\loginusers.vdf — no
    /// network involved, unlike the profile-XML lookup the original spec described. Null when Steam
    /// isn't installed or the ID isn't a logged-in account here; same never-throws contract, since
    /// a save slot without a pretty name is still perfectly usable by its numeric ID.
    /// </summary>
    string? TryGetPersonaName(string steamId64);
}
