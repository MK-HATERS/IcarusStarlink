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

    /// <summary>
    /// Extracted_Mods folder names, in merge-queue order (index 0 = lowest priority, matching
    /// MergeEngine's own convention). Can include opaque .pak entries (LibraryEntry.IsOpaquePak)
    /// alongside real EXMOD mods — those are never field-merged (no .EXMOD to merge against);
    /// instead Rebuild unpacks each one and folds its contents into the same staging folder as the
    /// merge output, so they end up in the one final merged pak.
    /// </summary>
    public List<string> MergeQueueFolderNames { get; set; } = [];

    public GameplayOptions Options { get; set; } = new();

    /// <summary>
    /// Conflict winners picked in Merge &amp; Install's conflict review: "file|row|field" → the
    /// winning mod's Library folder name ("folder/pak name" for one pak of a prebuilt entry, or
    /// "Built-in gameplay options" / "ArcFission Library data"). By mod, not queue position, so a
    /// pick survives reordering the queue and adding or removing other mods; a pick whose conflict
    /// isn't there right now just waits. Older profiles saved the mod's display name; those are
    /// moved to the folder name when the profile loads, where only one mod has that name.
    /// </summary>
    public Dictionary<string, string> ConflictWinners { get; set; } = [];
}
