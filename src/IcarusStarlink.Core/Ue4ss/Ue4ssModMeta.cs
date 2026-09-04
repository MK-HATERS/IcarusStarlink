namespace IcarusStarlink.Core.Ue4ss;

/// <summary>The sidecar content for a UE4SS mod's Nexus link — a UE4SS mod carries no metadata file of its own (see IUe4ssModRepository), so this is the only place a folder's Nexus mod ID/version can live.</summary>
public sealed class Ue4ssModMeta
{
    public int? NexusModId { get; set; }
    public string? NexusVersion { get; set; }

    /// <summary>
    /// The minimum UE4SS loader version this mod needs, as the user typed it (e.g. "3.0.1") — set
    /// via the UE4SS tab's own "Set minimum UE4SS version…" prompt. This can only ever be manual: a
    /// UE4SS mod carries no manifest file of its own to read a real requirement from (see this
    /// class's own summary), so there is nothing to scrape or infer it from automatically. Null means
    /// the user hasn't declared one — never treated as a warning. See Ue4ssVersionComparer for how
    /// this gets compared against the installed loader's own Ue4ssLoaderStatus.InstalledVersion.
    /// </summary>
    public string? MinUe4ssVersion { get; set; }
}

/// <summary>Reads/writes per-UE4SS-mod Nexus-link metadata keyed by folder name — mirrors LibraryMetaStore's own shape and reasoning (a separate sidecar directory, not a file dropped inside the mod's own folder).</summary>
public interface IUe4ssModMetaStore
{
    Ue4ssModMeta Load(string folderName);

    void Save(string folderName, Ue4ssModMeta meta);

    void Delete(string folderName);
}
