namespace IcarusStarlink.Core.Server;

public interface IDedicatedServerProfileStore
{
    IReadOnlyList<DedicatedServerProfile> GetAll();

    /// <summary>
    /// Null when the list loaded normally (or there was no file yet). Otherwise a plain sentence
    /// for the user: the saved list exists but couldn't be read, so changes are not written over it
    /// (unreadable) or the bad file is kept aside before the first save (corrupt).
    /// </summary>
    string? LoadProblem { get; }

    /// <summary>False when nothing was written (see LoadProblem, or the write itself failed).</summary>
    bool Save(DedicatedServerProfile profile);

    bool Delete(Guid id);
}
