namespace IcarusStarlink.Core.Profiles;

public interface IProfileStore
{
    IReadOnlyList<string> ProfileNames { get; }

    Profile Load(string name);

    /// <summary>Upsert — creates a new profile or overwrites an existing one with the same Name.</summary>
    void Save(Profile profile);

    void Delete(string name);

    /// <summary>Throws if newName already names a different existing profile.</summary>
    void Rename(string oldName, string newName);

    /// <summary>True if this profile has at least one backup to restore — the per-name existence check "Restore latest backup" needs to know whether to enable itself.</summary>
    bool HasBackup(string name);

    /// <summary>
    /// Replaces this profile's current file with its own most recent backup — a real point-in-time
    /// restore, the mirror of ILibraryRepository.RestoreLatestModBackup for a Profile. Returns false
    /// (not an error) only when no backup exists at all for this name.
    /// </summary>
    bool RestoreLatestBackup(string name);
}
