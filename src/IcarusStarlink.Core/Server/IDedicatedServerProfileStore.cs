namespace IcarusStarlink.Core.Server;

public interface IDedicatedServerProfileStore
{
    IReadOnlyList<DedicatedServerProfile> GetAll();

    void Save(DedicatedServerProfile profile);

    void Delete(Guid id);
}
