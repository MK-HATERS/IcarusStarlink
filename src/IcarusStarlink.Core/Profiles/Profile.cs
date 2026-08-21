namespace IcarusStarlink.Core.Profiles;

/// <summary>A saved merge list — "saved merge list + options + UE4SS set" per the spec.</summary>
public sealed class Profile
{
    public required string Name { get; set; }

    /// <summary>Extracted_Mods folder names, in merge-queue order (index 0 = lowest priority, matching MergeEngine's own convention).</summary>
    public List<string> MergeQueueFolderNames { get; set; } = [];

    public GameplayOptions Options { get; set; } = new();

    /// <summary>Staged_UE4SS folder names attached to this profile — order doesn't matter (unlike MergeQueueFolderNames, UE4SS mods don't have field-level merge conflicts to resolve by priority).</summary>
    public List<string> Ue4ssModFolderNames { get; set; } = [];
}
