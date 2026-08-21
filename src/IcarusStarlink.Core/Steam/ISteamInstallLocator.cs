namespace IcarusStarlink.Core.Steam;

/// <summary>Auto-detects the real Icarus install path (Phase 7.5) — Settings' "Auto-detect" button, an alternative to manually Browse-ing to it.</summary>
public interface ISteamInstallLocator
{
    /// <summary>
    /// Null if Steam itself, or Icarus specifically, couldn't be found — never throws, since this
    /// is a best-effort convenience the user can always fall back to Browse… for.
    /// </summary>
    string? FindIcarusContentPath();
}
