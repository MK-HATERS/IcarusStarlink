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
}
