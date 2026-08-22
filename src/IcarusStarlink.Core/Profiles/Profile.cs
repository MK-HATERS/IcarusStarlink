namespace IcarusStarlink.Core.Profiles;

/// <summary>
/// A saved merge list + gameplay options. UE4SS mods are deliberately NOT part of a profile
/// (Phase 8.5 reworked this from the original "saved merge list + options + UE4SS set" design) —
/// UE4SS mods are enabled/disabled directly via Library's own UE4SS tab, a single global on/off
/// state independent of which profile is active, not something that round-trips through
/// save/load/export/import the way the merge queue does.
/// </summary>
public sealed class Profile
{
    public required string Name { get; set; }

    /// <summary>Extracted_Mods folder names, in merge-queue order (index 0 = lowest priority, matching MergeEngine's own convention).</summary>
    public List<string> MergeQueueFolderNames { get; set; } = [];

    public GameplayOptions Options { get; set; } = new();
}
