using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.Catalog.Tests.Nexus;

/// <summary>Following one Nexus file through its updates, shaped like Deyvid's All-in-One (mod 317): several variant files side by side, each with its own version.</summary>
public sealed class NexusFileLineageTests
{
    private static NexusModFile File(int id, string name, string version) => new(id, $"{id}.zip", name, version, "OPTIONAL", false);

    private static readonly NexusModFile Vanilla108 = File(100, "Vanilla Weight", "1.0.8");
    private static readonly NexusModFile Vanilla109 = File(101, "Vanilla Weight", "1.0.9");
    private static readonly NexusModFile Vanilla110 = File(102, "Vanilla Weight", "1.1.0");
    private static readonly NexusModFile NoLevel105 = File(200, "75 Weight Without Level Progression", "1.0.5");

    [Fact]
    public void Newest_FollowsTheChainToTheLatestFile()
    {
        var set = new NexusModFileSet([Vanilla108, Vanilla109, Vanilla110, NoLevel105], [new(100, 101), new(101, 102)]);

        Assert.Same(Vanilla110, NexusFileLineage.Newest(set, 100));
        Assert.Same(Vanilla110, NexusFileLineage.Newest(set, 102));
    }

    [Fact]
    public void Newest_AnotherVariantsUpdatesNeverCount()
    {
        var set = new NexusModFileSet([Vanilla108, Vanilla109, NoLevel105], [new(100, 101)]);

        Assert.Same(NoLevel105, NexusFileLineage.Newest(set, 200));
    }

    [Fact]
    public void Newest_ChainEndingAtADeletedFile_StopsAtTheNewestThatStillExists()
    {
        var set = new NexusModFileSet([Vanilla108, Vanilla109], [new(100, 101), new(101, 999)]);

        Assert.Same(Vanilla109, NexusFileLineage.Newest(set, 100));
    }

    [Fact]
    public void Newest_FileGoneFromTheModEntirely_IsNull() =>
        Assert.Null(NexusFileLineage.Newest(new NexusModFileSet([NoLevel105], []), 100));

    [Fact]
    public void Newest_ACycleInTheUpdateList_StillEnds()
    {
        var set = new NexusModFileSet([Vanilla108, Vanilla109], [new(100, 101), new(101, 100)]);

        Assert.Same(Vanilla109, NexusFileLineage.Newest(set, 100));
    }
}
