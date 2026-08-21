namespace IcarusStarlink.Core.Profiles;

/// <summary>
/// The portable, shareable form of a Profile — "Export a patch for friends/servers" per the
/// spec. A friend importing this gets the same named profile + mod list reconstructed on their
/// own machine; they still Rebuild &amp; Install locally, so this never carries a merged pak.
/// </summary>
public sealed class PatchManifest
{
    public required string ProfileName { get; set; }

    /// <summary>Same order as the profile's own MergeQueueFolderNames (index 0 = lowest priority).</summary>
    public List<PatchModEntry> Mods { get; set; } = [];

    public GameplayOptions Options { get; set; } = new();
}

/// <summary>
/// One queued mod's identity for cross-machine matching, plus whether this patch carries its
/// actual EXMODZ content (<see cref="Bundled"/>) — true for mods with no community-catalog
/// listing (no download URL to point a friend at instead) or that were edited locally, false
/// for mods a friend can get from Downloads themselves, keeping most patches small.
/// </summary>
public sealed class PatchModEntry
{
    /// <summary>The exporter's own Extracted_Mods folder name — only meaningful for re-deriving the bundled EXMODZ's own file name on export; an importer resolves mods by Name+Author instead, since folder names aren't shared across machines.</summary>
    public required string FolderName { get; set; }

    public required string Name { get; set; }

    public required string Author { get; set; }

    public required string Version { get; set; }

    public required bool Bundled { get; set; }
}
