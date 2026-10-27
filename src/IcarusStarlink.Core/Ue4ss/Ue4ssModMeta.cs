namespace IcarusStarlink.Core.Ue4ss;

/// <summary>The sidecar content for a UE4SS mod's Nexus link — a UE4SS mod carries no metadata file of its own (see IUe4ssModRepository), so this is the only place a folder's Nexus mod ID/version can live.</summary>
public sealed class Ue4ssModMeta
{
    public int? NexusModId { get; set; }
    public string? NexusVersion { get; set; }

    /// <summary>The exact Nexus file this copy came from (a mod can offer several side by side, like the variants of
    /// Deyvid's All-in-One) and that file's own version. Update checks follow this file's own update chain instead of
    /// the mod's headline version. Null for anything not downloaded through Starlink's Nexus pipeline.</summary>
    public int? NexusFileId { get; set; }

    public string? NexusFileVersion { get; set; }

    /// <summary>The mod page's own title, recorded at the same Nexus lookup as NexusVersion — the UE4SS tab shows it under the folder name so a staged mod reads as the thing the user downloaded, not just a folder.</summary>
    public string? NexusName { get; set; }

    /// <summary>
    /// The minimum UE4SS loader version this mod needs, as the user typed it (e.g. "3.0.1") — set
    /// via the UE4SS tab's own "Set minimum UE4SS version…" prompt. This can only ever be manual: a
    /// UE4SS mod carries no manifest file of its own to read a real requirement from (see this
    /// class's own summary), so there is nothing to scrape or infer it from automatically. Null means
    /// the user hasn't declared one — never treated as a warning. See Ue4ssVersionComparer for how
    /// this gets compared against the installed loader's own Ue4ssLoaderStatus.InstalledVersion.
    /// </summary>
    public string? MinUe4ssVersion { get; set; }

    /// <summary>
    /// What the last update did to the mod's config files (Ue4ssModConfigCarryOver): the settings
    /// it added and the ones that no longer exist, for the user to look over in Edit config. Null or
    /// empty once reviewed (or when an update changed nothing worth a look).
    /// </summary>
    public List<Ue4ssConfigReview>? ConfigReviews { get; set; }
}

/// <summary>One config file after an update: what changed between the player's file and the new version's, so the editor can point it out.</summary>
public sealed class Ue4ssConfigReview
{
    /// <summary>The file, relative to the mod folder ("config.lua").</summary>
    public string File { get; set; } = "";

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The new version's name, when known ("1.4.0").</summary>
    public string? Version { get; set; }

    /// <summary>Settings this version has that the player's file didn't: at the mod's defaults.</summary>
    public List<string> Added { get; set; } = [];

    /// <summary>Settings the player's file had that this version doesn't (renamed or dropped), with the player's values: "OldName = 5".</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>Settings whose player value was carried over and differs from what the version ships.</summary>
    public List<string> Kept { get; set; } = [];

    /// <summary>
    /// True when the file was rebuilt on the new version's layout (new comments and order, with the
    /// player's values in it). False when the player's file was kept as it was, because it holds
    /// things the merge can't carry (a table over several lines); new settings are then only named.
    /// </summary>
    public bool MergedIntoNewLayout { get; set; }
}

/// <summary>Reads/writes per-UE4SS-mod Nexus-link metadata keyed by folder name — mirrors LibraryMetaStore's own shape and reasoning (a separate sidecar directory, not a file dropped inside the mod's own folder).</summary>
public interface IUe4ssModMetaStore
{
    Ue4ssModMeta Load(string folderName);

    void Save(string folderName, Ue4ssModMeta meta);

    void Delete(string folderName);

    /// <summary>Every folder name that has a sidecar on disk — used to purge sidecars whose mod folder is long gone (deleted by hand, or renamed), so a stale Nexus link can never attach itself to an unrelated mod that later lands under the same name.</summary>
    IReadOnlyList<string> ListFolderNames();
}
