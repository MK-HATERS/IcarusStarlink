namespace IcarusStarlink.Core.Profiles;

/// <summary>A saved merge list — "saved merge list + options + UE4SS set" per the spec. UE4SS staging doesn't exist yet (6.5), so that field joins this record when that phase lands rather than being guessed at now.</summary>
public sealed class Profile
{
    public required string Name { get; set; }

    /// <summary>Extracted_Mods folder names, in merge-queue order (index 0 = lowest priority, matching MergeEngine's own convention).</summary>
    public List<string> MergeQueueFolderNames { get; set; } = [];

    public GameplayOptions Options { get; set; } = new();
}
