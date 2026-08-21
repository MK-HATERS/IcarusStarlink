namespace IcarusStarlink.Core.Profiles;

/// <summary>A saved merge list — "saved merge list + options + UE4SS set" per the spec, but options/UE4SS staging don't exist yet (6.4/6.5), so those fields join this record when those phases land rather than being guessed at now.</summary>
public sealed class Profile
{
    public required string Name { get; set; }

    /// <summary>Extracted_Mods folder names, in merge-queue order (index 0 = lowest priority, matching MergeEngine's own convention).</summary>
    public List<string> MergeQueueFolderNames { get; set; } = [];
}
