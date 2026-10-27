using IcarusStarlink.Core.Profiles;

namespace IcarusStarlink.Core.Patches;

/// <summary>
/// The portable, shareable form of a Profile — "Export a patch for friends/servers" per the
/// spec. A friend importing this gets the same named profile + mod list reconstructed on their
/// own machine; they still Rebuild &amp; Install locally, so this never carries a merged pak.
///
/// Format 2 (2026-10-27) adds where each mod came from (Nexus mod and file ids, the catalog
/// entry id), pak mods (referenced, or bundled as their pak files), and the UE4SS mods that are
/// on in the exporter's game with their config files, so a friend gets the whole setup, not only
/// the EXMODs. A format 1 file (no FormatVersion) still imports: its mods are matched by name and
/// author as before.
/// </summary>
public sealed class PatchManifest
{
    public const int CurrentFormatVersion = 2;

    /// <summary>1 for patches written before 2026-10-27 (the field was added with format 2).</summary>
    public int FormatVersion { get; set; } = 1;

    public required string ProfileName { get; set; }

    /// <summary>When it was exported; null in a format 1 file.</summary>
    public DateTime? ExportedAtUtc { get; set; }

    /// <summary>The Starlink that wrote it ("3.4.0"); null in a format 1 file.</summary>
    public string? StarlinkVersion { get; set; }

    /// <summary>Same order as the profile's own MergeQueueFolderNames (index 0 = lowest priority).</summary>
    public List<PatchModEntry> Mods { get; set; } = [];

    public GameplayOptions Options { get; set; } = new();

    /// <summary>The UE4SS (Lua) mods on in the exporter's game, with their settings when <see cref="IncludesConfigs"/>.</summary>
    public List<PatchUe4ssEntry> Ue4ssMods { get; set; } = [];

    /// <summary>The exporter chose to carry the UE4SS mods' config files (their own key bindings and values).</summary>
    public bool IncludesConfigs { get; set; }
}

/// <summary>
/// One queued mod's identity for cross-machine matching, plus whether this patch carries its
/// actual content (<see cref="Bundled"/>) — true for mods with no Nexus or catalog source to
/// point a friend at (or that were edited locally), false for mods a friend can download
/// themselves, keeping most patches small.
/// </summary>
public sealed class PatchModEntry
{
    /// <summary>The exporter's own Extracted_Mods folder name — the bundled file's name on export; an importer resolves mods by Nexus id, catalog id, then Name+Author, since folder names aren't shared across machines.</summary>
    public required string FolderName { get; set; }

    public required string Name { get; set; }

    public required string Author { get; set; }

    public required string Version { get; set; }

    public required bool Bundled { get; set; }

    /// <summary>The Nexus mod it came from, when the exporter downloaded it through Starlink.</summary>
    public int? NexusModId { get; set; }

    /// <summary>The exact Nexus file (a mod can offer several side by side).</summary>
    public int? NexusFileId { get; set; }

    /// <summary>The community catalog (IMM Database) entry it came from.</summary>
    public string? CatalogEntryId { get; set; }

    /// <summary>A prebuilt pak mod (no .EXMOD): bundled as its pak files under <c>BundledPaks/&lt;FolderName&gt;/</c>, named in <see cref="PakFileNames"/>.</summary>
    public bool IsPak { get; set; }

    public List<string> PakFileNames { get; set; } = [];
}

/// <summary>A UE4SS mod on in the exporter's game: how to get it, and its settings.</summary>
public sealed class PatchUe4ssEntry
{
    /// <summary>The mod's folder name under ue4ss\Mods ("MkHatersTools").</summary>
    public required string Name { get; set; }

    /// <summary>The Nexus page's title, when known.</summary>
    public string? NexusName { get; set; }

    public int? NexusModId { get; set; }

    public int? NexusFileId { get; set; }

    /// <summary>The version main.lua states, when it does.</summary>
    public string? Version { get; set; }

    /// <summary>On in the exporter's game (always true today: only mods that are on are exported).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The mod's files travel in the patch under <c>BundledUe4ss/&lt;Name&gt;/</c> (no Nexus source to point at).</summary>
    public bool Bundled { get; set; }

    /// <summary>Its config files (config.lua, config.ini, config\*.ini) as text, relative to the mod folder; empty when the exporter left settings out.</summary>
    public List<PatchConfigFile> Configs { get; set; } = [];
}

/// <summary>One config file's text, as it was in the exporter's mod folder.</summary>
public sealed class PatchConfigFile
{
    /// <summary>Relative to the mod folder, with forward slashes ("config.lua", "config/Pets.ini").</summary>
    public required string Path { get; set; }

    public required string Text { get; set; }
}
