namespace IcarusStarlink.Core.Ue4ss;

/// <summary>The sidecar content for a UE4SS mod's Nexus link — a UE4SS mod carries no metadata file of its own (see IUe4ssModRepository), so this is the only place a folder's Nexus mod ID/version can live.</summary>
public sealed class Ue4ssModMeta
{
    public int? NexusModId { get; set; }
    public string? NexusVersion { get; set; }
}

/// <summary>Reads/writes per-UE4SS-mod Nexus-link metadata keyed by folder name — mirrors LibraryMetaStore's own shape and reasoning (a separate sidecar directory, not a file dropped inside the mod's own folder).</summary>
public interface IUe4ssModMetaStore
{
    Ue4ssModMeta Load(string folderName);

    void Save(string folderName, Ue4ssModMeta meta);

    void Delete(string folderName);
}
